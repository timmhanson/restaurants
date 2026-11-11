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

/// <summary>Investment groups. System administrators only; everyone else sees just their own group via /api/groups/mine.</summary>
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
            OwnerNames = g.Users.Where(u => u.Role == UserRole.Owner && u.IsActive).OrderBy(u => u.DisplayName).Select(u => u.DisplayName).ToList(),
            AdminNames = g.Users.Where(u => u.Role == UserRole.GroupAdmin && u.IsActive).OrderBy(u => u.DisplayName).Select(u => u.DisplayName).ToList()
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
    /// Creates an investment group with its investment group administrators (at least one), and optionally its restaurant
    /// owners and restaurants, in one step. Each restaurant lists which of the new owners own it (all of them when
    /// not specified); restaurants with no owner are run by the investment group administrators.
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
        var people = req.Admins.Select(a => (req: a, role: UserRole.GroupAdmin)).Concat(ownerRequests.Select(o => (req: o, role: UserRole.Owner))).ToList();
        if (req.Admins.Count == 0) return BadRequest(new { message = "Add at least one investment group administrator. They manage every account and restaurant in the group." });
        var usernames = people.Select(p => p.req.Username.Trim().ToLowerInvariant()).ToList();
        if (usernames.Distinct().Count() != usernames.Count) return BadRequest(new { message = "Each person needs a different username." });
        foreach (var (p, role) in people)
        {
            if (string.IsNullOrWhiteSpace(p.DisplayName)) return BadRequest(new { message = "Every person needs a name." });
            if (string.IsNullOrWhiteSpace(p.Password) || p.Password.Length < 8)
                return BadRequest(new { message = $"{RoleTitles.Of(role)} {p.DisplayName} needs a temporary password of at least 8 characters." });
        }
        var ownerNames = ownerRequests.Select(o => o.Username.Trim().ToLowerInvariant()).ToHashSet();
        var unknown = req.Restaurants.SelectMany(r => r.OwnerUsernames ?? []).Select(u => u.Trim().ToLowerInvariant()).Where(u => !ownerNames.Contains(u)).Distinct().ToList();
        if (unknown.Count > 0) return BadRequest(new { message = $"Restaurant owners must be among the owners being created: {string.Join(", ", unknown)}." });
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

        var created = new List<AppUser>();
        foreach (var (p, role) in people)
        {
            var username = p.Username.Trim().ToLowerInvariant();
            var user = new AppUser
            {
                Group = group, Username = username, DisplayName = p.DisplayName.Trim(), Email = string.IsNullOrWhiteSpace(p.Email) ? null : p.Email.Trim(),
                Role = role, Permissions = ManagerPermission.None, IsActive = true, MustChangePassword = true
            };
            user.PasswordHash = hasher.HashPassword(user, p.Password!);
            if (role == UserRole.Owner)
                for (var i = 0; i < restaurants.Count; i++)
                {
                    var listed = req.Restaurants[i].OwnerUsernames;
                    if (listed is null || listed.Any(u => u.Trim().ToLowerInvariant() == username))
                        user.UserRestaurants.Add(new UserRestaurant { Restaurant = restaurants[i] });
                }
            created.Add(user);
        }
        db.Users.AddRange(created);
        await db.SaveChangesAsync();
        var owners = created.Where(u => u.Role == UserRole.Owner).ToList();
        var admins = created.Where(u => u.Role == UserRole.GroupAdmin).ToList();

        await settings.SaveAsync(group.Id, new GroupSettings { GroupDisplayName = group.Name, UpdatedAt = DateTime.UtcNow, UpdatedBy = me.Username });
        await audit.LogAsync(me, "GroupCreated", "Group", group.Id,
            new
            {
                group.Name,
                Administrators = admins.Select(o => o.Username).ToArray(),
                Owners = owners.Select(o => new { o.Username, Restaurants = o.UserRestaurants.Select(ur => ur.Restaurant.Name).ToArray() }).ToArray(),
                Restaurants = restaurants.Select(r => r.Name).ToArray()
            }, groupId: group.Id);
        return new GroupDto(group.Id, group.Name, group.IsActive, group.CreatedAt, restaurants.Count, created.Count, 0)
        {
            OwnerNames = owners.Select(o => o.DisplayName).OrderBy(n => n).ToList(),
            AdminNames = admins.Select(o => o.DisplayName).OrderBy(n => n).ToList()
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
        OwnerNames = g.Users.Where(u => u.Role == UserRole.Owner && u.IsActive).OrderBy(u => u.DisplayName).Select(u => u.DisplayName).ToList(),
        AdminNames = g.Users.Where(u => u.Role == UserRole.GroupAdmin && u.IsActive).OrderBy(u => u.DisplayName).Select(u => u.DisplayName).ToList()
    }).FirstAsync();
}
