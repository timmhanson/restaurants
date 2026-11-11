using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Auth;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Controllers;

[ApiController]
[Route("api/audit")]
[Authorize]
public class AuditController(AppDbContext db, CurrentUserProvider users) : ControllerBase
{
    /// <summary>
    /// System and investment group administrators see the whole group. Owners see rows for the restaurants they own, changes to
    /// the people who work there, and their own actions. Managers with ViewAudit see rows for their restaurants plus their own actions.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PagedResult<AuditDto>>> List([FromQuery] int? restaurantId, [FromQuery] int? userId, [FromQuery] string? action,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int page = 1, [FromQuery] int pageSize = 100)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.ViewAudit)) return Forbid();
        var q = me.IsSuperAdmin && me.GroupId is null ? db.AuditLogs.AsQueryable() : db.AuditLogs.Where(a => a.GroupId == me.GroupId);
        if (me.IsOwner)
        {
            var mine = me.RestaurantIds.ToList();
            var people = await db.Users.Where(u => u.UserRestaurants.Any(ur => mine.Contains(ur.RestaurantId))).Select(u => u.Id.ToString()).ToListAsync();
            q = q.Where(a => (a.RestaurantId != null && mine.Contains(a.RestaurantId.Value)) || a.UserId == me.Id
                             || (a.EntityType == "User" && a.EntityId != null && people.Contains(a.EntityId)));
        }
        else if (!me.IsGroupWide)
        {
            var mine = me.RestaurantIds.ToList();
            q = q.Where(a => (a.RestaurantId != null && mine.Contains(a.RestaurantId.Value)) || a.UserId == me.Id);
        }
        if (restaurantId is not null) q = q.Where(a => a.RestaurantId == restaurantId);
        if (userId is not null) q = q.Where(a => a.UserId == userId);
        if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action == action);
        if (from is not null) q = q.Where(a => a.Timestamp >= from);
        if (to is not null) q = q.Where(a => a.Timestamp <= to);
        var total = await q.CountAsync();
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 500);
        var rows = await q.OrderByDescending(a => a.Timestamp).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        var names = await db.Restaurants.ToDictionaryAsync(r => r.Id, r => r.Name);
        var items = rows.Select(a => new AuditDto(a.Id, a.Timestamp, a.UserId, a.UserName, a.UserRole is null ? null : RoleTitles.Of(a.UserRole.Value), a.RestaurantId,
            a.RestaurantId is null ? null : names.GetValueOrDefault(a.RestaurantId.Value), a.Action, a.EntityType, a.EntityId, a.Details, a.IpAddress)).ToList();
        return new PagedResult<AuditDto>(items, total, page, pageSize);
    }

    [HttpGet("actions")]
    public async Task<ActionResult<List<string>>> Actions()
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.ViewAudit)) return Forbid();
        var q = me.IsSuperAdmin && me.GroupId is null ? db.AuditLogs.AsQueryable() : db.AuditLogs.Where(a => a.GroupId == me.GroupId);
        return await q.Select(a => a.Action).Distinct().OrderBy(a => a).ToListAsync();
    }
}
