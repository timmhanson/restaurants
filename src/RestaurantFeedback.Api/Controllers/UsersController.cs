using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Auth;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Models;
using RestaurantFeedback.Api.Services;

namespace RestaurantFeedback.Api.Controllers;

/// <summary>
/// User management.
///  - Super admins manage every user in every group (and other super admins).
///  - Owners manage every user in their own group.
///  - Managers holding ManageTeamLogin may only create / update / reset the generic
///    team-member login for restaurants they belong to.
/// Any password set by someone else forces a change at next login, except for team logins.
/// </summary>
[ApiController]
[Route("api/users")]
[Authorize]
public class UsersController(AppDbContext db, IPasswordHasher<AppUser> hasher, AuditService audit, CurrentUserProvider users) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<UserDto>>> List()
    {
        var me = await users.GetAsync();
        if (me.IsTeamMember) return Forbid();
        var q = db.Users.Include(u => u.UserRestaurants).Include(u => u.Group).AsQueryable();
        if (me.IsSuperAdmin)
            q = me.GroupId is null ? q : q.Where(u => u.GroupId == me.GroupId || u.Role == UserRole.SuperAdmin);
        else if (me.IsOwner)
            q = q.Where(u => u.GroupId == me.GroupId);
        else
        {
            if (!me.Has(ManagerPermission.ManageTeamLogin)) return Forbid();
            var mine = me.RestaurantIds.ToList();
            q = q.Where(u => u.GroupId == me.GroupId && u.Role == UserRole.TeamMember && u.UserRestaurants.Any(ur => mine.Contains(ur.RestaurantId)));
        }
        var list = await q.Where(u => !u.Username.Contains("#removed-")).OrderBy(u => u.Role).ThenBy(u => u.DisplayName).ToListAsync();
        return list.Select(UserDto.From).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<UserDto>> Create(UserRequest req)
    {
        var me = await users.GetAsync();
        if (!Enum.TryParse<UserRole>(req.Role, true, out var role)) return BadRequest(new { message = "Unknown role." });
        if (!CanManage(me, role, req.RestaurantIds, out var why)) return StatusCode(403, new { message = why });
        if (string.IsNullOrWhiteSpace(req.Password)) return BadRequest(new { message = "Password is required." });
        if (role != UserRole.SuperAdmin && me.GroupId is null) return BadRequest(new { message = "Select an investment group first." });

        var username = req.Username.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Username == username)) return Conflict(new { message = "That username is already taken." });
        var groupId = role == UserRole.SuperAdmin ? null : me.GroupId;
        var rids = groupId is null ? [] : await ValidRestaurantIds(groupId.Value, req.RestaurantIds);
        if (me.IsOwner && rids.Any(r => !me.CanManageRestaurant(r)))
            return StatusCode(403, new { message = await NotOwnedMessage(rids.Where(r => !me.CanManageRestaurant(r))) });

        var user = new AppUser
        {
            GroupId = groupId,
            Username = username,
            DisplayName = req.DisplayName.Trim(),
            Email = req.Email?.Trim(),
            Role = role,
            Permissions = role == UserRole.Manager ? ParsePermissions(req.Permissions) : ManagerPermission.None,
            IsActive = req.IsActive,
            MustChangePassword = role != UserRole.TeamMember,
            UserRestaurants = rids.Select(r => new UserRestaurant { RestaurantId = r }).ToList()
        };
        user.PasswordHash = hasher.HashPassword(user, req.Password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await audit.LogAsync(me, "UserCreated", "User", user.Id,
            new { user.Username, Role = role.ToString(), Permissions = UserDto.PermissionNamesOf(user.Permissions), RestaurantIds = rids }, groupId: groupId);
        user.Group = groupId is null ? null : await db.Groups.FindAsync(groupId);
        return CreatedAtAction(nameof(List), new { id = user.Id }, UserDto.From(user));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<UserDto>> Update(int id, UserRequest req)
    {
        var me = await users.GetAsync();
        var user = await FindManageable(me, id);
        if (user is null) return NotFound();
        if (!Enum.TryParse<UserRole>(req.Role, true, out var role)) return BadRequest(new { message = "Unknown role." });
        if (!CanManage(me, user.Role, user.UserRestaurants.Select(x => x.RestaurantId), out var why) ||
            !CanManage(me, role, req.RestaurantIds, out why)) return StatusCode(403, new { message = why });
        if (user.Id == me.Id && (role != me.Role || !req.IsActive))
            return BadRequest(new { message = "You cannot change your own role or deactivate your own account." });
        if ((user.Role == UserRole.SuperAdmin) != (role == UserRole.SuperAdmin))
            return BadRequest(new { message = "Users cannot be moved into or out of the super admin role; create a new account instead." });

        if (!CanManagePerson(me, user)) return StatusCode(403, new { message = await NotYoursMessage(user) });

        var username = req.Username.Trim().ToLowerInvariant();
        if (username != user.Username && await db.Users.AnyAsync(u => u.Username == username))
            return Conflict(new { message = "That username is already taken." });
        var requested = user.GroupId is null ? [] : await ValidRestaurantIds(user.GroupId.Value, req.RestaurantIds);
        var existing = user.UserRestaurants.Select(x => x.RestaurantId).ToHashSet();
        int[] rids;
        if (me.IsOwner && user.Id != me.Id)
        {
            // Owners change assignments only at restaurants they own; assignments elsewhere are left alone.
            var outside = requested.Where(r => !existing.Contains(r) && !me.CanManageRestaurant(r)).ToList();
            if (outside.Count > 0) return StatusCode(403, new { message = await NotOwnedMessage(outside) });
            rids = existing.Where(r => !me.CanManageRestaurant(r)).Union(requested.Where(me.CanManageRestaurant)).ToArray();
        }
        else rids = requested; // super admins anywhere; owners may pick any group restaurant for themselves

        var stillOwns = role == UserRole.Owner && req.IsActive ? rids.ToHashSet() : [];
        var orphaned = await RestaurantsLosingLastOwner(user, existing.Except(stillOwns));
        if (orphaned.Count > 0)
            return BadRequest(new { message = $"{string.Join(", ", orphaned)} would be left without an owner. Add another owner there first." });

        var before = Snapshot(user);
        user.Username = username;
        user.DisplayName = req.DisplayName.Trim();
        user.Email = req.Email?.Trim();
        user.Role = role;
        user.Permissions = role == UserRole.Manager ? ParsePermissions(req.Permissions) : ManagerPermission.None;
        user.IsActive = req.IsActive;
        user.UserRestaurants.Clear();
        foreach (var r in rids) user.UserRestaurants.Add(new UserRestaurant { UserId = user.Id, RestaurantId = r });
        var passwordChanged = !string.IsNullOrWhiteSpace(req.Password);
        if (passwordChanged)
        {
            user.PasswordHash = hasher.HashPassword(user, req.Password!);
            user.MustChangePassword = role != UserRole.TeamMember && user.Id != me.Id;
        }
        await db.SaveChangesAsync();
        await audit.LogAsync(me, "UserUpdated", "User", user.Id, new { before, after = Snapshot(user), passwordChanged }, groupId: user.GroupId);
        return UserDto.From(user);
    }

    /// <summary>Reset another user's password. Everyone except team logins must then change it at next login.</summary>
    [HttpPut("{id:int}/password")]
    public async Task<IActionResult> SetPassword(int id, SetPasswordRequest req)
    {
        var me = await users.GetAsync();
        var user = await FindManageable(me, id);
        if (user is null) return NotFound();
        if (!CanManage(me, user.Role, user.UserRestaurants.Select(x => x.RestaurantId), out var why)) return StatusCode(403, new { message = why });
        if (!CanManagePerson(me, user)) return StatusCode(403, new { message = await NotYoursMessage(user) });
        user.PasswordHash = hasher.HashPassword(user, req.Password);
        user.MustChangePassword = user.Role != UserRole.TeamMember && user.Id != me.Id;
        await db.SaveChangesAsync();
        await audit.LogAsync(me, "UserPasswordReset", "User", user.Id, new { user.Username, user.MustChangePassword }, groupId: user.GroupId);
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var me = await users.GetAsync();
        var user = await FindManageable(me, id);
        if (user is null) return NotFound();
        if (user.Id == me.Id) return BadRequest(new { message = "You cannot remove your own account." });
        if (!CanManage(me, user.Role, user.UserRestaurants.Select(x => x.RestaurantId), out var why)) return StatusCode(403, new { message = why });
        if (me.IsOwner && user.UserRestaurants.Any(ur => !me.CanManageRestaurant(ur.RestaurantId)))
        {
            var others = await NotOwnedMessage(user.UserRestaurants.Select(ur => ur.RestaurantId).Where(r => !me.CanManageRestaurant(r)));
            return StatusCode(403, new { message = $"{user.DisplayName} also works at restaurants you do not own. {others} Remove them from your restaurants on the Staff screen instead." });
        }
        if (user.Role == UserRole.Owner && !await db.Users.AnyAsync(u => u.GroupId == user.GroupId && u.Role == UserRole.Owner && u.IsActive && u.Id != id))
            return BadRequest(new { message = "A group must keep at least one active owner." });
        var orphaned = await RestaurantsLosingLastOwner(user, user.UserRestaurants.Select(ur => ur.RestaurantId));
        if (orphaned.Count > 0)
            return BadRequest(new { message = $"{string.Join(", ", orphaned)} would be left without an owner. Add another owner there first." });
        if (user.Role == UserRole.SuperAdmin && !await db.Users.AnyAsync(u => u.Role == UserRole.SuperAdmin && u.IsActive && u.Id != id))
            return BadRequest(new { message = "At least one active super admin must remain." });

        // Keep history intact: comments and audit rows reference the user by id and name, so we deactivate
        // and release the username rather than hard-deleting.
        var groupId = user.GroupId;
        user.IsActive = false;
        user.Username = $"{user.Username}#removed-{user.Id}";
        user.UserRestaurants.Clear();
        await db.SaveChangesAsync();
        await audit.LogAsync(me, "UserRemoved", "User", user.Id, new { user.DisplayName, Role = user.Role.ToString() }, groupId: groupId);
        return NoContent();
    }

    [HttpGet("permissions")]
    public ActionResult<object> PermissionCatalog() => new
    {
        permissions = Enum.GetValues<ManagerPermission>()
            .Where(v => v != ManagerPermission.None && v != ManagerPermission.All)
            .Select(v => new { name = v.ToString(), value = (int)v, description = Describe(v) }),
        roles = Enum.GetNames<UserRole>()
    };

    // ---- helpers ------------------------------------------------------------------------

    private async Task<AppUser?> FindManageable(CurrentUser me, int id)
    {
        var q = db.Users.Include(u => u.UserRestaurants).Include(u => u.Group).Where(u => u.Id == id);
        if (!me.IsSuperAdmin) q = q.Where(u => u.GroupId == me.GroupId);
        return await q.FirstOrDefaultAsync();
    }

    private static object Snapshot(AppUser u) => new
    {
        u.Username, u.DisplayName, u.Email, Role = u.Role.ToString(), Permissions = UserDto.PermissionNamesOf(u.Permissions), u.IsActive,
        RestaurantIds = u.UserRestaurants.Select(x => x.RestaurantId).OrderBy(x => x).ToArray()
    };

    private static string Describe(ManagerPermission p) => p switch
    {
        ManagerPermission.ViewComments => "View guest comments for their restaurants",
        ManagerPermission.EditComments => "Edit guest comments after entry",
        ManagerPermission.DeleteComments => "Remove guest comments",
        ManagerPermission.MarkResponded => "Mark comments as responded / not responded",
        ManagerPermission.ViewAnalytics => "See graphs and analytics",
        ManagerPermission.ExportComments => "Download comments as CSV",
        ManagerPermission.ViewAudit => "View the audit log for their restaurants",
        ManagerPermission.ManageTeamLogin => "Create and reset the generic team-member login",
        _ => p.ToString()
    };

    private static ManagerPermission ParsePermissions(IEnumerable<string> names)
    {
        var result = ManagerPermission.None;
        foreach (var n in names)
            if (Enum.TryParse<ManagerPermission>(n, true, out var p) && p != ManagerPermission.All) result |= p;
        return result;
    }

    /// <summary>
    /// Owners manage people who work at (or could be placed at) restaurants they own: anyone with no restaurant yet,
    /// or anyone sharing at least one owned restaurant. Super admins and the team-login rule are handled by CanManage.
    /// </summary>
    private static bool CanManagePerson(CurrentUser me, AppUser target) =>
        !me.IsOwner || target.Id == me.Id || target.UserRestaurants.Count == 0
        || target.UserRestaurants.Any(ur => me.CanManageRestaurant(ur.RestaurantId));

    private async Task<string> NotYoursMessage(AppUser target)
    {
        var names = await RestaurantNames(target.UserRestaurants.Select(ur => ur.RestaurantId));
        return $"{target.DisplayName} only works at restaurants you do not own ({names}). Ask one of those restaurants' owners or a super admin.";
    }

    private async Task<string> NotOwnedMessage(IEnumerable<int> restaurantIds) =>
        $"You can only assign people to restaurants you own. Not yours: {await RestaurantNames(restaurantIds)}.";

    private async Task<string> RestaurantNames(IEnumerable<int> ids)
    {
        var list = ids.Distinct().ToList();
        var names = await db.Restaurants.Where(r => list.Contains(r.Id)).OrderBy(r => r.Name).Select(r => r.Name).ToListAsync();
        return string.Join(", ", names);
    }

    /// <summary>Restaurants (by name) in which this owner is the last active owner, among those they would stop owning.</summary>
    private async Task<List<string>> RestaurantsLosingLastOwner(AppUser user, IEnumerable<int> stoppingAt)
    {
        if (user.Role != UserRole.Owner || !user.IsActive) return [];
        var ids = stoppingAt.ToList();
        if (ids.Count == 0) return [];
        return await db.Restaurants
            .Where(r => ids.Contains(r.Id) && r.IsActive
                        && !r.UserRestaurants.Any(ur => ur.UserId != user.Id && ur.User.Role == UserRole.Owner && ur.User.IsActive))
            .OrderBy(r => r.Name).Select(r => r.Name).ToListAsync();
    }

    private async Task<int[]> ValidRestaurantIds(int groupId, int[] requested)
        => await db.Restaurants.Where(r => r.GroupId == groupId && requested.Contains(r.Id)).Select(r => r.Id).ToArrayAsync();

    private static bool CanManage(CurrentUser me, UserRole targetRole, IEnumerable<int> targetRestaurants, out string why)
    {
        why = "";
        if (me.IsSuperAdmin) return true;
        if (targetRole == UserRole.SuperAdmin) { why = "Only super admins can manage super admin accounts."; return false; }
        if (me.IsOwner) return true;
        if (me.Role == UserRole.Manager && me.Has(ManagerPermission.ManageTeamLogin))
        {
            if (targetRole != UserRole.TeamMember) { why = "Managers may only manage the team-member login."; return false; }
            var ids = targetRestaurants.ToList();
            if (ids.Count == 0 || ids.Any(r => !me.CanAccessRestaurant(r))) { why = "Team logins must belong to restaurants you manage."; return false; }
            return true;
        }
        why = "Only owners can manage users.";
        return false;
    }
}
