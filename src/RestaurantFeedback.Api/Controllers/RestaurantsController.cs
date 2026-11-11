using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Auth;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Models;
using RestaurantFeedback.Api.Services;

namespace RestaurantFeedback.Api.Controllers;

/// <summary>
/// Restaurants and who works at them.
///  - System and group administrators manage every restaurant in the group and decide who owns each one.
///  - Owners manage (edit, staff, add co-owners to) only the restaurants they own. In a group with no group
///    administrator, owners may also claim restaurants that have no owner yet.
///  - Everyone else just sees the restaurants they belong to.
/// A restaurant must keep an owner unless the group has a group administrator to run it directly.
/// </summary>
[ApiController]
[Route("api/restaurants")]
[Authorize]
public class RestaurantsController(AppDbContext db, AuditService audit, CurrentUserProvider users) : ControllerBase
{
    /// <summary>
    /// Restaurants the signed-in user belongs to. With all=true, owners and super admins get every restaurant
    /// in the group (including inactive ones) with its owners, staff counts and whether they can manage it.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<RestaurantDto>>> List([FromQuery] bool all = false)
    {
        var me = await users.GetAsync();
        if (!(me.IsAdmin && all))
        {
            var mine = await db.Restaurants.Where(r => r.GroupId == me.GroupId && me.RestaurantIds.Contains(r.Id) && r.IsActive)
                .OrderBy(r => r.Name).ToListAsync();
            return mine.Select(RestaurantDto.From).ToList();
        }

        var hasAdmin = await db.GroupHasAdminAsync(me.GroupId);
        var list = await db.Restaurants.Where(r => r.GroupId == me.GroupId)
            .Include(r => r.UserRestaurants).ThenInclude(ur => ur.User)
            .OrderBy(r => r.Name).ToListAsync();
        return list.Select(r =>
        {
            var staff = r.UserRestaurants.Select(ur => ur.User).Where(u => u.IsActive).ToList();
            return RestaurantDto.From(r) with
            {
                Owners = staff.Where(u => u.Role == UserRole.Owner).OrderBy(u => u.DisplayName).Select(u => new PersonRef(u.Id, u.DisplayName, u.Username)).ToList(),
                ManagerCount = staff.Count(u => u.Role == UserRole.Manager),
                TeamLoginCount = staff.Count(u => u.Role == UserRole.TeamMember),
                OwnedByMe = me.IsOwner && staff.Any(u => u.Id == me.Id),
                CanManage = me.CanManageRestaurant(r.Id),
                CanClaim = me.IsOwner && !hasAdmin && !staff.Any(u => u.Role == UserRole.Owner)
            };
        }).ToList();
    }

    [HttpPost]
    [Authorize(Roles = "Owner,GroupAdmin,SuperAdmin")]
    public async Task<ActionResult<RestaurantDto>> Create(RestaurantRequest req)
    {
        var me = await users.GetAsync();
        if (me.GroupId is null) return BadRequest(new { message = "Select an investment group first." });
        var r = new Restaurant
        {
            GroupId = me.GroupId.Value, Name = req.Name.Trim(), StreetAddress = req.StreetAddress, City = req.City, State = req.State,
            Zip = req.Zip, Phone = req.Phone, IsActive = req.IsActive
        };
        // An owner who adds a restaurant owns it. A super admin assigns owners from the staffing screen.
        if (me.IsOwner) r.UserRestaurants.Add(new UserRestaurant { UserId = me.Id });
        db.Restaurants.Add(r);
        await db.SaveChangesAsync();
        await audit.LogAsync(me, "RestaurantCreated", "Restaurant", r.Id, new { r.Name }, r.Id);
        return RestaurantDto.From(r) with { CanManage = true, OwnedByMe = me.IsOwner };
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = "Owner,GroupAdmin,SuperAdmin")]
    public async Task<ActionResult<RestaurantDto>> Update(int id, RestaurantRequest req)
    {
        var me = await users.GetAsync();
        var r = await db.Restaurants.FirstOrDefaultAsync(x => x.Id == id && x.GroupId == me.GroupId);
        if (r is null) return NotFound();
        if (!me.CanManageRestaurant(id)) return NotOwned(r);
        var before = RestaurantDto.From(r);
        r.Name = req.Name.Trim(); r.StreetAddress = req.StreetAddress; r.City = req.City; r.State = req.State;
        r.Zip = req.Zip; r.Phone = req.Phone; r.IsActive = req.IsActive;
        await db.SaveChangesAsync();
        await audit.LogAsync(me, "RestaurantUpdated", "Restaurant", r.Id, new { before, after = RestaurantDto.From(r) }, r.Id);
        return RestaurantDto.From(r);
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = "Owner,GroupAdmin,SuperAdmin")]
    public async Task<IActionResult> Deactivate(int id)
    {
        var me = await users.GetAsync();
        var r = await db.Restaurants.FirstOrDefaultAsync(x => x.Id == id && x.GroupId == me.GroupId);
        if (r is null) return NotFound();
        if (!me.CanManageRestaurant(id)) return NotOwned(r);
        r.IsActive = false;
        await db.SaveChangesAsync();
        await audit.LogAsync(me, "RestaurantDeactivated", "Restaurant", r.Id, new { r.Name }, r.Id);
        return NoContent();
    }

    /// <summary>Everyone in the group who could work at this restaurant, flagged by whether they are assigned to it.</summary>
    [HttpGet("{id:int}/staff")]
    [Authorize(Roles = "Owner,GroupAdmin,SuperAdmin")]
    public async Task<ActionResult<StaffResponse>> Staff(int id)
    {
        var me = await users.GetAsync();
        var r = await db.Restaurants.FirstOrDefaultAsync(x => x.Id == id && x.GroupId == me.GroupId);
        if (r is null) return NotFound();
        return await BuildStaff(me, r);
    }

    /// <summary>
    /// Sets exactly who is assigned to this restaurant: owners, managers and team logins in one list.
    /// Only for restaurants the caller can manage. A restaurant must keep at least one owner.
    /// </summary>
    [HttpPut("{id:int}/staff")]
    [Authorize(Roles = "Owner,GroupAdmin,SuperAdmin")]
    public async Task<ActionResult<StaffResponse>> SetStaff(int id, StaffRequest req)
    {
        var me = await users.GetAsync();
        var r = await db.Restaurants.Include(x => x.UserRestaurants).FirstOrDefaultAsync(x => x.Id == id && x.GroupId == me.GroupId);
        if (r is null) return NotFound();
        if (!me.CanManageRestaurant(id)) return NotOwned(r);

        var wanted = req.UserIds.Distinct().ToHashSet();
        var current = r.UserRestaurants.Select(ur => ur.UserId).ToHashSet();
        // Inactive people may stay assigned (they are listed so they can be removed), but cannot be newly added.
        var candidates = await db.Users
            .Where(u => u.GroupId == me.GroupId && u.Role != UserRole.SuperAdmin && u.Role != UserRole.GroupAdmin && wanted.Contains(u.Id) && (u.IsActive || current.Contains(u.Id)))
            .Select(u => new { u.Id, u.Role, u.DisplayName, u.IsActive }).ToListAsync();
        if (candidates.Count != wanted.Count) return BadRequest(new { message = "Some of the selected people are not active members of this group." });
        if (!candidates.Any(c => c.Role == UserRole.Owner && c.IsActive) && !await db.GroupHasAdminAsync(me.GroupId))
            return BadRequest(new { message = "Every restaurant needs at least one owner. Tick an owner before saving." });
        if (me.IsOwner && !wanted.Contains(me.Id))
            return BadRequest(new { message = "To stop owning this restaurant, use \"Stop owning\" on the restaurant list instead." });

        var added = wanted.Except(current).ToList();
        var removed = current.Except(wanted).ToList();
        // Administrators are never assigned to restaurants, but keep any such row rather than touching it (defensive).
        var removable = await db.Users.Where(u => removed.Contains(u.Id) && u.Role != UserRole.SuperAdmin && u.Role != UserRole.GroupAdmin).Select(u => u.Id).ToListAsync();
        r.UserRestaurants.RemoveAll(ur => removable.Contains(ur.UserId));
        foreach (var uid in added) r.UserRestaurants.Add(new UserRestaurant { UserId = uid, RestaurantId = r.Id });
        await db.SaveChangesAsync();

        if (added.Count > 0 || removable.Count > 0)
        {
            var names = await db.Users.Where(u => added.Contains(u.Id) || removable.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => $"{u.DisplayName} ({u.Role})");
            await audit.LogAsync(me, "RestaurantStaffChanged", "Restaurant", r.Id, new
            {
                restaurant = r.Name,
                added = added.Select(i => names.GetValueOrDefault(i)).ToArray(),
                removed = removable.Select(i => names.GetValueOrDefault(i)).ToArray()
            }, r.Id);
        }
        return await BuildStaff(me, r);
    }

    /// <summary>An owner marks a restaurant in their group as one they own, or stops owning it.</summary>
    [HttpPut("{id:int}/ownership")]
    [Authorize(Roles = "Owner")]
    public async Task<IActionResult> SetOwnership(int id, OwnershipRequest req)
    {
        var me = await users.GetAsync();
        var r = await db.Restaurants.Include(x => x.UserRestaurants).ThenInclude(ur => ur.User)
            .FirstOrDefaultAsync(x => x.Id == id && x.GroupId == me.GroupId);
        if (r is null) return NotFound();
        var mine = r.UserRestaurants.FirstOrDefault(ur => ur.UserId == me.Id);
        var hasAdmin = await db.GroupHasAdminAsync(me.GroupId);
        if (req.Owned && mine is null)
        {
            if (hasAdmin)
                return StatusCode(403, new { message = $"Your group administrators decide who owns {r.Name}. Ask one of them, or an owner of {r.Name}, to add you on its Staff screen." });
            if (r.UserRestaurants.Any(ur => ur.User.Role == UserRole.Owner && ur.User.IsActive))
                return StatusCode(403, new { message = $"{r.Name} already has an owner. Ask them to add you as a co-owner on its Staff screen." });
            r.UserRestaurants.Add(new UserRestaurant { UserId = me.Id, RestaurantId = r.Id });
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "RestaurantOwnershipClaimed", "Restaurant", r.Id, new { r.Name }, r.Id);
        }
        else if (!req.Owned && mine is not null)
        {
            var otherOwners = r.UserRestaurants.Count(ur => ur.UserId != me.Id && ur.User.Role == UserRole.Owner && ur.User.IsActive);
            if (otherOwners == 0 && !hasAdmin)
                return BadRequest(new { message = $"You are the only owner of {r.Name}. Add another owner from its Staff screen first." });
            r.UserRestaurants.Remove(mine);
            await db.SaveChangesAsync();
            await audit.LogAsync(me, "RestaurantOwnershipReleased", "Restaurant", r.Id, new { r.Name }, r.Id);
        }
        return NoContent();
    }

    private ObjectResult NotOwned(Restaurant r) =>
        StatusCode(403, new { message = $"Only owners of {r.Name} or a group administrator can change it." });

    private async Task<StaffResponse> BuildStaff(CurrentUser me, Restaurant r)
    {
        var people = await db.Users.AsNoTracking()
            .Where(u => u.GroupId == me.GroupId && u.Role != UserRole.SuperAdmin && !u.Username.Contains("#removed-"))
            .Include(u => u.UserRestaurants).ThenInclude(ur => ur.Restaurant)
            .OrderBy(u => u.DisplayName).ToListAsync();
        StaffMember ToMember(AppUser u) => new(
            u.Id, u.DisplayName, u.Username, u.Email, u.Role.ToString(),
            u.Role == UserRole.Manager ? UserDto.PermissionNamesOf(u.Permissions) : [],
            u.UserRestaurants.Any(ur => ur.RestaurantId == r.Id), u.IsActive,
            u.UserRestaurants.Where(ur => ur.RestaurantId != r.Id).Select(ur => ur.Restaurant.Name).OrderBy(n => n).ToArray());
        var dto = RestaurantDto.From(r) with { CanManage = me.CanManageRestaurant(r.Id) };
        var admins = people.Where(u => u.Role == UserRole.GroupAdmin && u.IsActive).Select(u => new PersonRef(u.Id, u.DisplayName, u.Username)).ToList();
        return new StaffResponse(dto, me.CanManageRestaurant(r.Id),
            people.Where(u => u.Role == UserRole.Owner).Select(ToMember).ToList(),
            people.Where(u => u.Role == UserRole.Manager).Select(ToMember).ToList(),
            people.Where(u => u.Role == UserRole.TeamMember).Select(ToMember).ToList())
        {
            GroupAdmins = admins,
            OwnerRequired = admins.Count == 0
        };
    }
}
