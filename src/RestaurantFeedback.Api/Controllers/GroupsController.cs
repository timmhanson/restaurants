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
            db.Comments.Count(c => c.Restaurant.GroupId == g.Id))).ToListAsync();
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

    [HttpPost]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<ActionResult<GroupDto>> Create(GroupRequest req)
    {
        var me = await users.GetAsync();
        var name = req.Name.Trim();
        if (await db.Groups.AnyAsync(g => g.Name == name)) return Conflict(new { message = "A group with that name already exists." });
        var group = new OwnerGroup { Name = name, IsActive = req.IsActive };
        db.Groups.Add(group);

        AppUser? owner = null;
        if (req.FirstOwner is not null)
        {
            var username = req.FirstOwner.Username.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(req.FirstOwner.Password)) return BadRequest(new { message = "The first owner needs a password." });
            if (await db.Users.AnyAsync(u => u.Username == username)) return Conflict(new { message = "That username is already taken." });
            owner = new AppUser
            {
                Group = group, Username = username, DisplayName = req.FirstOwner.DisplayName.Trim(), Email = req.FirstOwner.Email?.Trim(),
                Role = UserRole.Owner, Permissions = ManagerPermission.All, IsActive = true, MustChangePassword = true
            };
            owner.PasswordHash = hasher.HashPassword(owner, req.FirstOwner.Password);
            db.Users.Add(owner);
        }
        await db.SaveChangesAsync();
        await settings.SaveAsync(group.Id, new GroupSettings { GroupDisplayName = group.Name, UpdatedAt = DateTime.UtcNow, UpdatedBy = me.Username });
        await audit.LogAsync(me, "GroupCreated", "Group", group.Id, new { group.Name, FirstOwner = owner?.Username }, groupId: group.Id);
        return new GroupDto(group.Id, group.Name, group.IsActive, group.CreatedAt, 0, owner is null ? 0 : 1, 0);
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
        db.Comments.Count(c => c.Restaurant.GroupId == g.Id))).FirstAsync();
}
