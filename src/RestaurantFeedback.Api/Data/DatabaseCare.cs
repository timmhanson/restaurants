using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Data;

/// <summary>What the last start-up check of the database found. Read by the System Administrator's Overview.</summary>
public class DatabaseHealth
{
    /// <summary>Damage the site could not put right by itself, in the database's own words. Null when the database is sound.</summary>
    public string? Problem { get; set; }
    /// <summary>What was repaired at the last start, if anything.</summary>
    public string? Repaired { get; set; }
    public DateTime? CheckedAt { get; set; }
}

/// <summary>
/// Checks the SQLite database when the site starts and rebuilds its indexes if they are out of step with the
/// tables. That can happen when two copies of the site write to the file at once (the file lives on a network
/// share, where file locking is not dependable), as on 2026-11-19 when a deploy replaced the site's files while
/// it was running. An index is only a look-up list made from a table, so rebuilding it loses nothing. Damage to
/// the tables themselves is never "repaired" here: it is reported, and shown on the Overview.
/// </summary>
public static class DatabaseCare
{
    public record Result(bool Sound, bool Rebuilt, List<string> Found, List<string> Remaining);

    /// <summary>True for the two things the database says when an index and its table disagree.</summary>
    public static bool IsIndexOnly(string line) =>
        line.Contains("missing from index", StringComparison.OrdinalIgnoreCase) || line.Contains("wrong # of entries in index", StringComparison.OrdinalIgnoreCase);

    public static async Task<List<string>> CheckAsync(DbConnection connection, CancellationToken ct = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        var lines = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) lines.Add(reader.GetString(0));
        return lines is ["ok"] ? [] : lines;
    }

    /// <summary>Checks the database; when the only damage is to indexes, rebuilds them and checks again.</summary>
    public static async Task<Result> CheckAndRepairAsync(DbConnection connection, CancellationToken ct = default)
    {
        var found = await CheckAsync(connection, ct);
        if (found.Count == 0) return new Result(true, false, found, []);
        if (!found.All(IsIndexOnly)) return new Result(false, false, found, found);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "REINDEX;";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var remaining = await CheckAsync(connection, ct);
        return new Result(remaining.Count == 0, true, found, remaining);
    }

    /// <summary>Run once at start-up, after migrations. Never stops the site from starting.</summary>
    public static async Task RunAsync(AppDbContext db, DatabaseHealth health, ILogger log, CancellationToken ct = default)
    {
        if (!db.Database.IsSqlite()) return;
        try
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open) await db.Database.OpenConnectionAsync(ct);
            var result = await CheckAndRepairAsync(connection, ct);
            health.CheckedAt = DateTime.UtcNow;
            static string Brief(List<string> lines) => string.Join("; ", lines.Take(6)) + (lines.Count > 6 ? $"; and {lines.Count - 6} more" : "");
            if (result.Rebuilt && result.Sound)
            {
                health.Repaired = Brief(result.Found);
                log.LogWarning("The database's indexes were out of step with its tables and have been rebuilt: {Found}", health.Repaired);
                db.AuditLogs.Add(new AuditLog { UserName = "system", Action = "DatabaseIndexesRebuilt", EntityType = "Database", Details = $"Indexes rebuilt at start-up. Found: {health.Repaired}" });
                await db.SaveChangesAsync(ct);
            }
            else if (!result.Sound)
            {
                health.Problem = Brief(result.Remaining);
                log.LogError("The database reports damage the site cannot repair by itself: {Problem}", health.Problem);
            }
        }
        catch (Exception e)
        {
            log.LogError(e, "The start-up check of the database could not be completed.");
        }
    }
}
