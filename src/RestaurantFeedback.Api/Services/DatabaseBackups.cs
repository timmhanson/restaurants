using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Services;

public class BackupOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Where copies are kept on the site's own disk. Default: a "backups" folder beside the database.</summary>
    public string? Folder { get; set; }
    /// <summary>The container in the site's storage account that copies are also sent to.</summary>
    public string ContainerName { get; set; } = "database-backups";
    /// <summary>How often a copy is made.</summary>
    public int EveryMinutes { get; set; } = 60;
    /// <summary>Every copy is kept for this long (the rolling hourly copies).</summary>
    public int KeepHourlyHours { get; set; } = 48;
    /// <summary>After that, the first copy of each day is kept for this many days (three months).</summary>
    public int KeepDailyDays { get; set; } = 92;
    /// <summary>On the site's own disk, the daily copies are kept for this many days; storage keeps the rest.</summary>
    public int OnSiteDays { get; set; } = 7;
}

/// <summary>How the last backup went.</summary>
public record BackupState(DateTime At, bool Ok, string? Name, long Bytes, bool OffSite, string? Problem, string By);
public record BackupFile(string Name, DateTime At, long Bytes, bool OnSite, bool OffSite);

/// <summary>
/// Copies of the site's database: one every hour, one every time the site starts (which is what a deploy does,
/// and it is made before the new version changes anything in the database), and one whenever the System
/// Administrator asks.
///
/// A copy is made with SQLite's own online backup, so it is consistent even while the site is in use (it includes
/// what is still in the write-ahead log). The copy is checked, compressed, kept on the site's own disk and sent to
/// the site's Azure Storage account, which is a different service from the disk the database lives on.
///
/// What is kept: every copy for two days; after that the first copy of each day, for three months; after that
/// the first copy of each month, always. The site's disk keeps the two days and a week of daily copies.
///
/// The encryption key ring is deliberately not copied: it unlocks the saved keys of the outside services, and
/// should not sit beside copies of the data it protects. A database restored without it works, and those keys are
/// entered again on Connected Systems.
/// </summary>
public class DatabaseBackups(AppDbContext db, StorageOptions storage, BackupOptions options, AuditService audit, ILogger<DatabaseBackups> log)
{
    private const string StateKey = "backup.state";
    public const string BeforeStart = "Before the site started (a deploy, or a restart)";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Regex NamePattern = new(@"^restaurant-feedback-(\d{8}-\d{6})\.db\.gz$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string DatabasePath => Path.GetFullPath(new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource);
    public string Folder => options.Folder ?? Path.Combine(Path.GetDirectoryName(DatabasePath) ?? ".", "backups");
    public bool OffSiteConfigured => !string.IsNullOrWhiteSpace(storage.ConnectionString) || !string.IsNullOrWhiteSpace(storage.AccountUrl);
    public BackupOptions Options => options;

    private BlobContainerClient? Container() =>
        !OffSiteConfigured ? null
        : (!string.IsNullOrWhiteSpace(storage.ConnectionString) ? new BlobServiceClient(storage.ConnectionString) : new BlobServiceClient(new Uri(storage.AccountUrl!), new DefaultAzureCredential()))
            .GetBlobContainerClient(options.ContainerName);

    public static bool IsBackupName(string name) => NamePattern.IsMatch(name);
    private static DateTime? StampOf(string name) =>
        NamePattern.Match(name) is { Success: true } m && DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

    public async Task<BackupState?> StateAsync(CancellationToken ct = default)
    {
        var row = await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == StateKey, ct);
        try { return row is null ? null : JsonSerializer.Deserialize<BackupState>(row.Value, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>The hour's backup is due once the last good copy is an hour old. After a failure it is tried again ten minutes later.</summary>
    public async Task<bool> DueAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        if (!options.Enabled) return false;
        var last = await StateAsync(ct);
        if (last is null) return true;
        return nowUtc - last.At >= (last.Ok ? TimeSpan.FromMinutes(Math.Max(5, options.EveryMinutes) - 2) : TimeSpan.FromMinutes(10));
    }

    /// <summary>Makes a copy now and records how it went. One at a time: a second request waits for the first.</summary>
    public async Task<BackupState> RunAsync(string by, CancellationToken ct = default)
    {
        var state = await MakeAsync(by, ct);
        await RecordAsync(state, ct);
        return state;
    }

    /// <summary>
    /// Whether the database file is there to be copied. On a brand-new site there is nothing yet.
    /// </summary>
    public bool HasDatabase => File.Exists(DatabasePath) && new FileInfo(DatabasePath).Length > 0;

    /// <summary>
    /// Makes a copy without reading or writing any of the site's own tables, so it can be made before the
    /// database is brought up to date by a new version. Record it afterwards with <see cref="RecordAsync"/>.
    /// </summary>
    public async Task<BackupState> MakeAsync(string by, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            var name = $"restaurant-feedback-{now:yyyyMMdd-HHmmss}.db.gz";
            try
            {
                Directory.CreateDirectory(Folder);
                // Anything a copy that was cut short left behind.
                foreach (var left in Directory.GetFiles(Folder, "*.tmp*")) File.Delete(left);
                var file = Path.Combine(Folder, name);
                await CopyAsync(file, ct);
                var bytes = new FileInfo(file).Length;
                string? problem = null;
                var offSite = false;
                if (Container() is { } container)
                {
                    try
                    {
                        await container.CreateIfNotExistsAsync(cancellationToken: ct);
                        await using var stream = File.OpenRead(file);
                        await container.GetBlobClient(name).UploadAsync(stream, overwrite: true, ct);
                        offSite = true;
                        await PruneOffSiteAsync(container, now, ct);
                    }
                    catch (Exception ex) when (ex is Azure.RequestFailedException or IOException or AuthenticationFailedException or OperationCanceledException)
                    {
                        log.LogError(ex, "The database copy could not be sent to storage");
                        problem = $"The copy was made and is on the site's own disk, but it could not be sent to Azure Storage: {ex.Message.Split('\n')[0]}";
                    }
                }
                PruneOnSite(now);
                return new BackupState(now, problem is null, name, bytes, offSite, problem, by);
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
            {
                log.LogError(ex, "The database could not be backed up");
                return new BackupState(now, false, null, 0, false, $"No copy could be made: {ex.Message.Split('\n')[0]}", by);
            }
        }
        finally { Gate.Release(); }
    }

    /// <summary>Notes how a backup went, for System Status and the Overview, with an audit row.</summary>
    public async Task RecordAsync(BackupState state, CancellationToken ct = default)
    {
        var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == StateKey, ct);
        if (row is null) db.SiteSettings.Add(row = new SiteSetting { Key = StateKey });
        row.Value = JsonSerializer.Serialize(state, Json);
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = state.By;
        await db.SaveChangesAsync(ct);
        await audit.LogRawAsync(null, state.By, null, null, state.Ok ? "DatabaseBackedUp" : "DatabaseBackupFailed", "Database", null,
            new { state.Name, state.Bytes, state.OffSite, state.Problem });
    }

    /// <summary>A consistent, checked, compressed copy of the database at <paramref name="file"/>.</summary>
    private async Task CopyAsync(string file, CancellationToken ct)
    {
        var raw = file + ".tmp";
        try
        {
            // The copy's own connections are not pooled, so its file is let go of as soon as each is closed.
            await using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            await using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = raw, Pooling = false }.ToString()))
            {
                await source.OpenAsync(ct);
                await target.OpenAsync(ct);
                source.BackupDatabase(target);
                // The copy is one file that stands on its own: no write-ahead log beside it.
                await using var single = target.CreateCommand();
                single.CommandText = "PRAGMA journal_mode=DELETE;";
                await single.ExecuteScalarAsync(ct);
            }
            await using (var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = raw, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                await check.OpenAsync(ct);
                var found = await DatabaseCare.CheckAsync(check, ct);
                if (found.Count > 0) throw new InvalidOperationException($"the copy did not pass its check ({found[0]})");
            }
            await using (var from = File.OpenRead(raw))
            await using (var to = File.Create(file))
            await using (var zip = new GZipStream(to, CompressionLevel.Optimal))
                await from.CopyToAsync(zip, ct);
        }
        finally
        {
            foreach (var left in new[] { raw, raw + "-wal", raw + "-shm" })
                if (File.Exists(left)) File.Delete(left);
        }
    }

    /// <summary>
    /// Which copies are kept: every copy from the last <paramref name="hourlyHours"/> hours; before that the first
    /// copy of each day, for <paramref name="dailyDays"/> days; before that the first copy of each month, always
    /// (unless <paramref name="monthly"/> is off, as it is for the site's own disk).
    /// </summary>
    public static HashSet<string> Kept(IEnumerable<string> names, DateTime nowUtc, int hourlyHours, int dailyDays, bool monthly = true)
    {
        var all = names.Select(n => (Name: n, At: StampOf(n))).Where(x => x.At is not null).Select(x => (x.Name, At: x.At!.Value)).OrderBy(x => x.At).ToList();
        var keep = new HashSet<string>();
        foreach (var x in all.Where(x => x.At >= nowUtc.AddHours(-hourlyHours))) keep.Add(x.Name);
        foreach (var day in all.Where(x => x.At >= nowUtc.AddDays(-dailyDays)).GroupBy(x => x.At.Date)) keep.Add(day.First().Name);
        if (monthly)
            foreach (var month in all.GroupBy(x => (x.At.Year, x.At.Month))) keep.Add(month.First().Name);
        // The newest copy is never removed, whatever the clock says.
        if (all.Count > 0) keep.Add(all[^1].Name);
        return keep;
    }

    private void PruneOnSite(DateTime now)
    {
        var files = new DirectoryInfo(Folder).GetFiles("restaurant-feedback-*.db.gz").Where(f => IsBackupName(f.Name)).ToList();
        var keep = Kept(files.Select(f => f.Name), now, options.KeepHourlyHours, options.OnSiteDays, monthly: false);
        foreach (var old in files.Where(f => !keep.Contains(f.Name))) old.Delete();
    }

    private async Task PruneOffSiteAsync(BlobContainerClient container, DateTime now, CancellationToken ct)
    {
        var names = new List<string>();
        await foreach (var blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, "restaurant-feedback-", ct)) names.Add(blob.Name);
        var keep = Kept(names, now, options.KeepHourlyHours, options.KeepDailyDays);
        foreach (var name in names.Where(n => IsBackupName(n) && !keep.Contains(n)))
            await container.DeleteBlobIfExistsAsync(name, cancellationToken: ct);
    }

    /// <summary>Every copy there is, newest first, with where each one is.</summary>
    public async Task<List<BackupFile>> ListAsync(CancellationToken ct = default)
    {
        var found = new Dictionary<string, BackupFile>();
        if (Directory.Exists(Folder))
            foreach (var f in new DirectoryInfo(Folder).GetFiles("restaurant-feedback-*.db.gz"))
                if (StampOf(f.Name) is { } at) found[f.Name] = new BackupFile(f.Name, at, f.Length, true, false);
        if (Container() is { } container)
        {
            try
            {
                await foreach (var blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, "restaurant-feedback-", ct))
                    if (StampOf(blob.Name) is { } at)
                        found[blob.Name] = found.TryGetValue(blob.Name, out var here) ? here with { OffSite = true } : new BackupFile(blob.Name, at, blob.Properties.ContentLength ?? 0, false, true);
            }
            catch (Exception ex) when (ex is Azure.RequestFailedException or AuthenticationFailedException)
            {
                // No container yet (nothing has been sent), or storage cannot be reached: the copies on the disk are still listed.
                log.LogWarning("The copies in storage could not be listed: {Message}", ex.Message.Split('\n')[0]);
            }
        }
        return found.Values.OrderByDescending(f => f.Name).ToList();
    }

    /// <summary>Opens a copy for download: from the site's disk when it is there, otherwise from storage.</summary>
    public async Task<Stream?> OpenAsync(string name, CancellationToken ct = default)
    {
        if (!IsBackupName(name)) return null;
        var file = Path.Combine(Folder, name);
        if (File.Exists(file)) return File.OpenRead(file);
        if (Container() is not { } container) return null;
        try { return await container.GetBlobClient(name).OpenReadAsync(cancellationToken: ct); }
        catch (Azure.RequestFailedException) { return null; }
    }
}

/// <summary>Makes the hour's backup. Looks every five minutes, so a restart or a failure only delays it.</summary>
public class BackupWorker(IServiceScopeFactory scopes, BackupOptions options, ILogger<BackupWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var backups = scope.ServiceProvider.GetRequiredService<DatabaseBackups>();
                if (await backups.DueAsync(DateTime.UtcNow, stoppingToken)) await backups.RunAsync("Hourly backup", stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { log.LogError(ex, "The hourly backup round failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
