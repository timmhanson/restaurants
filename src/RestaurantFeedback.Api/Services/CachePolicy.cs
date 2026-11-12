using System.Security.Cryptography;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace RestaurantFeedback.Api.Services;

/// <summary>
/// Browsers always fetch the live page: index.html and API responses are never cached, while the hashed files
/// under /assets (whose names change with every build) are cached for a year. Every API response also carries
/// the build's version so an open tab can tell a new version was deployed and reload itself.
/// </summary>
public static class CachePolicy
{
    public const string VersionHeader = "X-App-Version";

    public static string AppVersion(IWebHostEnvironment env)
    {
        var index = env.WebRootFileProvider.GetFileInfo("index.html");
        if (!index.Exists) return "dev";
        using var stream = index.CreateReadStream();
        return Convert.ToHexString(SHA256.HashData(stream))[..12].ToLowerInvariant();
    }

    public static StaticFileOptions StaticFiles() => new()
    {
        OnPrepareResponse = ctx =>
        {
            var headers = ctx.Context.Response.Headers;
            if (ctx.Context.Request.Path.StartsWithSegments("/assets"))
            {
                headers[HeaderNames.CacheControl] = "public, max-age=31536000, immutable";
            }
            else
            {
                NoStore(headers);
            }
        }
    };

    public const string SessionHeader = "X-Session-Id";
    public const string OutdatedMessage = "This page is an old copy of the site. Reload it to get the current version, then sign in again. (Mac: Cmd+Shift+R. Windows: Ctrl+F5.)";
    private static readonly string[] OpenToAnyone = ["/api/health", "/api/info"];

    /// <summary>Every current copy of the site sends X-Session-Id; a caller without it is a stale cached copy.</summary>
    public static bool IsOutdatedClient(HttpRequest request) =>
        request.Path.StartsWithSegments("/api") && !request.Headers.ContainsKey(SessionHeader)
        && !OpenToAnyone.Any(p => request.Path.Equals(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Refuses API calls from stale copies of the site with a message their own error display will show, so nobody
    /// keeps working in an out-of-date screen (wrong menus, missing roles) without knowing. Runs after request
    /// logging so the attempts appear in the interaction log.
    /// </summary>
    public static IApplicationBuilder UseCurrentClientOnly(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        if (IsOutdatedClient(ctx.Request))
        {
            ctx.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            await ctx.Response.WriteAsJsonAsync(new { code = "OutdatedClient", message = OutdatedMessage });
            return;
        }
        await next();
    });

    public static void NoStore(IHeaderDictionary headers)
    {
        headers[HeaderNames.CacheControl] = "no-store, no-cache, must-revalidate, max-age=0";
        headers[HeaderNames.Pragma] = "no-cache";
        headers[HeaderNames.Expires] = "0";
    }

    /// <summary>No caching for any API response, and the version header on each.</summary>
    public static IApplicationBuilder UseApiNoStore(this IApplicationBuilder app, string version) => app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            // Copies of the site from before pages were sent no-store may still sit in a browser's cache. They are
            // the only callers that don't send X-Session-Id, so tell those browsers to drop their cached files;
            // the next load then gets the live version.
            var outdatedClient = IsOutdatedClient(ctx.Request);
            ctx.Response.OnStarting(() =>
            {
                if (outdatedClient) ctx.Response.Headers["Clear-Site-Data"] = "\"cache\"";
                NoStore(ctx.Response.Headers);
                ctx.Response.Headers[VersionHeader] = version;
                ctx.Response.Headers[HeaderNames.AccessControlExposeHeaders] = VersionHeader;
                return Task.CompletedTask;
            });
        }
        await next();
    });
}

/// <summary>Facts about this running copy of the site, for the System status page.</summary>
public class AppInfo
{
    public string Version { get; set; } = "dev";
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public string ConnectionString { get; init; } = "";

    /// <summary>The SQLite file and its size including the write-ahead log, or null for another database.</summary>
    public (string File, long Bytes)? Database()
    {
        if (!ConnectionString.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase)) return null;
        var path = ConnectionString["Data Source=".Length..].Split(';')[0];
        long size = 0;
        foreach (var f in new[] { path, path + "-wal" })
            if (File.Exists(f)) size += new FileInfo(f).Length;
        return (Path.GetFileName(path), size);
    }
}
