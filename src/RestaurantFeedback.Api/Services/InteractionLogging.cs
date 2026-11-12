using System.Diagnostics;
using System.Security.Claims;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Auth;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Services;

public class InteractionLogOptions
{
    /// <summary>Rows older than this are deleted once a day.</summary>
    public int RetentionDays { get; set; } = 730;
}

/// <summary>
/// In-memory hand-off between requests and <see cref="InteractionLogWriter"/>, so logging never slows a request.
/// Bounded: under a flood the oldest unsaved rows are dropped rather than blocking users.
/// </summary>
public class InteractionLogQueue
{
    private readonly Channel<InteractionLog> _channel = Channel.CreateBounded<InteractionLog>(
        new BoundedChannelOptions(20_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public void Enqueue(InteractionLog row) => _channel.Writer.TryWrite(row);
    public ChannelReader<InteractionLog> Reader => _channel.Reader;

    /// <summary>Who is making this request, read from the token (no database call), plus where they are.</summary>
    public static InteractionLog Describe(HttpContext ctx, string kind)
    {
        var user = ctx.User;
        var signedIn = user.Identity?.IsAuthenticated == true;
        UserRole? role = signedIn && Enum.TryParse<UserRole>(user.FindFirstValue(ClaimTypes.Role), out var r) ? r : null;
        int? groupId = int.TryParse(user.FindFirstValue("gid"), out var g) ? g : null;
        var viewing = false;
        if (role == UserRole.SuperAdmin)
        {
            var raw = ctx.Request.Headers[CurrentUserProvider.GroupHeader].FirstOrDefault() ?? ctx.Request.Query["groupId"].FirstOrDefault();
            groupId = int.TryParse(raw, out var sg) ? sg : null;
            viewing = groupId is not null;
        }
        return new InteractionLog
        {
            Kind = kind,
            UserId = int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null,
            UserName = Clip(user.FindFirstValue(ClaimTypes.Name), 100),
            DisplayName = Clip(user.FindFirstValue("name"), 200),
            Role = role,
            GroupId = groupId,
            RestaurantId = int.TryParse(ctx.Request.Query["restaurantId"].FirstOrDefault(), out var rid) ? rid : null,
            ViewingAs = viewing,
            IpAddress = Clip(ctx.Connection.RemoteIpAddress?.ToString(), 64),
            UserAgent = Clip(ctx.Request.Headers.UserAgent.ToString(), 300),
            SessionId = Clip(ctx.Request.Headers["X-Session-Id"].FirstOrDefault(), 64)
        };
    }

    public static string? Clip(string? s, int max) => string.IsNullOrEmpty(s) ? null : s.Length <= max ? s : s[..max];
}

/// <summary>Keys controllers (via <see cref="AuditService"/>) use to tell the request log what a call did.</summary>
public static class InteractionContext
{
    public const string Action = "log.action";
    public const string RestaurantId = "log.restaurantId";
    public const string GroupId = "log.groupId";
    public const string User = "log.user";

    /// <summary>Called for every audit row, so the request that caused it carries the same action, restaurant and group.</summary>
    public static void Note(HttpContext? ctx, string action, int? restaurantId, int? groupId, (int? Id, string Name, UserRole? Role)? user = null)
    {
        if (ctx is null) return;
        ctx.Items[Action] = ctx.Items.TryGetValue(Action, out var existing) && existing is string s && s != action ? $"{s}, {action}" : action;
        if (restaurantId is not null) ctx.Items[RestaurantId] = restaurantId;
        if (groupId is not null) ctx.Items[GroupId] = groupId;
        if (user is not null) ctx.Items[User] = user;
    }
}

/// <summary>
/// Records every API call: who, which group and restaurant, what it was, the result and how long it took.
/// Request and response bodies are never stored. Future endpoints are covered automatically.
/// </summary>
public class InteractionLoggingMiddleware(RequestDelegate next, InteractionLogQueue queue)
{
    // Not user interactions: health probes, and the browser's own event batches (stored as their events).
    private static readonly string[] Skip = ["/api/health", "/api/info", "/api/activity"];

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || Skip.Any(s => path.Equals(s, StringComparison.OrdinalIgnoreCase)))
        {
            await next(ctx);
            return;
        }
        var clock = Stopwatch.StartNew();
        var failed = false;
        try { await next(ctx); }
        catch (AccountUnavailableException) { failed = true; ctx.Items["log.status"] = 401; throw; }
        catch (Exception ex)
        {
            failed = true;
            // The error type and message only (never request data), so the System Administrator can see what broke.
            ctx.Items["log.error"] = $"{ex.GetType().Name}: {(ex.InnerException ?? ex).Message}";
            throw;
        }
        finally
        {
            var row = InteractionLogQueue.Describe(ctx, InteractionKinds.Api);
            row.Method = ctx.Request.Method;
            row.Path = InteractionLogQueue.Clip(path + ctx.Request.QueryString.Value, 400)!;
            row.Status = failed ? (ctx.Items.TryGetValue("log.status", out var st) && st is int code ? code : 500) : ctx.Response.StatusCode;
            row.DurationMs = (int)Math.Min(int.MaxValue, clock.ElapsedMilliseconds);
            if (ctx.Items.TryGetValue(InteractionContext.Action, out var action)) row.Action = InteractionLogQueue.Clip(action as string, 200);
            if (ctx.Items.TryGetValue("log.error", out var error)) row.Detail = InteractionLogQueue.Clip(error as string, 500);
            if (ctx.Items.TryGetValue(InteractionContext.RestaurantId, out var rid) && rid is int r) row.RestaurantId = r;
            if (row.GroupId is null && ctx.Items.TryGetValue(InteractionContext.GroupId, out var gid) && gid is int g) row.GroupId = g;
            // Sign-ins happen before there is a token, so take who it was from the audit.
            if (row.UserName is null && ctx.Items.TryGetValue(InteractionContext.User, out var u) && u is ValueTuple<int?, string, UserRole?> who)
            {
                row.UserId = who.Item1;
                row.UserName = InteractionLogQueue.Clip(who.Item2, 100);
                row.Role = who.Item3;
            }
            queue.Enqueue(row);
        }
    }
}

/// <summary>Saves queued interaction rows in batches and trims rows past the retention period once a day.</summary>
public class InteractionLogWriter(InteractionLogQueue queue, IServiceScopeFactory scopes, InteractionLogOptions options, ILogger<InteractionLogWriter> log) : BackgroundService
{
    private DateTime _lastTrim = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<InteractionLog>(200);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await queue.Reader.WaitToReadAsync(stoppingToken)) break;
                // Give a burst a moment to collect so it is written in one transaction.
                await Task.Delay(250, stoppingToken);
                while (batch.Count < 500 && queue.Reader.TryRead(out var row)) batch.Add(row);
                await SaveAsync(batch, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogError(ex, "Could not save {Count} interaction log rows", batch.Count);
            }
            finally { batch.Clear(); }
        }
        // Flush what is left on shutdown.
        while (queue.Reader.TryRead(out var row)) batch.Add(row);
        if (batch.Count > 0)
        {
            try { await SaveAsync(batch, CancellationToken.None); } catch (Exception ex) { log.LogError(ex, "Could not save interaction log rows on shutdown"); }
        }
    }

    private async Task SaveAsync(List<InteractionLog> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.InteractionLogs.AddRange(batch);
        await db.SaveChangesAsync(ct);
        if (DateTime.UtcNow - _lastTrim > TimeSpan.FromDays(1))
        {
            _lastTrim = DateTime.UtcNow;
            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(30, options.RetentionDays));
            var removed = await db.InteractionLogs.Where(l => l.At < cutoff).ExecuteDeleteAsync(ct);
            if (removed > 0) log.LogInformation("Removed {Count} interaction log rows older than {Cutoff:d}", removed, cutoff);
        }
    }
}
