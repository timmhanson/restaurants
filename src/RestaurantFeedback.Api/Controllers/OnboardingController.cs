using System.Text.Json;
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
/// Getting a new investment group up and running. The System Administrator has already talked with the group and may
/// have set things up for it (restaurants, people, mailing, the AI assistant); the group's own administrator, at
/// their first sign-in, is welcomed and walked through a short wizard that shows what is already there for them to
/// confirm or change. It is never forced: they can look around first and come back, and every step can be left for
/// later. The only thing the site cannot work without is one restaurant, so that is all "finished" asks for.
///
/// This controller keeps where the group has got to (GroupOnboarding); the steps themselves change things through
/// the same endpoints the ordinary screens use, with the same permissions and audit rows.
/// </summary>
public static class Onboarding
{
    public const string New = "new", Open = "open", Done = "done", None = "none";
    public const string Group = "group", Restaurants = "restaurants", Offers = "offers", Sending = "sending", People = "people", Assistant = "assistant";

    /// <summary>The steps in order. Only the restaurants step is needed before the site can be used.</summary>
    public static readonly (string Id, string Title, bool Required)[] Steps =
    [
        (Group, "Your investment group", false), (Restaurants, "Your restaurants", true), (Offers, "What you offer guests", false),
        (Sending, "How cards are sent", false), (People, "Your people", false), (Assistant, "AI assistant", false),
    ];

    public record Confirmed(DateTime At, string By);

    public static Dictionary<string, Confirmed> Read(GroupOnboarding? row)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, Confirmed>>(row?.StepsJson ?? "{}") ?? []; }
        catch (JsonException) { return []; }
    }

    public static string Status(GroupOnboarding? row) =>
        row?.CompletedAt is not null ? Done : row is null || (row.StartedAt is null && row.LaterAt is null) ? New : Open;
}

[ApiController]
[Route("api/onboarding")]
[Authorize]
public class OnboardingController(AppDbContext db, CurrentUserProvider users, IGroupSettingsStore settingsStore, MailLedger ledger, AiOptions aiOptions, IAiDrafter drafter, AuditService audit) : ControllerBase
{
    /// <summary>The group's administrators run its set-up (a System Administrator can too, while viewing the group as one).</summary>
    private static bool CanRun(CurrentUser me) => me.IsGroupWide && me.GroupId is not null;

    private async Task<OnboardingDto> Now(CurrentUser me, CancellationToken ct)
    {
        var groupId = me.GroupId!.Value;
        var group = await db.Groups.AsNoTracking().FirstAsync(g => g.Id == groupId, ct);
        var row = await db.GroupOnboardings.AsNoTracking().FirstOrDefaultAsync(o => o.GroupId == groupId, ct);
        var settings = await settingsStore.GetAsync(groupId, ct);
        var billing = await db.GroupBilling.AsNoTracking().FirstOrDefaultAsync(b => b.GroupId == groupId, ct);
        var restaurants = await db.Restaurants.AsNoTracking().Where(r => r.GroupId == groupId && r.IsActive).OrderBy(r => r.Name).ToListAsync(ct);
        var people = await db.Users.AsNoTracking().Where(u => u.GroupId == groupId && u.IsActive && u.Role != UserRole.SuperAdmin).OrderBy(u => u.Role).ThenBy(u => u.DisplayName)
            .Select(u => new OnboardingPerson(u.DisplayName, RoleTitles.Of(u.Role), u.LastLoginAt == null)).ToListAsync(ct);
        var terms = await ledger.TermsAsync(groupId, ct);
        var ai = AiPolicy.For(billing, aiOptions);
        // The assistant is part of set-up only where the group has been offered it.
        var offered = drafter.Configured && (ai.On || ai.DecidedBy == MailChoices.Group);
        var confirmed = Onboarding.Read(row);
        var steps = Onboarding.Steps.Where(s => s.Id != Onboarding.Assistant || offered)
            .Select(s => new OnboardingStepDto(s.Id, s.Title, confirmed.ContainsKey(s.Id), confirmed.GetValueOrDefault(s.Id)?.At, confirmed.GetValueOrDefault(s.Id)?.By, s.Required)).ToList();
        return new OnboardingDto(Onboarding.Status(row), true, group.Name, string.IsNullOrWhiteSpace(settings.GroupDisplayName) ? group.Name : settings.GroupDisplayName,
            string.IsNullOrWhiteSpace(row?.WelcomeNote) ? null : row.WelcomeNote, row?.WelcomeNoteBy, steps, restaurants.Count > 0,
            restaurants.Select(r => new OnboardingRestaurant(r.Id, r.Name, r.StreetAddress, r.City, r.State, r.Zip, r.Phone)).ToList(), people,
            new OnboardingSending(GroupPolicy.Switch(billing), GroupPolicy.Decides(billing, GroupChoice.Mail), GroupPolicy.Mode(billing), terms.CostCents + terms.FeeCents, terms.PrintFeeCents,
                terms.HasPayMethod, terms.PayRequired, GroupPolicy.SignatureForAll(billing)),
            offered ? new OnboardingAssistant(ai.On, ai.DecidedBy == MailChoices.Group, ai.Level.Label, AiLevels.Price(ai.Level.PriceMills)) : null,
            settings.CouponOffers, settings.ReasonCategories, row?.CompletedAt is { } done ? DateTime.SpecifyKind(done, DateTimeKind.Utc) : null);
    }

    /// <summary>Where the group's set-up stands. For anyone but the group's administrators it says only that there is nothing for them to do.</summary>
    [HttpGet]
    public async Task<OnboardingDto> Get(CancellationToken ct)
    {
        var me = await users.GetAsync();
        return CanRun(me) ? await Now(me, ct) : OnboardingDto.NotMine;
    }

    private async Task<GroupOnboarding> Row(int groupId, CancellationToken ct)
    {
        var row = await db.GroupOnboardings.FirstOrDefaultAsync(o => o.GroupId == groupId, ct);
        if (row is null) db.GroupOnboardings.Add(row = new GroupOnboarding { GroupId = groupId });
        return row;
    }

    /// <summary>"Set up now": the wizard has been opened.</summary>
    [HttpPost("begin")]
    public async Task<ActionResult<OnboardingDto>> Begin(CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!CanRun(me)) return Forbid();
        var row = await Row(me.GroupId!.Value, ct);
        if (row.StartedAt is null)
        {
            row.StartedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(me, "SetUpStarted", "Group", me.GroupId);
        }
        return await Now(me, ct);
    }

    /// <summary>"Look around first" or "Finish later": the site opens, and set-up waits under Getting Started.</summary>
    [HttpPost("later")]
    public async Task<ActionResult<OnboardingDto>> Later(CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!CanRun(me)) return Forbid();
        var row = await Row(me.GroupId!.Value, ct);
        var first = row.LaterAt is null;
        row.LaterAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        if (first) await audit.LogAsync(me, "SetUpLeftForLater", "Group", me.GroupId);
        return await Now(me, ct);
    }

    /// <summary>A step has been looked at and is right (as it was set up for them, or as they changed it).</summary>
    [HttpPost("steps/{id}/confirm")]
    public async Task<ActionResult<OnboardingDto>> Confirm(string id, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!CanRun(me)) return Forbid();
        if (Onboarding.Steps.All(s => s.Id != id)) return NotFound();
        var groupId = me.GroupId!.Value;
        if (id == Onboarding.Restaurants && !await db.Restaurants.AnyAsync(r => r.GroupId == groupId && r.IsActive, ct))
            return BadRequest(new { message = "Add at least one restaurant first. It is the one thing the site cannot work without." });
        var row = await Row(groupId, ct);
        row.StartedAt ??= DateTime.UtcNow;
        var steps = Onboarding.Read(row);
        steps[id] = new Onboarding.Confirmed(DateTime.UtcNow, me.DisplayName);
        row.StepsJson = JsonSerializer.Serialize(steps);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, "SetUpStepConfirmed", "Group", groupId, new { Step = Onboarding.Steps.First(s => s.Id == id).Title });
        return await Now(me, ct);
    }

    /// <summary>Set-up is finished. It needs one restaurant; every other step can still be done from its own screen.</summary>
    [HttpPost("finish")]
    public async Task<ActionResult<OnboardingDto>> Finish(CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!CanRun(me)) return Forbid();
        var groupId = me.GroupId!.Value;
        if (!await db.Restaurants.AnyAsync(r => r.GroupId == groupId && r.IsActive, ct))
            return BadRequest(new { message = "Add at least one restaurant before finishing. It is the one thing the site cannot work without." });
        var row = await Row(groupId, ct);
        if (row.CompletedAt is null)
        {
            (row.CompletedAt, row.CompletedBy) = (DateTime.UtcNow, me.DisplayName);
            row.StartedAt ??= row.CompletedAt;
            await db.SaveChangesAsync(ct);
            var left = Onboarding.Steps.Where(s => !Onboarding.Read(row).ContainsKey(s.Id)).Select(s => s.Title).ToArray();
            await audit.LogAsync(me, "SetUpFinished", "Group", groupId, new { NotConfirmed = left });
        }
        return await Now(me, ct);
    }
}

/// <summary>The System Administrator's side: a welcome note for the group's administrator, and where its set-up stands.</summary>
[ApiController]
[Route("api/system/groups/{id:int}/onboarding")]
[Authorize(Roles = "SuperAdmin")]
public class SystemOnboardingController(AppDbContext db, CurrentUserProvider users, AuditService audit) : ControllerBase
{
    private async Task<SystemOnboardingDto> Now(int id, CancellationToken ct)
    {
        var row = await db.GroupOnboardings.AsNoTracking().FirstOrDefaultAsync(o => o.GroupId == id, ct);
        var steps = Onboarding.Read(row);
        static DateTime? Utc(DateTime? d) => d is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
        return new SystemOnboardingDto(id, Onboarding.Status(row), row?.WelcomeNote, Utc(row?.StartedAt), Utc(row?.LaterAt), Utc(row?.CompletedAt), row?.CompletedBy,
            Onboarding.Steps.Select(s => new OnboardingStepDto(s.Id, s.Title, steps.ContainsKey(s.Id), steps.GetValueOrDefault(s.Id)?.At, steps.GetValueOrDefault(s.Id)?.By, s.Required)).ToList());
    }

    [HttpGet]
    public async Task<ActionResult<SystemOnboardingDto>> Get(int id, CancellationToken ct) => await db.Groups.AnyAsync(g => g.Id == id, ct) ? await Now(id, ct) : NotFound();

    /// <summary>
    /// Saves the welcome note the group's administrator reads on their first screen. Restart puts the group back to
    /// "not started" so its administrator is welcomed again (what they have set up is not touched).
    /// </summary>
    [HttpPut]
    public async Task<ActionResult<SystemOnboardingDto>> Put(int id, SystemOnboardingRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!await db.Groups.AnyAsync(g => g.Id == id, ct)) return NotFound();
        var row = await db.GroupOnboardings.FirstOrDefaultAsync(o => o.GroupId == id, ct);
        if (row is null) db.GroupOnboardings.Add(row = new GroupOnboarding { GroupId = id });
        if (req.WelcomeNote is not null)
        {
            var note = req.WelcomeNote.Trim();
            (row.WelcomeNote, row.WelcomeNoteBy) = (note.Length == 0 ? null : note, note.Length == 0 ? null : me.DisplayName);
        }
        if (req.Restart) (row.StartedAt, row.LaterAt, row.CompletedAt, row.CompletedBy, row.StepsJson) = (null, null, null, null, "{}");
        if (req.MarkDone && row.CompletedAt is null) (row.CompletedAt, row.CompletedBy) = (DateTime.UtcNow, me.DisplayName);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, "SetUpChangedBySite", "Group", id, new { NoteChanged = req.WelcomeNote is not null, req.Restart, req.MarkDone }, groupId: id);
        return await Now(id, ct);
    }
}
