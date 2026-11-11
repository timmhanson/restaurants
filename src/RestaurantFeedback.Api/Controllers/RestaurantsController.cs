using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Auth;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Models;
using RestaurantFeedback.Api.Services;

namespace RestaurantFeedback.Api.Controllers;

[ApiController]
[Route("api/restaurants")]
[Authorize]
public class RestaurantsController(AppDbContext db, AuditService audit, CurrentUserProvider users) : ControllerBase
{
    /// <summary>Restaurants the signed-in user belongs to. Owners also see every restaurant in the group (flagged by membership).</summary>
    [HttpGet]
    public async Task<ActionResult<List<RestaurantDto>>> Mine([FromQuery] bool all = false)
    {
        var me = await users.GetAsync();
        var q = db.Restaurants.Where(r => r.GroupId == me.GroupId);
        if (!(me.IsAdmin && all)) q = q.Where(r => me.RestaurantIds.Contains(r.Id) && r.IsActive);
        var list = await q.OrderBy(r => r.Name).ToListAsync();
        return list.Select(RestaurantDto.From).ToList();
    }

    [HttpPost]
    [Authorize(Roles = "Owner,SuperAdmin")]
    public async Task<ActionResult<RestaurantDto>> Create(RestaurantRequest req)
    {
        var me = await users.GetAsync();
        var r = new Restaurant
        {
            Name = req.Name.Trim(), StreetAddress = req.StreetAddress, City = req.City, State = req.State,
            Zip = req.Zip, Phone = req.Phone, IsActive = req.IsActive
        };
        if (me.GroupId is null) return BadRequest(new { message = "Select an investment group first." });
        r.GroupId = me.GroupId.Value;
        // The creating owner is linked automatically; other owners are linked by editing their user record.
        if (me.IsOwner) r.UserRestaurants.Add(new UserRestaurant { UserId = me.Id });
        db.Restaurants.Add(r);
        await db.SaveChangesAsync();
        await audit.LogAsync(me, "RestaurantCreated", "Restaurant", r.Id, new { r.Name }, r.Id);
        return RestaurantDto.From(r);
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = "Owner,SuperAdmin")]
    public async Task<ActionResult<RestaurantDto>> Update(int id, RestaurantRequest req)
    {
        var me = await users.GetAsync();
        var r = await db.Restaurants.FirstOrDefaultAsync(x => x.Id == id && x.GroupId == me.GroupId);
        if (r is null) return NotFound();
        var before = RestaurantDto.From(r);
        r.Name = req.Name.Trim(); r.StreetAddress = req.StreetAddress; r.City = req.City; r.State = req.State;
        r.Zip = req.Zip; r.Phone = req.Phone; r.IsActive = req.IsActive;
        await db.SaveChangesAsync();
        await audit.LogAsync(me, "RestaurantUpdated", "Restaurant", r.Id, new { before, after = RestaurantDto.From(r) }, r.Id);
        return RestaurantDto.From(r);
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = "Owner,SuperAdmin")]
    public async Task<IActionResult> Deactivate(int id)
    {
        var me = await users.GetAsync();
        var r = await db.Restaurants.FirstOrDefaultAsync(x => x.Id == id && x.GroupId == me.GroupId);
        if (r is null) return NotFound();
        r.IsActive = false;
        await db.SaveChangesAsync();
        await audit.LogAsync(me, "RestaurantDeactivated", "Restaurant", r.Id, new { r.Name }, r.Id);
        return NoContent();
    }
}
