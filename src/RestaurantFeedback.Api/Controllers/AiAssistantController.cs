using System.Globalization;
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
/// The AI assistant on Enter Card: a person types what happened, presses Process, and the card is filled in for them
/// to check, with a face for how upset the guest was (which stands for one of the group's rewards) and, when asked
/// for, an email to the guest.
///
/// It spends the group's money, a little each time, so: it is off for every group until the System Administrator
/// switches it on or leaves that to the group; the price is on the button; a group cannot be charged past its
/// monthly limit; a press that arrives twice is answered and charged once; a request that fails is not charged; and
/// every press is kept (AiRequest) with who, when, what was typed and the card it led to, if any. Pressing Process
/// needs the UseAiAssistant permission, which owners and managers hold as the levels above allow; the shared team
/// login never does, so it does not see the assistant. What was typed is never
/// written to the audit or interaction logs. Groups are never told which company's model is behind a level.
/// </summary>
[ApiController]
[Route("api/ai")]
[Authorize]
public class AiAssistantController(AppDbContext db, CurrentUserProvider users, IGroupSettingsStore settingsStore, IAiDrafter drafter, AiLedger ledger, AiOptions options, AuditService audit) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static List<AiFaceDto> Faces(GroupSettings settings)
    {
        var rewards = AiMoods.Rewards(settings);
        return AiMoods.All.Select((m, i) => new AiFaceDto(m.Level, m.Face, m.Label, m.Meaning, rewards[i])).ToList();
    }

    /// <param name="max">The highest level the group may choose; those above it are marked not allowed.</param>
    public static List<AiLevelDto> Levels(AiLevel? max = null) =>
        AiLevels.All.Select(l => new AiLevelDto(l.Id, l.Label, l.Says, l.PriceMills, AiLevels.Price(l.PriceMills)) { Allowed = max is null || AiLevels.Within(l, max) }).ToList();

    private Task<GroupBilling?> Row(int groupId, CancellationToken ct) => db.GroupBilling.FirstOrDefaultAsync(b => b.GroupId == groupId, ct);

    /// <summary>Whether the card form shows the assistant for this restaurant, and what a press costs. It never says why it is off.</summary>
    [HttpGet("config")]
    public async Task<AiConfigDto> Config([FromQuery] int restaurantId, CancellationToken ct)
    {
        var me = await users.GetAsync();
        var off = new AiConfigDto(false, null, 0, null, []);
        if (me.GroupId is not int groupId || !me.CanAccessRestaurant(restaurantId) || !drafter.Configured || !me.Has(ManagerPermission.UseAiAssistant)) return off;
        var rules = AiPolicy.For(await Row(groupId, ct), options);
        if (!rules.On) return off;
        return new AiConfigDto(true, rules.Level.Label, rules.Level.PriceMills, AiLevels.Price(rules.Level.PriceMills), Faces(await settingsStore.GetAsync(groupId, ct)));
    }

    [HttpPost("process")]
    public async Task<ActionResult<AiProcessResponse>> Process(AiProcessRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is not int groupId || !me.CanAccessRestaurant(req.RestaurantId)) return Forbid();
        if (!me.Has(ManagerPermission.UseAiAssistant)) return StatusCode(403, new { message = "You have not been given permission to use the AI assistant. Fill in the card yourself, or ask your owner or administrator." });
        var key = req.RequestKey.Trim();
        var settings = await settingsStore.GetAsync(groupId, ct);
        var faces = Faces(settings);

        // The same press arriving again (a retry, a double click): the first answer, and no second charge.
        var earlier = await db.AiRequests.AsNoTracking().FirstOrDefaultAsync(r => r.RequestKey == key, ct);
        if (earlier is not null)
        {
            if (earlier.GroupId != groupId || earlier.Outcome != AiOutcomes.Ok || earlier.ResultJson is null)
                return BadRequest(new { message = "That try did not finish. Press Process again." });
            var before = JsonSerializer.Deserialize<AiFields>(earlier.ResultJson, Json)!;
            return new AiProcessResponse(key, earlier.PriceMills, AiLevels.Price(earlier.PriceMills), before, earlier.Offer, faces);
        }

        var text = req.Text.Trim();
        if (text.Length < 15) return BadRequest(new { message = "Describe what happened in a sentence or two first." });
        var rules = AiPolicy.For(await Row(groupId, ct), options);
        if (!rules.On || !drafter.Configured) return StatusCode(403, new { message = "The AI assistant is not switched on for this investment group." });
        var restaurant = await db.Restaurants.AsNoTracking().FirstOrDefaultAsync(r => r.Id == req.RestaurantId && r.GroupId == groupId, ct);
        if (restaurant is null) return NotFound();

        var level = rules.Level;
        var (_, used) = await ledger.ThisMonthAsync(groupId, ct);
        if (used + level.PriceMills > rules.LimitCents * 10)
        {
            await audit.LogAsync(me, "AiAssistantOverLimit", "Group", groupId, new { UsedMills = used, rules.LimitCents, Level = level.Label }, restaurant.Id);
            return BadRequest(new
            {
                message = $"This investment group has reached its limit for the AI assistant this month ({BillingService.Dollars(rules.LimitCents)}). Fill in the card yourself, or ask the site's administrator to raise the limit."
            });
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // An email to the guest is always drafted: it costs the group nothing extra, so it is not a choice.
        var ask = new AiAsk(text, true, restaurant.Name, settings.ReasonCategories, CommentSources.All, AiMoods.All.Select((m, i) => (m, faces[i].Reward)).ToList(), today);
        var entry = new AiRequest
        {
            RequestKey = key, GroupId = groupId, RestaurantId = restaurant.Id, UserId = me.Id, UserName = me.DisplayName, Level = level.Id, Model = level.Model,
            WantEmail = true, Input = text
        };
        AiAnswer answer;
        try
        {
            answer = await drafter.AskAsync(level, ask, ct);
        }
        catch (AiException ex)
        {
            // Kept, and not charged.
            (entry.Outcome, entry.Error) = (AiOutcomes.Failed, ex.ForAdmin);
            db.AiRequests.Add(entry);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(me, "AiAssistantFailed", "AiRequest", entry.Id, new { Level = level.Label, Charged = false }, restaurant.Id);
            return StatusCode(502, new { message = ex.Message });
        }

        // Only what fits the card is kept: a reason and source from the group's lists, a real day that is not in the future.
        var f = answer.Fields;
        var mood = Math.Clamp(f.Mood, 1, AiMoods.All.Length);
        var visit = DateOnly.TryParseExact(f.VisitDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d <= today.AddDays(1) && d >= today.AddYears(-2) ? f.VisitDate : null;
        static string? Kept(string? s, int most) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > most ? s.Trim()[..most] : s.Trim();
        var fields = f with
        {
            GuestFirstName = Kept(f.GuestFirstName, 100), GuestLastName = Kept(f.GuestLastName, 100), Phone = Kept(f.Phone, 30), Email = Kept(f.Email, 200),
            StreetAddress = Kept(f.StreetAddress, 200), City = Kept(f.City, 100), State = UsStates.Code(f.State), Zip = Kept(f.Zip, 20), VisitDate = visit,
            Source = CommentSources.Match(f.Source), Reason = settings.ReasonCategories.FirstOrDefault(r => r.Equals(f.Reason?.Trim(), StringComparison.OrdinalIgnoreCase)),
            Details = Kept(f.Details, 2000), Mood = mood, MoodWhy = Kept(f.MoodWhy, 300),
            EmailSubject = Kept(f.EmailSubject, 200), EmailBody = Kept(f.EmailBody, 4000)
        };
        var offer = faces[mood - 1].Reward;

        entry.Outcome = AiOutcomes.Ok;
        entry.PriceMills = level.PriceMills;
        (entry.InputTokens, entry.OutputTokens) = (answer.InputTokens, answer.OutputTokens);
        entry.CostMicros = (int)Math.Round(answer.InputTokens * level.InMicrosPerToken + answer.OutputTokens * level.OutMicrosPerToken);
        entry.ResultJson = JsonSerializer.Serialize(fields, Json);
        entry.GuestName = Kept($"{fields.GuestFirstName} {fields.GuestLastName}", 200);
        (entry.Reason, entry.Mood, entry.Offer) = (fields.Reason, mood, offer);
        db.AiRequests.Add(entry);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, "AiAssistantUsed", "AiRequest", entry.Id, new { Level = level.Label, ChargedCents = level.PriceMills / 10m, Face = mood, Reward = offer }, restaurant.Id);
        return new AiProcessResponse(key, level.PriceMills, AiLevels.Price(level.PriceMills), fields, offer, faces);
    }

    // ---- The group's own settings and its record of requests ------------------------------------------------------

    private static bool CanChange(CurrentUser me) => me.IsGroupWide || me.Has(ManagerPermission.ManageAiAssistant);

    private async Task<AiSettingsDto> SettingsNow(CurrentUser me, int groupId, CancellationToken ct)
    {
        var rules = AiPolicy.For(await Row(groupId, ct), options);
        var settings = await settingsStore.GetAsync(groupId, ct);
        var (requests, mills) = await ledger.ThisMonthAsync(groupId, ct);
        return new AiSettingsDto(rules.On, rules.DecidedBy, CanChange(me), drafter.Configured, rules.Level.Id, Levels(rules.Max), rules.LimitCents, mills, requests, Faces(settings), settings.CouponOffers) { MaxLevel = rules.Max.Id };
    }

    [HttpGet("settings")]
    public async Task<ActionResult<AiSettingsDto>> Settings(CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.IsAdmin || me.GroupId is not int groupId) return Forbid();
        return await SettingsNow(me, groupId, ct);
    }

    /// <summary>
    /// The group's choices: the level, the reward each face stands for and, where the System Administrator left it
    /// to the group, whether the assistant is on. Each change is saved as it is made.
    /// </summary>
    [HttpPut("settings")]
    public async Task<ActionResult<AiSettingsDto>> SetSettings(AiSettingsRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is not int groupId || !me.IsAdmin || !CanChange(me)) return Forbid();
        var row = await Row(groupId, ct);
        if (row is null) db.GroupBilling.Add(row = new GroupBilling { GroupId = groupId });
        var before = new { On = row.AiEnabled, Level = AiLevels.Of(row.AiLevel).Label };
        if (req.On is bool on && on != row.AiEnabled)
        {
            if (!GroupPolicy.Decides(row, GroupChoice.Ai))
                return StatusCode(403, new { message = "Whether the AI assistant is on is decided for this investment group by the people who run the site." });
            row.AiEnabled = on;
        }
        if (req.Level is not null)
        {
            if (!AiLevels.Is(req.Level)) return BadRequest(new { message = "Choose one of the levels." });
            var max = AiLevels.Max(row.AiMaxLevel);
            if (!AiLevels.Within(AiLevels.Of(req.Level), max))
                return StatusCode(403, new { message = $"The highest level this investment group can choose is {max.Label}. That is decided by the people who run the site." });
            row.AiLevel = req.Level;
        }
        await db.SaveChangesAsync(ct);
        object? rewards = null;
        if (req.Rewards is not null)
        {
            var settings = await settingsStore.GetAsync(groupId, ct);
            if (req.Rewards.Count != AiMoods.All.Length || req.Rewards.Any(r => !string.IsNullOrWhiteSpace(r) && !settings.CouponOffers.Contains(r, StringComparer.OrdinalIgnoreCase)))
                return BadRequest(new { message = "Choose a reward from this group's list of offers for each face." });
            settings.AiRewards = req.Rewards.Select(r => r?.Trim() ?? "").ToList();
            (settings.UpdatedAt, settings.UpdatedBy) = (DateTime.UtcNow, me.DisplayName);
            await settingsStore.SaveAsync(groupId, settings, ct);
            rewards = AiMoods.All.Select((m, i) => new { m.Label, Reward = settings.AiRewards[i] });
        }
        await audit.LogAsync(me, "AiAssistantSettingsChanged", "Group", groupId, new { before, after = new { On = row.AiEnabled, Level = AiLevels.Of(row.AiLevel).Label }, rewards });
        return await SettingsNow(me, groupId, ct);
    }

    /// <summary>
    /// The group's requests in a month (yyyy-MM, this month when left out), newest first, each with the card it was
    /// for or "no card saved". For the people who see the group's bills; owners see their own restaurants'.
    /// </summary>
    [HttpGet("requests")]
    public async Task<ActionResult<AiUsageList>> Requests([FromQuery] string? month, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.IsAdmin || me.GroupId is not int groupId) return Forbid();
        var range = MailLedger.Range(month) ?? MailLedger.Range(MailLedger.MonthKey(DateTime.UtcNow))!.Value;
        var mine = me.RestaurantIds.ToList();
        var rows = await db.AiRequests.AsNoTracking().Where(r => r.GroupId == groupId && r.At >= range.From && r.At < range.To && (me.IsGroupWide || mine.Contains(r.RestaurantId)))
            .OrderByDescending(r => r.At).Take(2000).ToListAsync(ct);
        var names = await db.Restaurants.AsNoTracking().Where(r => r.GroupId == groupId).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var list = rows.Select(r => Row(r, names.GetValueOrDefault(r.RestaurantId, ""), null, false)).ToList();
        var charged = rows.Where(r => r.Outcome == AiOutcomes.Ok).ToList();
        return new AiUsageList(rows.Count, charged.Count, charged.Sum(r => r.PriceMills), charged.Count(r => r.CommentId is null), list);
    }

    public static AiRequestRow Row(AiRequest r, string restaurant, string? group, bool forSite)
    {
        var row = new AiRequestRow(r.Id, DateTime.SpecifyKind(r.At, DateTimeKind.Utc), r.UserName, r.GroupId, group, r.RestaurantId, restaurant, AiLevels.Of(r.Level).Label, r.PriceMills, r.Outcome,
            r.WantEmail, r.CommentId, r.GuestName, r.Reason, r.Mood, r.Offer);
        return forSite ? row with { Input = r.Input, Model = r.Model, InputTokens = r.InputTokens, OutputTokens = r.OutputTokens, CostMicros = r.CostMicros, Error = r.Error } : row;
    }
}

/// <summary>
/// The System Administrator's side of the AI assistant: the site's key (Connected Systems), each group's switch,
/// level and monthly limit, and how the assistant is being used and by which groups.
/// </summary>
[ApiController]
[Route("api/system")]
[Authorize(Roles = "SuperAdmin")]
public class SystemAiController(AppDbContext db, CurrentUserProvider users, IAiDrafter drafter, AiAccountStore account, AiLedger ledger, AiOptions options, AuditService audit) : ControllerBase
{
    private AiSiteDto Site()
    {
        var a = account.Current;
        var real = drafter is ClaudeDrafter;
        var levels = AiLevels.All.Select(l => new AiSiteLevelDto(l.Id, l.Label, l.ModelName, l.PriceMills, AiLevels.Price(l.PriceMills), l.InMicrosPerToken, l.OutMicrosPerToken)).ToList();
        return a is null
            ? new AiSiteDto(false, "None", null, null, null, real, levels, options.DefaultMonthlyLimitCents)
            : new AiSiteDto(true, a.Source, a.ApiKey.Length >= 4 ? a.ApiKey[^4..] : null, a.SavedAt, a.SavedBy, real, levels, options.DefaultMonthlyLimitCents);
    }

    [HttpGet("ai")]
    public AiSiteDto Get() => Site();

    /// <summary>Saves the Anthropic API key, encrypted; it is never shown again or written to any log.</summary>
    [HttpPut("ai")]
    public async Task<ActionResult<AiSiteDto>> SetKey(AiKeyRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        var key = (req.ApiKey ?? "").Trim();
        if (key.Length is < 20 or > 300 || key.Any(char.IsWhiteSpace))
            return BadRequest(new { message = "Enter the API key exactly as Anthropic's Console shows it (it begins sk-ant-), with no spaces." });
        await account.SaveAsync(key, me.DisplayName, ct);
        await audit.LogAsync(me, "AiAssistantKeySet", details: new { KeyEnding = key[^4..] });
        return Site();
    }

    [HttpDelete("ai")]
    public async Task<ActionResult<AiSiteDto>> RemoveKey(CancellationToken ct)
    {
        var me = await users.GetAsync();
        var before = account.Current;
        await account.RemoveAsync(ct);
        if (before?.Source == "Saved in the site") await audit.LogAsync(me, "AiAssistantKeyRemoved", details: new { KeyEnding = before.ApiKey[^4..] });
        return Site();
    }

    /// <summary>Asks the quickest level to read one made-up comment, to prove the key works. Costs a fraction of a cent; no group is charged.</summary>
    [HttpPost("ai/test")]
    public async Task<ServiceTestResult> Test(CancellationToken ct)
    {
        var me = await users.GetAsync();
        (bool Ok, string Message) result;
        try
        {
            var level = AiLevels.Of(AiLevels.Quick);
            var answer = await drafter.AskAsync(level, new AiAsk("Pat Example called about yesterday's visit: the fries were cold and the order took twenty minutes. Calm, but disappointed.", false, "Test Restaurant",
                ["Wait time", "Food temperature", "Other"], CommentSources.All, AiMoods.All.Select(m => (m, (string?)null)).ToList(), DateOnly.FromDateTime(DateTime.UtcNow)), ct);
            result = (true, $"The assistant answered: it read the test comment as “{answer.Fields.Reason}”, face {Math.Clamp(answer.Fields.Mood, 1, 5)} of 5. The key is working. ({answer.InputTokens + answer.OutputTokens} tokens used; no group is charged for a test.)");
        }
        catch (AiException ex) { result = (false, ex.ForAdmin); }
        await audit.LogAsync(me, result.Ok ? "AiAssistantTestPassed" : "AiAssistantTestFailed", details: new { message = result.Message });
        return new ServiceTestResult(result.Ok, result.Message);
    }

    // ---- One group: on or off (or left to the group), its level and its monthly limit ----------------------------

    private async Task<AiGroupDto> GroupNow(int id, GroupBilling? row, CancellationToken ct)
    {
        var rules = AiPolicy.For(row, options);
        var (requests, mills) = await ledger.ThisMonthAsync(id, ct);
        return new AiGroupDto(id, rules.On, rules.DecidedBy == MailChoices.Group, rules.Level.Id, row?.AiMonthlyLimitCents, options.DefaultMonthlyLimitCents, mills, requests, AiAssistantController.Levels()) { MaxLevel = rules.Max.Id };
    }

    [HttpGet("groups/{id:int}/ai")]
    public async Task<ActionResult<AiGroupDto>> Group(int id, CancellationToken ct) =>
        await db.Groups.AnyAsync(g => g.Id == id, ct) ? await GroupNow(id, await db.GroupBilling.AsNoTracking().FirstOrDefaultAsync(b => b.GroupId == id, ct), ct) : NotFound();

    [HttpPut("groups/{id:int}/ai")]
    public async Task<ActionResult<AiGroupDto>> SetGroup(int id, AiGroupRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!await db.Groups.AnyAsync(g => g.Id == id, ct)) return NotFound();
        if ((req.Level is not null && !AiLevels.Is(req.Level)) || (req.MaxLevel is not null && !AiLevels.Is(req.MaxLevel))) return BadRequest(new { message = "Choose one of the levels." });
        if (req.LimitCents is < 0 or > 1_000_000) return BadRequest(new { message = "Enter a monthly limit from $0 to $10,000." });
        var row = await db.GroupBilling.FirstOrDefaultAsync(b => b.GroupId == id, ct);
        if (row is null) db.GroupBilling.Add(row = new GroupBilling { GroupId = id });
        var before = new { On = row.AiEnabled, GroupDecides = GroupPolicy.Decides(row, GroupChoice.Ai), Level = AiLevels.Of(row.AiLevel).Label, Highest = AiLevels.Max(row.AiMaxLevel).Label, LimitCents = row.AiMonthlyLimitCents };
        if (req.On is bool on) row.AiEnabled = on;
        if (req.MaxLevel is not null) row.AiMaxLevel = req.MaxLevel;
        if (req.GroupDecides is bool decides) row.GroupDecides = decides ? row.GroupDecides | GroupChoice.Ai : row.GroupDecides & ~GroupChoice.Ai;
        if (req.Level is not null) row.AiLevel = req.Level;
        // The level in use is never above the highest one allowed: lowering the highest brings it down with it.
        var highest = AiLevels.Max(row.AiMaxLevel);
        if (!AiLevels.Within(AiLevels.Of(row.AiLevel), highest)) row.AiLevel = highest.Id;
        if (req.UseDefaultLimit) row.AiMonthlyLimitCents = null;
        else if (req.LimitCents is int limit) row.AiMonthlyLimitCents = limit;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, "AiAssistantSetForGroup", "Group", id,
            new { before, after = new { On = row.AiEnabled, GroupDecides = GroupPolicy.Decides(row, GroupChoice.Ai), Level = AiLevels.Of(row.AiLevel).Label, Highest = highest.Label, LimitCents = row.AiMonthlyLimitCents } }, groupId: id);
        return await GroupNow(id, row, ct);
    }

    // ---- How it is being used, and by which groups -----------------------------------------------------------------

    /// <summary>
    /// Every request between two days, filtered by investment group and restaurant: how many, how many were charged,
    /// failed or never became a card, what groups were charged and what it cost the site, by group, restaurant,
    /// level and day, and the requests themselves (the newest 1,000) with what was typed.
    /// </summary>
    [HttpGet("ai/usage")]
    public async Task<AiUsageDto> Usage([FromQuery] string? groupIds, [FromQuery] string? restaurantIds, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        static List<int> Ids(string? s) => (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n > 0).ToList();
        var (gs, rs) = (Ids(groupIds), Ids(restaurantIds));
        var q = db.AiRequests.AsNoTracking().AsQueryable();
        if (gs.Count > 0) q = q.Where(r => gs.Contains(r.GroupId));
        if (rs.Count > 0) q = q.Where(r => rs.Contains(r.RestaurantId));
        if (from is { } f) q = q.Where(r => r.At >= f.ToDateTime(TimeOnly.MinValue));
        if (to is { } t) q = q.Where(r => r.At < t.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var rows = await q.OrderByDescending(r => r.At).ToListAsync(ct);
        var groups = await db.Groups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name, ct);
        var restaurants = await db.Restaurants.AsNoTracking().IgnoreQueryFilters().ToDictionaryAsync(r => r.Id, r => r.Name, ct);

        static bool Charged(AiRequest r) => r.Outcome == AiOutcomes.Ok;
        AiUsageBucket Bucket(string key, string label, IEnumerable<AiRequest> of)
        {
            var list = of.ToList();
            return new AiUsageBucket(key, label, list.Count, list.Count(Charged), list.Count(r => !Charged(r)), list.Count(r => Charged(r) && r.CommentId is null),
                list.Where(Charged).Sum(r => r.PriceMills), list.Sum(r => r.CostMicros));
        }
        var all = Bucket("", "", rows);
        return new AiUsageDto(all.Requests, all.Charged, all.Failed, all.NotSaved, rows.Count(r => r.WantEmail), all.Mills, all.CostMicros,
            rows.GroupBy(r => r.GroupId).Select(g => Bucket(g.Key.ToString(), groups.GetValueOrDefault(g.Key, $"Group {g.Key}"), g)).OrderByDescending(b => b.Requests).ToList(),
            rows.GroupBy(r => r.RestaurantId).Select(g => Bucket(g.Key.ToString(), restaurants.GetValueOrDefault(g.Key, $"Restaurant {g.Key}"), g)).OrderByDescending(b => b.Requests).ToList(),
            AiLevels.All.Select(l => Bucket(l.Id, $"{l.Label} ({l.ModelName})", rows.Where(r => r.Level == l.Id))).ToList(),
            rows.GroupBy(r => DateOnly.FromDateTime(r.At)).OrderBy(g => g.Key).Select(g => Bucket(g.Key.ToString("yyyy-MM-dd"), g.Key.ToString("MMM d", CultureInfo.InvariantCulture), g)).ToList(),
            rows.Take(1000).Select(r => AiAssistantController.Row(r, restaurants.GetValueOrDefault(r.RestaurantId, ""), groups.GetValueOrDefault(r.GroupId), true)).ToList());
    }
}
