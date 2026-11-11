using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Auth;

/// <summary>Strongly typed view of the signed-in user, resolved from JWT claims (plus group scope for super admins).</summary>
public class CurrentUser
{
    public int Id { get; private init; }
    public string Username { get; private init; } = "";
    public string DisplayName { get; private init; } = "";
    public UserRole Role { get; private init; }
    /// <summary>The group the user belongs to, or the group a super admin is currently administering (null = none selected).</summary>
    public int? GroupId { get; private init; }
    public ManagerPermission Permissions { get; private init; }
    public IReadOnlySet<int> RestaurantIds { get; private init; } = new HashSet<int>();
    public bool IsAuthenticated { get; private init; }
    public bool MustChangePassword { get; private init; }

    public CurrentUser(ClaimsPrincipal principal)
    {
        IsAuthenticated = principal.Identity?.IsAuthenticated == true;
        Id = int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : 0;
        Username = principal.FindFirstValue(ClaimTypes.Name) ?? "";
        DisplayName = principal.FindFirstValue("name") ?? Username;
        Role = Enum.TryParse<UserRole>(principal.FindFirstValue(ClaimTypes.Role), out var r) ? r : UserRole.TeamMember;
        GroupId = int.TryParse(principal.FindFirstValue("gid"), out var g) ? g : null;
        Permissions = Role is UserRole.Owner or UserRole.SuperAdmin
            ? ManagerPermission.All
            : (ManagerPermission)(int.TryParse(principal.FindFirstValue("perm"), out var p) ? p : 0);
        RestaurantIds = (principal.FindFirstValue("rids") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse).ToHashSet();
        MustChangePassword = principal.FindFirstValue("pwc") == "1";
    }

    private CurrentUser() { }

    /// <summary>A copy of this (super admin) user scoped to one investment group and its restaurants.</summary>
    public CurrentUser ScopedTo(int? groupId, IEnumerable<int> restaurantIds) => new()
    {
        Id = Id, Username = Username, DisplayName = DisplayName, Role = Role, GroupId = groupId, Permissions = Permissions,
        RestaurantIds = restaurantIds.ToHashSet(), IsAuthenticated = IsAuthenticated, MustChangePassword = MustChangePassword
    };

    public bool IsSuperAdmin => Role == UserRole.SuperAdmin;
    public bool IsOwner => Role == UserRole.Owner;
    /// <summary>Owners and super admins: the people allowed to manage users, restaurants and settings.</summary>
    public bool IsAdmin => Role is UserRole.Owner or UserRole.SuperAdmin;
    public bool IsTeamMember => Role == UserRole.TeamMember;
    public bool Has(ManagerPermission p) => IsAdmin || (Role == UserRole.Manager && Permissions.HasFlag(p));
    public bool CanAccessRestaurant(int restaurantId) => RestaurantIds.Contains(restaurantId);
}

/// <summary>
/// Resolves the current user once per request. Super admins pick the investment group they are
/// working in with the X-Group-Id header (or ?groupId=); that group's restaurants become theirs.
/// </summary>
public class CurrentUserProvider(IHttpContextAccessor http, AppDbContext db)
{
    public const string GroupHeader = "X-Group-Id";
    private CurrentUser? _cached;

    public async Task<CurrentUser> GetAsync()
    {
        if (_cached is not null) return _cached;
        var ctx = http.HttpContext ?? throw new InvalidOperationException("No HTTP context.");
        var user = new CurrentUser(ctx.User);
        if (user.IsSuperAdmin)
        {
            var raw = ctx.Request.Headers[GroupHeader].FirstOrDefault() ?? ctx.Request.Query["groupId"].FirstOrDefault();
            int? gid = int.TryParse(raw, out var g) ? g : null;
            var rids = gid is null ? [] : await db.Restaurants.Where(r => r.GroupId == gid).Select(r => r.Id).ToListAsync();
            user = user.ScopedTo(gid, rids);
        }
        return _cached = user;
    }
}
