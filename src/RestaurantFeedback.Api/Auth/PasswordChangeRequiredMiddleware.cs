namespace RestaurantFeedback.Api.Auth;

/// <summary>
/// Once an admin has reset a user's password, the user may only sign in, look at their own profile and
/// pick a new password until they do so.
/// </summary>
public class PasswordChangeRequiredMiddleware(RequestDelegate next)
{
    private static readonly string[] Allowed = ["/api/auth/login", "/api/auth/me", "/api/auth/change-password", "/api/auth/change-password/request", "/api/health", "/api/info", "/api/activity"];

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            && ctx.User.Identity?.IsAuthenticated == true
            && ctx.User.FindFirst("pwc")?.Value == "1"
            && !Allowed.Any(a => path.Equals(a, StringComparison.OrdinalIgnoreCase)))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { code = "PasswordChangeRequired", message = "You must change your password before continuing." });
            return;
        }
        await next(ctx);
    }
}
