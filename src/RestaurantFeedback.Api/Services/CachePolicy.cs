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
            ctx.Response.OnStarting(() =>
            {
                NoStore(ctx.Response.Headers);
                ctx.Response.Headers[VersionHeader] = version;
                ctx.Response.Headers[HeaderNames.AccessControlExposeHeaders] = VersionHeader;
                return Task.CompletedTask;
            });
        }
        await next();
    });
}
