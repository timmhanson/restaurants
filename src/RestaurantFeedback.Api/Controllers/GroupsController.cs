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

/// <summary>Investment groups. Super admins only; owners see just their own group via /api/groups/mine.</summary>
[ApiController]
[Route("api/groups")]
[Authorize]
public class GroupsController(AppDbContext db, IPasswordHasher<AppUser> hasher, AuditService audit, IGroupSettingsStore settings, CurrentUserProvider users) : ControllerBase
{
    [HttpGet]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<ActionResult<List<GroupDto>>> List()
    {
        var groups = await db.Groups.OrderBy(g => g.Name).Select(g => new GroupDto(
            g.Id, g.Name, g.IsActive, g.CreatedAt, g.Restaurants.Count, g.Users.Count(u => u.IsActive),
            db.Comments.Count(c => c.Restaurant.GroupId == g.Id))
        {
            OwnerNames = g.Users.Where(u => u.Role == UserRole.Owner && u.IsActive).OrderBy(u => u.DisplayName).Select(u => u.DisplayName).ToList()
        }).ToListAsync();
        return groups;
    }

    [HttpGet("mine")]
    public async Task<ActionResult<GroupDto>> Mine()
    {
        var me = await users.GetAsync();
        if (me.GroupId is null) return NotFound();
        var g = await db.Groups.Where(x => x.Id == me.GroupId).Select(g => new GroupDto(
            g.Id, g.Name, g.IsActive, g.CreatedAt, g.Restaurants.Count, g.Users.Count(u => u.IsActive),
            db.Comments.Count(c => c.Restaurant.GroupId == g.Id))).FirstOrDefaultAsync();
        return g is null ? NotFound() : g;
    }

    /// <summary>
    /// Creates an investment group, optionally with its owners and restaurants in one step.
    /// Every owner created here owns every restaurant created here; adjust afterwards on each restaurant's Staff screen.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<ActionResult<GroupDto>> Create(GroupRequest req)
    {
        var me = await users.GetAsync();
        var name = req.Name.Trim();
        if (await db.Groups.AnyAsync(g => g.Name == name)) return Conflict(new { message = "A group with that name already exists." });

        var ownerRequests = req.Owners.ToList();
        if (req.FirstOwner is not null) ownerRequests.Insert(0, req.FirstOwner);
        var usernames = ownerRequests.Select(o => o.Username.Trim().ToLowerInvariant()).ToList();
        if (usernames.Distinct().Count() != usernames.Count) return BadRequest(new { message = "Each owner needs a different username." });
        foreach (var o in ownerRequests)
        {
            if (string.IsNullOrWhiteSpace(o.Password) || o.Password.Length < 8)
                return BadRequest(new { message = $"Owner {o.DisplayName} needs a temporary password of at least 8 characters." });
            if (string.IsNullOrWhiteSpace(o.DisplayName)) return BadRequest(new { message = "Every owner needs a display name." });
        }
        var taken = await db.Users.Where(u => usernames.Contains(u.Username)).Select(u => u.Username).ToListAsync();
        if (taken.Count > 0) return Conflict(new { message = $"Username already taken: {string.Join(", ", taken)}." });
        if (req.Restaurants.Any(r => string.IsNullOrWhiteSpace(r.Name))) return BadRequest(new { message = "Every restaurant needs a name." });

        var group = new OwnerGroup { Name = name, IsActive = req.IsActive };
        db.Groups.Add(group);
        var restaurants = req.Restaurants.Select(r => new Restaurant
        {
            Group = group, Name = r.Name.Trim(), StreetAddress = r.StreetAddress, City = r.City, State = r.State, Zip = r.Zip, Phone = r.Phone, IsActive = r.IsActive
        }).ToList();
        db.Restaurants.AddRange(restaurants);

        var owners = new List<AppUser>();
        foreach (var o in ownerRequests)
        {
            var owner = new AppUser
            {
                Group = group, Username = o.Username.Trim().ToLowerInvariant(), DisplayName = o.DisplayName.Trim(), Email = string.IsNullOrWhiteSpace(o.Email) ? null : o.Email.Trim(),
                Role = UserRole.Owner, Permissions = ManagerPermission.All, IsActive = true, MustChangePassword = true
            };
            owner.PasswordHash = hasher.HashPassword(owner, o.Password!);
            foreach (var r in restaurants) owner.UserRestaurants.Add(new UserRestaurant { Restaurant = r });
            owners.Add(owner);
        }
        db.Users.AddRange(owners);
        await db.SaveChangesAsync();

        await settings.SaveAsync(group.Id, new GroupSettings { GroupDisplayName = group.Name, UpdatedAt = DateTime.UtcNow, UpdatedBy = me.Username });
        await audit.LogAsync(me, "GroupCreated", "Group", group.Id,
            new { group.Name, Owners = owners.Select(o => o.Username).ToArray(), Restaurants = restaurants.Select(r => r.Name).ToArray() }, groupId: group.Id);
        return new GroupDto(group.Id, group.Name, group.IsActive, group.CreatedAt, restaurants.Count, owners.Count, 0)
        {
            OwnerNames = owners.Select(o => o.DisplayName).OrderBy(n => n).ToList()
        };
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<ActionResult<GroupDto>> Update(int id, GroupRequest req)
    {
        var me = await users.GetAsync();
        var group = await db.Groups.FindAsync(id);
        if (group is null) return NotFound();
        var before = new { group.Name, group.IsActive };
        group.Name = req.Name.Trim();
        group.IsActive = req.IsActive;
        await db.SaveChangesAsync();
        await audit.LogAsync(me, "GroupUpdated", "Group", group.Id, new { before, after = new { group.Name, group.IsActive } }, groupId: group.Id);
        return await Mine(group.Id);
    }

    private async Task<GroupDto> Mine(int id) => await db.Groups.Where(x => x.Id == id).Select(g => new GroupDto(
        g.Id, g.Name, g.IsActive, g.CreatedAt, g.Restaurants.Count, g.Users.Count(u => u.IsActive),
        db.Comments.Count(c => c.Restaurant.GroupId == g.Id))
    {
        OwnerNames = g.Users.Where(u => u.Role == UserRole.Owner && u.IsActive).OrderBy(u => u.DisplayName).Select(u => u.DisplayName).ToList()
    }).FirstAsync();
}
