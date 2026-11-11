using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Auth;

/// <summary>The signed-in user as the API sees them for this request.</summary>
public class CurrentUser
{
    public int Id { get; private init; }
    public string Username { get; private init; } = "";
    public string DisplayName { get; private init; } = "";
    public UserRole Role { get; private init; }
    /// <summary>The group the user belongs to, or the group a super admin is currently administering (null = none selected).</summary>
    public int? GroupId { get; private init; }
    public ManagerPermission Permissions { get; private init; }
    /// <summary>Restaurants the user belongs to (for a super admin: every restaurant in the selected group).</summary>
    public IReadOnlySet<int> RestaurantIds { get; private init; } = new HashSet<int>();
    public bool IsAuthenticated { get; private init; }
    public bool MustChangePassword { get; private init; }

    /// <summary>Claims-only view. Used where the database copy is not needed (login, password change).</summary>
    public CurrentUser(ClaimsPrincipal principal)
    {
        IsAuthenticated = principal.Identity?.IsAuthenticated == true;
        Id = int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;
        Username = principal.FindFirstValue(ClaimTypes.Name) ?? "";
        DisplayName = principal.FindFirstValue("name") ?? Username;
        Role = Enum.TryParse<UserRole>(principal.FindFirstValue(ClaimTypes.Role), out var r) ? r : UserRole.TeamMember;
        GroupId = int.TryParse(principal.FindFirstValue("gid"), out var g) ? g : null;
        Permissions = RoleTitles.HasAllPermissions(Role)
            ? ManagerPermission.All
            : (ManagerPermission)(int.TryParse(principal.FindFirstValue("perm"), out var p) ? p : 0);
        RestaurantIds = (principal.FindFirstValue("rids") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse).ToHashSet();
        MustChangePassword = principal.FindFirstValue("pwc") == "1";
    }

    /// <summary>Live view built from the database, so role, permission and restaurant changes apply on the next request.</summary>
    public CurrentUser(AppUser user, int? groupId, IEnumerable<int> restaurantIds)
    {
        IsAuthenticated = true;
        Id = user.Id;
        Username = user.Username;
        DisplayName = user.DisplayName;
        Role = user.Role;
        GroupId = groupId;
        Permissions = RoleTitles.HasAllPermissions(user.Role) ? ManagerPermission.All : user.Permissions;
        RestaurantIds = restaurantIds.ToHashSet();
        MustChangePassword = user.MustChangePassword;
    }

    public bool IsSuperAdmin => Role == UserRole.SuperAdmin;
    public bool IsGroupAdmin => Role == UserRole.GroupAdmin;
    /// <summary>System and investment group administrators: everything in the group, whoever owns each restaurant.</summary>
    public bool IsGroupWide => Role is UserRole.SuperAdmin or UserRole.GroupAdmin;
    public bool IsOwner => Role == UserRole.Owner;
    /// <summary>Administrators and owners: the people allowed to manage users and restaurants (owners only their own).</summary>
    public bool IsAdmin => Role is UserRole.Owner or UserRole.SuperAdmin or UserRole.GroupAdmin;
    public bool IsTeamMember => Role == UserRole.TeamMember;
    public bool Has(ManagerPermission p) => IsAdmin || (Role == UserRole.Manager && Permissions.HasFlag(p));
    public bool CanAccessRestaurant(int restaurantId) => RestaurantIds.Contains(restaurantId);

    /// <summary>
    /// Restaurants whose details and staffing this user may change: every restaurant in the group for system and
    /// investment group administrators, the restaurants they own for an owner, nothing for anyone else.
    /// </summary>
    public bool CanManageRestaurant(int restaurantId) => IsAdmin && RestaurantIds.Contains(restaurantId);
}

/// <summary>
/// Resolves the current user once per request from the database (not just the token), so that
/// deactivation, permission changes and restaurant assignments take effect immediately.
/// Super admins pick the investment group they are working in with the X-Group-Id header (or ?groupId=).
/// </summary>
public class CurrentUserProvider(IHttpContextAccessor http, AppDbContext db)
{
    public const string GroupHeader = "X-Group-Id";
    private CurrentUser? _cached;

    public async Task<CurrentUser> GetAsync()
    {
        if (_cached is not null) return _cached;
        var ctx = http.HttpContext ?? throw new InvalidOperationException("No HTTP context.");
        var claims = new CurrentUser(ctx.User);
        var user = await db.Users.AsNoTracking().Include(u => u.UserRestaurants).FirstOrDefaultAsync(u => u.Id == claims.Id);
        if (user is null || !user.IsActive) throw new AccountUnavailableException();

        if (user.Role == UserRole.SuperAdmin)
        {
            var raw = ctx.Request.Headers[GroupHeader].FirstOrDefault() ?? ctx.Request.Query["groupId"].FirstOrDefault();
            int? gid = int.TryParse(raw, out var g) ? g : null;
            var rids = gid is null ? [] : await db.Restaurants.Where(r => r.GroupId == gid).Select(r => r.Id).ToListAsync();
            return _cached = new CurrentUser(user, gid, rids);
        }
        if (user.Role == UserRole.GroupAdmin)
        {
            var rids = await db.Restaurants.Where(r => r.GroupId == user.GroupId).Select(r => r.Id).ToListAsync();
            return _cached = new CurrentUser(user, user.GroupId, rids);
        }
        return _cached = new CurrentUser(user, user.GroupId, user.UserRestaurants.Select(x => x.RestaurantId));
    }
}

/// <summary>Thrown when a token belongs to a user who has since been removed or deactivated.</summary>
public class AccountUnavailableException : Exception;

public class AccountUnavailableMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await next(ctx); }
        catch (AccountUnavailableException) when (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { message = "Your account is no longer active. Please sign in again." });
        }
    }
}
