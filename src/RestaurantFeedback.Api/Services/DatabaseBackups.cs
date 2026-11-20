using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Identity;
using Azure.Storage.Blobs;
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
    /// <summary>The hour of the day (UTC) after which the night's backup is made. 8 is 2 or 3 in the morning, Central time.</summary>
    public int HourUtc { get; set; } = 8;
    public int KeepOnSite { get; set; } = 7;
    public int KeepDaily { get; set; } = 30;
    public int KeepMonthly { get; set; } = 12;
}

/// <summary>How the last backup went.</summary>
public record BackupState(DateTime At, bool Ok, string? Name, long Bytes, bool OffSite, string? Problem, string By);
public record BackupFile(string Name, DateTime At, long Bytes, bool OnSite, bool OffSite);

/// <summary>
/// Copies of the site's database, made every night and whenever the System Administrator asks.
///
/// A copy is made with SQLite's own online backup, so it is consistent even while the site is in use (it includes
/// what is still in the write-ahead log). The copy is checked, compressed, kept on the site's own disk and sent to
/// the site's Azure Storage account, which is a different service from the disk the database lives on. The last
/// week is kept on the disk; in storage, the last 30 days and the first copy of each month for a year.
///
/// The encryption key ring is deliberately not copied: it unlocks the saved keys of the outside services, and
/// should not sit beside copies of the data it protects. A database restored without it works, and those keys are
/// entered again on Connected Systems.
/// </summary>
public class DatabaseBackups(AppDbContext db, StorageOptions storage, BackupOptions options, AuditService audit, ILogger<DatabaseBackups> log)
{
    private const string StateKey = "backup.state";
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

    /// <summary>The night's backup is due once the hour has passed and no good copy has been made since.</summary>
    public async Task<bool> DueAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        if (!options.Enabled) return false;
        var tonight = nowUtc.Date.AddHours(Math.Clamp(options.HourUtc, 0, 23));
        if (nowUtc < tonight) tonight = tonight.AddDays(-1);
        var last = await StateAsync(ct);
        // After a failure it is tried again, but not more than once an hour.
        if (last is { Ok: false } && nowUtc - last.At < TimeSpan.FromHours(1)) return false;
        return last is not { Ok: true } || last.At < tonight;
    }

    /// <summary>Makes a copy now. One at a time: a second request waits for the first.</summary>
    public async Task<BackupState> RunAsync(string by, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            var name = $"restaurant-feedback-{now:yyyyMMdd-HHmmss}.db.gz";
            BackupState state;
            try
            {
                Directory.CreateDirectory(Folder);
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
                        await PruneOffSiteAsync(container, ct);
                    }
                    catch (Exception ex) when (ex is Azure.RequestFailedException or IOException or Azure.Identity.AuthenticationFailedException)
                    {
                        log.LogError(ex, "The database copy could not be sent to storage");
                        problem = $"The copy was made and is on the site's own disk, but it could not be sent to Azure Storage: {ex.Message.Split('\n')[0]}";
                    }
                }
                PruneOnSite();
                state = new BackupState(now, problem is null, name, bytes, offSite, problem, by);
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                log.LogError(ex, "The database could not be backed up");
                state = new BackupState(now, false, null, 0, false, $"No copy could be made: {ex.Message.Split('\n')[0]}", by);
            }
            await SaveStateAsync(state, ct);
            await audit.LogRawAsync(null, by, null, null, state.Ok ? "DatabaseBackedUp" : "DatabaseBackupFailed", "Database", null,
                new { state.Name, state.Bytes, state.OffSite, state.Problem });
            return state;
        }
        finally { Gate.Release(); }
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
            if (File.Exists(raw)) File.Delete(raw);
        }
    }

    private void PruneOnSite()
    {
        var keep = Math.Max(1, options.KeepOnSite);
        foreach (var old in new DirectoryInfo(Folder).GetFiles("restaurant-feedback-*.db.gz").Where(f => IsBackupName(f.Name)).OrderByDescending(f => f.Name).Skip(keep))
            old.Delete();
    }

    /// <summary>Which copies are kept in storage: the newest ones, and the first of each month for a year.</summary>
    public static HashSet<string> Kept(IEnumerable<string> names, int daily, int monthly)
    {
        var all = names.Where(IsBackupName).OrderByDescending(n => n).ToList();
        var keep = all.Take(Math.Max(1, daily)).ToHashSet();
        foreach (var first in all.GroupBy(n => n.Substring("restaurant-feedback-".Length, 6)).OrderByDescending(g => g.Key).Take(Math.Max(0, monthly)).Select(g => g.Min()!))
            keep.Add(first);
        return keep;
    }

    private async Task PruneOffSiteAsync(BlobContainerClient container, CancellationToken ct)
    {
        var names = new List<string>();
        await foreach (var blob in container.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "restaurant-feedback-", ct)) names.Add(blob.Name);
        var keep = Kept(names, options.KeepDaily, options.KeepMonthly);
        foreach (var name in names.Where(n => IsBackupName(n) && !keep.Contains(n)))
            await container.DeleteBlobIfExistsAsync(name, cancellationToken: ct);
    }

    private async Task SaveStateAsync(BackupState state, CancellationToken ct)
    {
        var row = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == StateKey, ct);
        if (row is null) db.SiteSettings.Add(row = new SiteSetting { Key = StateKey });
        row.Value = JsonSerializer.Serialize(state, Json);
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = state.By;
        await db.SaveChangesAsync(ct);
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
                await foreach (var blob in container.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "restaurant-feedback-", ct))
                    if (StampOf(blob.Name) is { } at)
                        found[blob.Name] = found.TryGetValue(blob.Name, out var here) ? here with { OffSite = true } : new BackupFile(blob.Name, at, blob.Properties.ContentLength ?? 0, false, true);
            }
            catch (Exception ex) when (ex is Azure.RequestFailedException or Azure.Identity.AuthenticationFailedException)
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

/// <summary>Makes the night's backup. Looks every half hour, so a restart or a failure only delays it.</summary>
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
                if (await backups.DueAsync(DateTime.UtcNow, stoppingToken)) await backups.RunAsync("Nightly backup", stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { log.LogError(ex, "The nightly backup round failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
