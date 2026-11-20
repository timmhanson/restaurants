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
///  - System administrators manage everyone, in every group.
///  - Investment Group administrators manage every account in their group, including owners and other investment group administrators.
///  - Restaurant owners manage the managers and team logins who work at any restaurant they own: edit, set
///    permissions, reset passwords, and suspend or restore access, even when that person also works at
///    restaurants with other owners. They assign people only to restaurants they own, and may add co-owners there,
///    but changing another owner's account is for investment group administrators.
///  - Managers holding ManageTeamLogin may only create / update / reset the team login for their restaurants.
/// Any password set by someone else forces a change at next login, except for team logins.
/// A group that has an investment group administrator must always keep at least one active.
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
            q = me.GroupId is null ? q : q.Where(u => u.GroupId == me.GroupId);
        else if (me.IsGroupAdmin)
            q = q.Where(u => u.GroupId == me.GroupId);
        else if (me.IsOwner)
        {
            // Owners see the people at their restaurants (including co-owners) and anyone not yet placed anywhere.
            var mine = me.RestaurantIds.ToList();
            q = q.Where(u => u.GroupId == me.GroupId && (u.Id == me.Id
                || u.UserRestaurants.Any(ur => mine.Contains(ur.RestaurantId))
                || ((u.Role == UserRole.Manager || u.Role == UserRole.TeamMember) && !u.UserRestaurants.Any())));
        }
        else
        {
            if (!me.Has(ManagerPermission.ManageTeamLogin)) return Forbid();
            var mine = me.RestaurantIds.ToList();
            q = q.Where(u => u.GroupId == me.GroupId && u.Role == UserRole.TeamMember && u.UserRestaurants.Any(ur => mine.Contains(ur.RestaurantId)));
        }
        var list = await q.Where(u => !u.Username.Contains("#removed-")).ToListAsync();
        return list.OrderBy(u => RoleRank(u.Role)).ThenBy(u => u.DisplayName).Select(UserDto.From).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<UserDto>> Create(UserRequest req)
    {
        var me = await users.GetAsync();
        if (!Enum.TryParse<UserRole>(req.Role, true, out var role) || !Enum.IsDefined(role)) return BadRequest(new { message = "Unknown role." });
        if (!CanGiveRole(me, role, req.RestaurantIds, out var why)) return StatusCode(403, new { message = why });
        if (me.IsOwner && !me.Has(ManagerPermission.ManageStaff)) return Denied(OwnersDoNotManageStaff);
        if (string.IsNullOrWhiteSpace(req.Password)) return BadRequest(new { message = "Password is required." });
        if (role != UserRole.SuperAdmin && me.GroupId is null) return BadRequest(new { message = "Select an investment group first." });

        var username = req.Username.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Username == username)) return Conflict(new { message = "That username is already taken." });
        var groupId = role == UserRole.SuperAdmin ? null : me.GroupId;
        // Administrators see every restaurant in the group, so they are never assigned to particular ones.
        // For an investment group administrator these are the restaurants they also own; they still see every restaurant.
        int[] rids = groupId is null || role == UserRole.SuperAdmin ? [] : await ValidRestaurantIds(groupId.Value, req.RestaurantIds);
        if (me.IsOwner && rids.Any(r => !me.CanManageRestaurant(r)))
            return StatusCode(403, new { message = await NotOwnedMessage(rids.Where(r => !me.CanManageRestaurant(r))) });

        var user = new AppUser
        {
            GroupId = groupId,
            Username = username,
            DisplayName = req.DisplayName.Trim(),
            Email = req.Email?.Trim(),
            Role = role,
            // Only what is set manager by manager is kept, and only from someone who may choose it.
            Permissions = role == UserRole.Manager ? PermissionPolicy.Parse(req.Permissions) & await Settable(me, groupId) : ManagerPermission.None,
            IsActive = req.IsActive,
            MustChangePassword = MustChange(me, role, targetId: 0, req.RequirePasswordChange, req.Email),
            UserRestaurants = rids.Select(r => new UserRestaurant { RestaurantId = r }).ToList()
        };
        user.PasswordHash = hasher.HashPassword(user, req.Password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await audit.LogAsync(me, "UserCreated", "User", user.Id,
            new { user.Username, Role = RoleTitles.Of(role), Permissions = UserDto.PermissionNamesOf(user.Permissions), Restaurants = await RestaurantNames(rids), user.MustChangePassword }, groupId: groupId);
        user.Group = groupId is null ? null : await db.Groups.FindAsync(groupId);
        return CreatedAtAction(nameof(List), new { id = user.Id }, UserDto.From(user));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<UserDto>> Update(int id, UserRequest req)
    {
        var me = await users.GetAsync();
        var user = await FindInScope(me, id);
        if (user is null) return NotFound();
        if (!Enum.TryParse<UserRole>(req.Role, true, out var role) || !Enum.IsDefined(role)) return BadRequest(new { message = "Unknown role." });
        if (await CanActOn(me, user) is { } denied) return denied;
        if (user.Id == me.Id && (role != me.Role || !req.IsActive))
            return BadRequest(new { message = "You cannot change your own role or deactivate your own account." });
        if (role != user.Role)
        {
            if (!me.IsGroupWide) return StatusCode(403, new { message = "Only an investment group administrator can change someone's role." });
            if ((user.Role == UserRole.SuperAdmin) != (role == UserRole.SuperAdmin))
                return BadRequest(new { message = "Accounts cannot be moved into or out of System Administrator; create a new account instead." });
        }
        if (!CanGiveRole(me, role, req.RestaurantIds, out var why)) return StatusCode(403, new { message = why });

        var username = req.Username.Trim().ToLowerInvariant();
        if (username != user.Username && await db.Users.AnyAsync(u => u.Username == username))
            return Conflict(new { message = "That username is already taken." });
        var existing = user.UserRestaurants.Select(x => x.RestaurantId).ToHashSet();
        var (rids, notMine) = await RestaurantsFor(me, user, role, req.RestaurantIds);
        if (notMine is not null) return notMine;

        if (await LastGroupAdminBlock(user, stillAdmin: role == UserRole.GroupAdmin && req.IsActive) is { } adminBlock) return adminBlock;
        var stillOwns = role == UserRole.Owner && req.IsActive ? rids.ToHashSet() : [];
        var orphaned = await RestaurantsLosingLastOwner(user, existing.Except(stillOwns));
        if (orphaned.Count > 0)
            return BadRequest(new { message = $"{string.Join(", ", orphaned)} would be left without an owner. Add another owner there first." });

        var before = Snapshot(user);
        user.Username = username;
        user.DisplayName = req.DisplayName.Trim();
        user.Email = req.Email?.Trim();
        user.Role = role;
        if (role == UserRole.Manager)
        {
            // What this person may choose is replaced; anything decided elsewhere (for all managers, or by someone
            // with more say) is left as it was, so it is there again if the rule is later relaxed.
            var settable = await Settable(me, user.GroupId);
            var kept = user.Role == UserRole.Manager ? user.Permissions & ~settable : ManagerPermission.None;
            user.Permissions = (kept | (PermissionPolicy.Parse(req.Permissions) & settable)) & PermissionPolicy.ForManagers;
        }
        else user.Permissions = ManagerPermission.None;
        user.IsActive = req.IsActive;
        user.UserRestaurants.Clear();
        foreach (var r in rids) user.UserRestaurants.Add(new UserRestaurant { UserId = user.Id, RestaurantId = r });
        if (string.IsNullOrWhiteSpace(user.Email)) user.MustChangePassword = false; // no address to send the code to
        var passwordChanged = !string.IsNullOrWhiteSpace(req.Password);
        if (passwordChanged)
        {
            user.PasswordHash = hasher.HashPassword(user, req.Password!);
            user.MustChangePassword = MustChange(me, role, user.Id, req.RequirePasswordChange, user.Email);
        }
        await db.SaveChangesAsync();
        await audit.LogAsync(me, "UserUpdated", "User", user.Id, new { before, after = Snapshot(user), passwordChanged, MustChangePassword = passwordChanged ? user.MustChangePassword : (bool?)null }, groupId: user.GroupId);
        return UserDto.From(user);
    }

    /// <summary>
    /// Whether someone whose password was just set by <paramref name="me"/> must pick a new one at next sign-in.
    /// Never for team logins or your own account; otherwise yes unless an administrator chose not to require it.
    /// Never for an account with no email either: the change needs an emailed code, so it could not be completed
    /// and the person would be locked out.
    /// </summary>
    private static bool MustChange(CurrentUser me, UserRole role, int targetId, bool requested, string? email) =>
        role != UserRole.TeamMember && targetId != me.Id && !string.IsNullOrWhiteSpace(email) && (requested || !me.IsGroupWide);

    /// <summary>Reset another user's password. Everyone except team logins must then change it at next login, unless an administrator waives it.</summary>
    [HttpPut("{id:int}/password")]
    public async Task<IActionResult> SetPassword(int id, SetPasswordRequest req)
    {
        var me = await users.GetAsync();
        var user = await FindInScope(me, id);
        if (user is null) return NotFound();
        if (await CanActOn(me, user) is { } denied) return denied;
        user.PasswordHash = hasher.HashPassword(user, req.Password);
        user.MustChangePassword = MustChange(me, user.Role, user.Id, req.RequirePasswordChange, user.Email);
        await db.SaveChangesAsync();
        await audit.LogAsync(me, "UserPasswordReset", "User", user.Id,
            new { user.Username, user.MustChangePassword, Restaurants = await RestaurantNames(user.UserRestaurants.Select(ur => ur.RestaurantId)) }, groupId: user.GroupId);
        return NoContent();
    }

    /// <summary>
    /// Suspend or restore someone's access. Suspension applies to the whole account, so an owner can cut off a
    /// manager who also works at other owners' restaurants; it is audited with every restaurant affected.
    /// </summary>
    [HttpPut("{id:int}/status")]
    public async Task<ActionResult<UserDto>> SetStatus(int id, UserStatusRequest req)
    {
        var me = await users.GetAsync();
        var user = await FindInScope(me, id);
        if (user is null) return NotFound();
        if (user.Id == me.Id) return BadRequest(new { message = "You cannot suspend your own account." });
        if (await CanActOn(me, user) is { } denied) return denied;
        if (user.IsActive == req.Active) return UserDto.From(user);
        if (!req.Active)
        {
            if (await LastGroupAdminBlock(user, stillAdmin: false) is { } adminBlock) return adminBlock;
            var orphaned = await RestaurantsLosingLastOwner(user, user.UserRestaurants.Select(ur => ur.RestaurantId));
            if (orphaned.Count > 0)
                return BadRequest(new { message = $"{string.Join(", ", orphaned)} would be left without an owner. Add another owner there first." });
            if (user.Role == UserRole.SuperAdmin && !await db.Users.AnyAsync(u => u.Role == UserRole.SuperAdmin && u.IsActive && u.Id != id))
                return BadRequest(new { message = "At least one active System Administrator must remain." });
        }
        user.IsActive = req.Active;
        await db.SaveChangesAsync();
        await audit.LogAsync(me, req.Active ? "UserRestored" : "UserSuspended", "User", user.Id,
            new { user.DisplayName, Role = RoleTitles.Of(user.Role), Restaurants = await RestaurantNames(user.UserRestaurants.Select(ur => ur.RestaurantId)) }, groupId: user.GroupId);
        return UserDto.From(user);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var me = await users.GetAsync();
        var user = await FindInScope(me, id);
        if (user is null) return NotFound();
        if (user.Id == me.Id) return BadRequest(new { message = "You cannot remove your own account." });
        if (await CanActOn(me, user) is { } denied) return denied;
        if (me.IsOwner && user.UserRestaurants.Any(ur => !me.CanManageRestaurant(ur.RestaurantId)))
        {
            var others = await RestaurantNames(user.UserRestaurants.Select(ur => ur.RestaurantId).Where(r => !me.CanManageRestaurant(r)));
            return StatusCode(403, new { message = $"{user.DisplayName} also works at {others}, which you do not own. Suspend their access, or take them off your restaurants on the Staff screen, instead." });
        }
        if (await LastGroupAdminBlock(user, stillAdmin: false) is { } adminBlock) return adminBlock;
        if (user.Role == UserRole.Owner && !await db.GroupHasAdminAsync(user.GroupId)
            && !await db.Users.AnyAsync(u => u.GroupId == user.GroupId && u.Role == UserRole.Owner && u.IsActive && u.Id != id))
            return BadRequest(new { message = "A group must keep at least one active owner or investment group administrator." });
        var orphaned = await RestaurantsLosingLastOwner(user, user.UserRestaurants.Select(ur => ur.RestaurantId));
        if (orphaned.Count > 0)
            return BadRequest(new { message = $"{string.Join(", ", orphaned)} would be left without an owner. Add another owner there first." });
        if (user.Role == UserRole.SuperAdmin && !await db.Users.AnyAsync(u => u.Role == UserRole.SuperAdmin && u.IsActive && u.Id != id))
            return BadRequest(new { message = "At least one active System Administrator must remain." });

        // Keep history intact: comments and audit rows reference the user by id and name, so we deactivate
        // and release the username rather than hard-deleting.
        var groupId = user.GroupId;
        user.IsActive = false;
        user.Username = $"{user.Username}#removed-{user.Id}";
        user.UserRestaurants.Clear();
        await db.SaveChangesAsync();
        await audit.LogAsync(me, "UserRemoved", "User", user.Id, new { user.DisplayName, Role = RoleTitles.Of(user.Role) }, groupId: groupId);
        return NoContent();
    }

    /// <summary>
    /// Maps a person to restaurants: exactly where they work (or, for an owner or an administrator, which they own).
    /// The same rules as changing the account: owners map only at restaurants they own, and what a person has at
    /// other owners' restaurants is left alone.
    /// </summary>
    [HttpPut("{id:int}/restaurants")]
    public async Task<ActionResult<UserDto>> SetRestaurants(int id, UserRestaurantsRequest req)
    {
        var me = await users.GetAsync();
        var user = await FindInScope(me, id);
        if (user is null) return NotFound();
        if (!me.IsAdmin) return Denied("Only owners and administrators can assign people to restaurants.");
        if (user.Role == UserRole.SuperAdmin) return BadRequest(new { message = "System Administrators are not part of any group, so they are not assigned to restaurants." });
        if (user.Id != me.Id && await CanActOn(me, user) is { } denied) return denied;
        if (user.Id == me.Id && me.IsOwner) return BadRequest(new { message = "To own or stop owning a restaurant yourself, use the restaurant list." });
        var existing = user.UserRestaurants.Select(x => x.RestaurantId).ToHashSet();
        var (rids, notMine) = await RestaurantsFor(me, user, user.Role, req.RestaurantIds ?? []);
        if (notMine is not null) return notMine;
        var orphaned = await RestaurantsLosingLastOwner(user, existing.Except(rids));
        if (orphaned.Count > 0)
            return BadRequest(new { message = $"{string.Join(", ", orphaned)} would be left without an owner. Add another owner there first." });
        if (existing.SetEquals(rids)) return UserDto.From(user);
        var before = await RestaurantNames(existing);
        user.UserRestaurants.Clear();
        foreach (var r in rids) user.UserRestaurants.Add(new UserRestaurant { UserId = user.Id, RestaurantId = r });
        await db.SaveChangesAsync();
        await audit.LogAsync(me, "UserRestaurantsChanged", "User", user.Id,
            new { user.DisplayName, Role = RoleTitles.Of(user.Role), before, after = await RestaurantNames(rids) }, groupId: user.GroupId);
        return UserDto.From(user);
    }

    /// <summary>
    /// Every permission as it stands in this person's group: whether owners hold it, and whether all managers do,
    /// none do, or it is set manager by manager. CanSet: this person may choose the manager-by-manager ones.
    /// </summary>
    [HttpGet("permissions")]
    public async Task<ActionResult<object>> PermissionCatalog()
    {
        var me = await users.GetAsync();
        var group = me.GroupId is null ? null : await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == me.GroupId);
        return new
        {
            permissions = PermissionPolicy.States(group).Select(s => new
            {
                name = s.Name, value = (int)Enum.Parse<ManagerPermission>(s.Name), description = s.Description, s.ForManagers, s.SpendsMoney,
                s.OwnersCan, s.Managers, s.ManagersSetBy
            }),
            canSet = CanSetPermissions(me),
            roles = Enum.GetValues<UserRole>().OrderBy(RoleRank).Select(r => new { name = r.ToString(), title = RoleTitles.Of(r) })
        };
    }

    // ---- helpers ------------------------------------------------------------------------

    /// <summary>Widest role first, for sorting lists.</summary>
    private static int RoleRank(UserRole r) => r switch
    {
        UserRole.SuperAdmin => 0, UserRole.GroupAdmin => 1, UserRole.Owner => 2, UserRole.Manager => 3, _ => 4
    };

    private static bool IsGroupWideRole(UserRole r) => r is UserRole.SuperAdmin or UserRole.GroupAdmin;

    private async Task<AppUser?> FindInScope(CurrentUser me, int id)
    {
        var q = db.Users.Include(u => u.UserRestaurants).Include(u => u.Group).Where(u => u.Id == id);
        if (!me.IsSuperAdmin) q = q.Where(u => u.GroupId == me.GroupId);
        return await q.FirstOrDefaultAsync();
    }

    /// <summary>
    /// Whether the caller may change, reset, suspend or remove this existing account. Returns null when allowed,
    /// otherwise the 403 to send back.
    /// </summary>
    private async Task<ObjectResult?> CanActOn(CurrentUser me, AppUser target)
    {
        if (me.IsSuperAdmin) return null;
        if (target.Role == UserRole.SuperAdmin) return Denied("Only a System Administrator can manage System Administrator accounts.");
        if (me.IsGroupAdmin) return null;
        if (target.Id == me.Id) return me.IsTeamMember ? Denied("Team logins cannot manage accounts.") : null;
        if (me.IsOwner)
        {
            if (!me.Has(ManagerPermission.ManageStaff)) return Denied(OwnersDoNotManageStaff);
            if (target.Role is UserRole.GroupAdmin or UserRole.Owner)
                return Denied($"Only an investment group administrator can change {target.DisplayName}'s account, because they are {RoleTitles.WithArticle(target.Role)}. You can still add or remove co-owners on a restaurant's Staff screen.");
            var shares = target.UserRestaurants.Count == 0 || target.UserRestaurants.Any(ur => me.CanManageRestaurant(ur.RestaurantId));
            if (shares) return null;
            var names = await RestaurantNames(target.UserRestaurants.Select(ur => ur.RestaurantId));
            return Denied($"{target.DisplayName} only works at restaurants you do not own ({names}). Ask one of those restaurants' owners or an investment group administrator.");
        }
        if (me.Role == UserRole.Manager && me.Has(ManagerPermission.ManageTeamLogin))
        {
            if (target.Role != UserRole.TeamMember) return Denied("Managers may only manage the team login.");
            var ids = target.UserRestaurants.Select(ur => ur.RestaurantId).ToList();
            if (ids.Count == 0 || ids.Any(r => !me.CanAccessRestaurant(r))) return Denied("Team logins must belong to restaurants you manage.");
            return null;
        }
        return Denied("Only owners and administrators can manage users.");
    }

    /// <summary>Whether the caller may create an account with this role (or keep one in it) at these restaurants.</summary>
    private static bool CanGiveRole(CurrentUser me, UserRole role, IEnumerable<int> restaurants, out string why)
    {
        why = "";
        if (me.IsSuperAdmin) return true;
        if (role == UserRole.SuperAdmin) { why = "Only a System Administrator can create System Administrators."; return false; }
        if (me.IsGroupAdmin) return true;
        if (role == UserRole.GroupAdmin) { why = "Only system and investment group administrators can make someone an investment group administrator."; return false; }
        if (me.IsOwner) return true; // restaurant scope is checked by the caller
        if (me.Role == UserRole.Manager && me.Has(ManagerPermission.ManageTeamLogin))
        {
            if (role != UserRole.TeamMember) { why = "Managers may only manage the team login."; return false; }
            var ids = restaurants.ToList();
            if (ids.Count == 0 || ids.Any(r => !me.CanAccessRestaurant(r))) { why = "Team logins must belong to restaurants you manage."; return false; }
            return true;
        }
        why = "Only owners and administrators can manage users.";
        return false;
    }

    private ObjectResult Denied(string message) => StatusCode(403, new { message });

    private const string OwnersDoNotManageStaff = "Owners in your investment group may not add or change staff accounts. Ask one of the group's administrators.";

    /// <summary>Administrators always choose what each manager may do; an owner does when owners in the group were left that.</summary>
    private static bool CanSetPermissions(CurrentUser me) => me.IsGroupWide || (me.IsOwner && me.Has(ManagerPermission.SetPermissions));

    /// <summary>The permissions this person may tick or untick for a manager: the ones set manager by manager in the group, or none.</summary>
    private async Task<ManagerPermission> Settable(CurrentUser me, int? groupId)
    {
        if (!CanSetPermissions(me)) return ManagerPermission.None;
        var group = groupId is null ? null : await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId);
        return PermissionPolicy.PerManager(group);
    }

    /// <summary>
    /// The restaurants a person ends up mapped to when <paramref name="me"/> asks for these. Owners change
    /// assignments only at restaurants they own; what the person has elsewhere is left alone.
    /// </summary>
    private async Task<(int[] Ids, ObjectResult? Problem)> RestaurantsFor(CurrentUser me, AppUser user, UserRole role, int[] wanted)
    {
        int[] requested = user.GroupId is null || role == UserRole.SuperAdmin ? [] : await ValidRestaurantIds(user.GroupId.Value, wanted);
        var existing = user.UserRestaurants.Select(x => x.RestaurantId).ToHashSet();
        if (me.IsOwner && user.Id != me.Id)
        {
            var outside = requested.Where(r => !existing.Contains(r) && !me.CanManageRestaurant(r)).ToList();
            if (outside.Count > 0) return ([], StatusCode(403, new { message = await NotOwnedMessage(outside) }));
            return (existing.Where(r => !me.CanManageRestaurant(r)).Union(requested.Where(me.CanManageRestaurant)).ToArray(), null);
        }
        // An owner editing themselves keeps their restaurants; ownership changes go through the restaurant list.
        return (me.IsOwner ? existing.ToArray() : requested, null);
    }

    /// <summary>Stops the last active investment group administrator of a group from being demoted, suspended or removed.</summary>
    private async Task<ObjectResult?> LastGroupAdminBlock(AppUser user, bool stillAdmin)
    {
        if (user.Role != UserRole.GroupAdmin || !user.IsActive || stillAdmin) return null;
        if (await db.GroupHasAdminAsync(user.GroupId, exceptUserId: user.Id)) return null;
        return BadRequest(new { message = $"{user.DisplayName} is the group's only investment group administrator. Make someone else an investment group administrator first." });
    }

    private static object Snapshot(AppUser u) => new
    {
        u.Username, u.DisplayName, u.Email, Role = RoleTitles.Of(u.Role), Permissions = UserDto.PermissionNamesOf(u.Permissions), u.IsActive,
        RestaurantIds = u.UserRestaurants.Select(x => x.RestaurantId).OrderBy(x => x).ToArray()
    };

    private async Task<string> NotOwnedMessage(IEnumerable<int> restaurantIds) =>
        $"You can only assign people to restaurants you own. Not yours: {await RestaurantNames(restaurantIds)}.";

    private async Task<string> RestaurantNames(IEnumerable<int> ids)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return "";
        var names = await db.Restaurants.Where(r => list.Contains(r.Id)).OrderBy(r => r.Name).Select(r => r.Name).ToListAsync();
        return string.Join(", ", names);
    }

    /// <summary>
    /// Restaurants (by name) in which this owner is the last active owner, among those they would stop owning.
    /// Only enforced in groups without an investment group administrator; with one, a restaurant may be run by the administrators directly.
    /// </summary>
    private async Task<List<string>> RestaurantsLosingLastOwner(AppUser user, IEnumerable<int> stoppingAt)
    {
        if (user.Role != UserRole.Owner || !user.IsActive) return [];
        var ids = stoppingAt.ToList();
        if (ids.Count == 0 || await db.GroupHasAdminAsync(user.GroupId)) return [];
        return await db.Restaurants
            .Where(r => ids.Contains(r.Id) && r.IsActive
                        && !r.UserRestaurants.Any(ur => ur.UserId != user.Id && ur.User.Role == UserRole.Owner && ur.User.IsActive))
            .OrderBy(r => r.Name).Select(r => r.Name).ToListAsync();
    }

    private async Task<int[]> ValidRestaurantIds(int groupId, int[] requested)
        => await db.Restaurants.Where(r => r.GroupId == groupId && requested.Contains(r.Id)).Select(r => r.Id).ToArrayAsync();
}
