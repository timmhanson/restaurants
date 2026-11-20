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
/// What owners and managers may do in an investment group, level by level (see PermissionPolicy).
///  - /api/system/groups/{id}/permissions: the System Administrator's answers for one group, which take precedence:
///    all can, none can, or the group's administrators decide.
///  - /api/permission-rules: the group's administrators' own answers, where the site left them the decision.
/// </summary>
[ApiController]
[Authorize]
public class PermissionRulesController(AppDbContext db, AuditService audit, CurrentUserProvider users) : ControllerBase
{
    [HttpGet("api/system/groups/{id:int}/permissions")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<ActionResult<PermissionRulesDto>> SiteGet(int id, CancellationToken ct)
    {
        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct);
        return group is null ? NotFound() : Dto(group, true);
    }

    [HttpPut("api/system/groups/{id:int}/permissions")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<ActionResult<PermissionRulesDto>> SitePut(int id, PermissionRulesRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null) return NotFound();
        var before = Snapshot(group);
        foreach (var row in req.Rows ?? [])
        {
            if (!Enum.TryParse<ManagerPermission>(row.Name, true, out var p) || !PermissionPolicy.Each_().Contains(p)) return BadRequest(new { message = $"There is no permission called {row.Name}." });
            if (row.Owners is not (null or PermissionPolicy.Yes or PermissionPolicy.No or PermissionPolicy.Group)) return BadRequest(new { message = "For owners choose: all owners can, no owner can, or the group decides." });
            if (row.Managers is not (null or PermissionPolicy.Yes or PermissionPolicy.No or PermissionPolicy.Group)) return BadRequest(new { message = "For managers choose: all managers can, no manager can, or the group decides." });
            if (row.Owners is { } o)
            {
                group.SiteOwnerYes = Set(group.SiteOwnerYes, p, o == PermissionPolicy.Yes);
                group.SiteOwnerNo = Set(group.SiteOwnerNo, p, o == PermissionPolicy.No);
            }
            if (row.Managers is { } m && PermissionPolicy.ForManagers.HasFlag(p))
            {
                group.SiteManagerYes = Set(group.SiteManagerYes, p, m == PermissionPolicy.Yes);
                group.SiteManagerNo = Set(group.SiteManagerNo, p, m == PermissionPolicy.No);
            }
        }
        await db.SaveChangesAsync(ct);
        await Audit(me, group, before, "PermissionRulesSetBySite");
        return Dto(group, true);
    }

    [HttpGet("api/permission-rules")]
    public async Task<ActionResult<PermissionRulesDto>> GroupGet(CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.IsGroupWide) return StatusCode(403, new { message = AdminsOnly });
        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == me.GroupId, ct);
        return group is null ? BadRequest(new { message = "Select an investment group first." }) : Dto(group, false);
    }

    [HttpPut("api/permission-rules")]
    public async Task<ActionResult<PermissionRulesDto>> GroupPut(PermissionRulesRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.IsGroupWide) return StatusCode(403, new { message = AdminsOnly });
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == me.GroupId, ct);
        if (group is null) return BadRequest(new { message = "Select an investment group first." });
        var before = Snapshot(group);
        var states = PermissionPolicy.States(group).ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var row in req.Rows ?? [])
        {
            if (!Enum.TryParse<ManagerPermission>(row.Name, true, out var p) || !states.TryGetValue(p.ToString(), out var now)) return BadRequest(new { message = $"There is no permission called {row.Name}." });
            if (row.Owners is not (null or PermissionPolicy.Yes or PermissionPolicy.No)) return BadRequest(new { message = "For owners choose: all owners can, or no owner can." });
            if (row.Managers is not (null or PermissionPolicy.Yes or PermissionPolicy.No or PermissionPolicy.Each)) return BadRequest(new { message = "For managers choose: all managers can, no manager can, or set for each manager." });
            if (row.Owners is { } o && o != now.GroupOwners)
            {
                if (now.SiteOwners != PermissionPolicy.Group) return StatusCode(403, new { message = $"“{now.Description}” is decided for owners by the people who run the site, so it cannot be changed here." });
                group.OwnerNo = Set(group.OwnerNo, p, o == PermissionPolicy.No);
            }
            if (row.Managers is { } m && now.ForManagers && m != now.GroupManagers)
            {
                if (now.SiteManagers != PermissionPolicy.Group) return StatusCode(403, new { message = $"“{now.Description}” is decided for managers by the people who run the site, so it cannot be changed here." });
                group.ManagerYes = Set(group.ManagerYes, p, m == PermissionPolicy.Yes);
                group.ManagerNo = Set(group.ManagerNo, p, m == PermissionPolicy.No);
            }
        }
        await db.SaveChangesAsync(ct);
        await Audit(me, group, before, "PermissionRulesChanged");
        return Dto(group, false);
    }

    private const string AdminsOnly = "Only the investment group's administrators decide what owners and managers may do.";

    private static ManagerPermission Set(ManagerPermission flags, ManagerPermission p, bool on) => on ? flags | p : flags & ~p;

    private static PermissionRulesDto Dto(OwnerGroup g, bool site) => new(g.Id, g.Name, site, PermissionPolicy.States(g));

    private static Dictionary<string, string> Snapshot(OwnerGroup g) =>
        PermissionPolicy.States(g).ToDictionary(s => s.Name, s => $"site: owners {s.SiteOwners}, managers {s.SiteManagers ?? "-"}; group: owners {s.GroupOwners}, managers {s.GroupManagers ?? "-"}");

    private async Task Audit(CurrentUser me, OwnerGroup g, Dictionary<string, string> before, string action)
    {
        var after = Snapshot(g);
        var changed = after.Where(kv => before[kv.Key] != kv.Value).Select(kv => new { permission = kv.Key, before = before[kv.Key], after = kv.Value }).ToList();
        if (changed.Count > 0) await audit.LogAsync(me, action, "Group", g.Id, new { Group = g.Name, changed }, groupId: g.Id);
    }
}

/// <summary>
/// The settings the System Administrator has left to an investment group's own administrators: each is one answer
/// for all the group's restaurants, or left to each restaurant. A setting the site has decided for the group is
/// shown, and cannot be changed here.
/// </summary>
[ApiController]
[Route("api/group-rules")]
[Authorize]
public class GroupRulesController(AppDbContext db, AuditService audit, CurrentUserProvider users, SiteMailing site) : ControllerBase
{
    private const string AdminsOnly = "Only the investment group's administrators change settings for all restaurants.";

    [HttpGet]
    public async Task<ActionResult<GroupRulesDto>> Get(CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is not int groupId) return BadRequest(new { message = "Select an investment group first." });
        if (!me.IsGroupWide) return StatusCode(403, new { message = AdminsOnly });
        return Dto(await db.GroupBilling.AsNoTracking().FirstOrDefaultAsync(x => x.GroupId == groupId, ct));
    }

    [HttpPut]
    public async Task<ActionResult<GroupRulesDto>> Put(GroupRulesRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is not int groupId) return BadRequest(new { message = "Select an investment group first." });
        if (!me.IsGroupWide) return StatusCode(403, new { message = AdminsOnly });
        if (req.Mail is not (null or MailChoices.On or MailChoices.Off or MailChoices.Each)) return BadRequest(new { message = "Choose whether cards can be mailed: for all restaurants, for none, or let each restaurant decide." });
        if (req.MailMode is not (null or MailChoices.Live or MailChoices.Test or MailChoices.Each)) return BadRequest(new { message = "Choose real mailing, test mailing, or let each restaurant decide." });
        if (req.UsualWay is not (null or MailChoices.Mail or MailChoices.Stamp or MailChoices.Each)) return BadRequest(new { message = "Choose how cards usually go out, or let each restaurant decide." });
        if ((req.SignatureForAll?.Trim().Length ?? 0) > 60) return BadRequest(new { message = "Keep the name that signs cards to 60 characters." });

        var b = await db.GroupBilling.FirstOrDefaultAsync(x => x.GroupId == groupId, ct);
        if (b is null) db.GroupBilling.Add(b = new GroupBilling { GroupId = groupId });
        var before = GroupPolicy.Snapshot(b);
        ObjectResult SetBySite(string what) => StatusCode(403, new { message = $"{what} is decided for your investment group by the people who run the site, so it cannot be changed here." });

        if (req.Mail is { } mail && mail != before.Mail)
        {
            if (!GroupPolicy.Decides(b, GroupChoice.Mail)) return SetBySite("Having cards printed and mailed");
            b.MailPerRestaurant = mail == MailChoices.Each;
            b.MailEnabled = mail == MailChoices.On;
        }
        if (req.MailMode is { } mode && mode != before.MailMode)
        {
            if (GroupPolicy.SiteMode(site.Billing) != MailChoices.Each || !GroupPolicy.Decides(b, GroupChoice.Mode)) return SetBySite("Real or test mailing");
            b.MailMode = mode;
        }
        if (req.UsualWay is { } usual && usual != before.UsualWay)
        {
            if (!GroupPolicy.Decides(b, GroupChoice.Usual)) return SetBySite("How cards usually go out");
            b.UsualWay = usual;
        }
        if (req.Signature is not null)
        {
            var name = req.Signature == "all" ? (string.IsNullOrWhiteSpace(req.SignatureForAll) ? null : req.SignatureForAll.Trim()) : null;
            if (req.Signature is not ("all" or MailChoices.Each)) return BadRequest(new { message = "Choose one name for all restaurants, or let each restaurant decide." });
            if (name != before.SignatureForAll)
            {
                if (!GroupPolicy.Decides(b, GroupChoice.Signature)) return SetBySite("The name that signs for the team login");
                b.SignatureForAll = name;
            }
        }
        await db.SaveChangesAsync(ct);
        var after = GroupPolicy.Snapshot(b);
        if (!before.Equals(after))
            await audit.LogAsync(me, "GroupSettingsChanged", "Group", groupId, new { before, after }, groupId: groupId);
        if (before.Mail != after.Mail)
            await audit.LogAsync(me, after.Mail == MailChoices.Off ? "GroupMailingSwitchedOff" : "GroupMailingSwitchedOn", "Group", groupId, new { after.Mail }, groupId: groupId);
        return Dto(b);
    }

    private GroupRulesDto Dto(GroupBilling? b) =>
        new(GroupPolicy.Switch(b), GroupPolicy.Mode(b), GroupPolicy.SiteMode(site.Billing), GroupPolicy.Usual(b), GroupPolicy.SignatureForAll(b), GroupPolicy.DecidedByGroup(b));
}
