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
/// An investment group's settings as the System Administrator makes them, in one place: each is either one answer
/// for all the group's restaurants ("X for all restaurants") or left to each restaurant ("Each restaurant
/// decides"), and each restaurant's own choices can be set here too. What the group fixes takes precedence over a
/// restaurant's choice (see GroupPolicy).
/// </summary>
[ApiController]
[Route("api/system/groups/{id:int}/settings")]
[Authorize(Roles = "SuperAdmin")]
public class SystemGroupSettingsController(AppDbContext db, AuditService audit, CurrentUserProvider users, SiteMailing site, BillingService billing) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<GroupSettingsDto>> Get(int id, CancellationToken ct)
    {
        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct);
        return group is null ? NotFound() : await Dto(group, ct);
    }

    [HttpPut]
    public async Task<ActionResult<GroupSettingsDto>> Put(int id, GroupSettingsRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null) return NotFound();
        if (req.Mail is not (null or MailChoices.On or MailChoices.Off or MailChoices.Each)) return BadRequest(new { message = "Choose whether cards can be mailed: for all restaurants, for none, or let each restaurant decide." });
        if (req.MailMode is not (null or MailChoices.Live or MailChoices.Test or MailChoices.Each)) return BadRequest(new { message = "Choose real mailing, test mailing, or let each restaurant decide." });
        if (req.UsualWay is not (null or MailChoices.Mail or MailChoices.Stamp or MailChoices.Each)) return BadRequest(new { message = "Choose how cards usually go out, or let each restaurant decide." });
        if (req.PayRule is not (null or "site" or "required" or "notrequired")) return BadRequest(new { message = "Choose whether this group needs a payment method before mailing." });
        if ((req.SignatureForAll?.Trim().Length ?? 0) > 60) return BadRequest(new { message = "Keep the name that signs cards to 60 characters." });

        var b = await db.GroupBilling.FirstOrDefaultAsync(x => x.GroupId == id, ct);
        if (b is null) db.GroupBilling.Add(b = new GroupBilling { GroupId = id });
        var before = GroupPolicy.Snapshot(b);
        if (req.GroupDecides is not null) b.GroupDecides = GroupPolicy.ParseDecides(req.GroupDecides);
        if (req.Mail is not null)
        {
            b.MailPerRestaurant = req.Mail == MailChoices.Each;
            b.MailEnabled = req.Mail == MailChoices.On;
        }
        if (req.MailMode is not null) b.MailMode = req.MailMode;
        if (req.UsualWay is not null) b.UsualWay = req.UsualWay;
        b.SignatureForAll = string.IsNullOrWhiteSpace(req.SignatureForAll) ? null : req.SignatureForAll.Trim();
        if (req.PayRule is not null) b.RequirePayMethod = req.PayRule switch { "required" => true, "notrequired" => false, _ => null };

        // Each restaurant's own choices. They count where the group leaves the decision to the restaurant, and are kept either way.
        var changed = new List<object>();
        if (req.Restaurants is { Count: > 0 } wanted)
        {
            var ids = wanted.Select(w => w.Id).ToList();
            var restaurants = await db.Restaurants.Where(r => r.GroupId == id && ids.Contains(r.Id)).ToListAsync(ct);
            if (restaurants.Count != ids.Distinct().Count()) return BadRequest(new { message = "One of the restaurants is not in this investment group." });
            foreach (var r in restaurants)
            {
                var w = wanted.First(x => x.Id == r.Id);
                var was = new { r.MailOn, r.MailLive, r.MailByDefault, r.SignatureName };
                r.MailOn = w.MailOn;
                r.MailLive = w.MailLive;
                r.MailByDefault = w.MailByDefault;
                r.SignatureName = string.IsNullOrWhiteSpace(w.SignatureName) ? null : w.SignatureName.Trim();
                var now = new { r.MailOn, r.MailLive, r.MailByDefault, r.SignatureName };
                if (!was.Equals(now)) changed.Add(new { r.Id, r.Name, before = was, after = now });
            }
        }
        await db.SaveChangesAsync(ct);

        var after = GroupPolicy.Snapshot(b);
        if (!before.Equals(after))
            await audit.LogAsync(me, "GroupSettingsChanged", "Group", id, new { Group = group.Name, before, after }, groupId: id);
        if (before.Mail != after.Mail)
            await audit.LogAsync(me, after.Mail == MailChoices.Off ? "GroupMailingSwitchedOff" : "GroupMailingSwitchedOn", "Group", id, new { Group = group.Name, after.Mail }, groupId: id);
        if (changed.Count > 0)
            await audit.LogAsync(me, "RestaurantSettingsChanged", "Group", id, new { Group = group.Name, restaurants = changed }, groupId: id);
        return await Dto(group, ct);
    }

    private async Task<GroupSettingsDto> Dto(OwnerGroup group, CancellationToken ct)
    {
        var s = site.Billing;
        var b = await db.GroupBilling.AsNoTracking().FirstOrDefaultAsync(x => x.GroupId == group.Id, ct);
        var restaurants = await db.Restaurants.AsNoTracking().Where(r => r.GroupId == group.Id).OrderBy(r => r.Name).ToListAsync(ct);
        return new GroupSettingsDto(group.Id, group.Name, GroupPolicy.Switch(b), GroupPolicy.Mode(b), GroupPolicy.SiteMode(s), GroupPolicy.Usual(b), GroupPolicy.SignatureForAll(b),
            b?.RequirePayMethod is null ? "site" : b.RequirePayMethod.Value ? "required" : "notrequired", s.RequirePaymentMethod, site.Test is not null, site.Live is not null,
            await billing.CreditBalanceAsync(group.Id, ct), b?.PayLabel,
            restaurants.Select(r => new RestaurantSettingsDto(r.Id, r.Name, r.IsActive, r.MailOn, r.MailLive, r.MailByDefault, r.SignatureName, GroupPolicy.For(s, b, r))).ToList())
        { GroupDecides = GroupPolicy.DecidedByGroup(b) };
    }
}
