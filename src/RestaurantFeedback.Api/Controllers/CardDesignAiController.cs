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
/// "Design it for me" on Card Design: the assistant looks at the card's logo and photo and at pictures the person
/// uploads to model the design after, and offers three designs (a layout, colours and words) for them to preview,
/// choose and adjust. Nothing is applied by this: the person picks one and saves the card as they would any change.
///
/// It is free to the group, since it is part of getting set up, but not unlimited: each group has a number of tries
/// (20 unless the System Administrator sets its own; 0 switches it off). Every try is kept in the request log
/// (AiRequest, kind "design", charged nothing) with what it cost the site; a try that fails is not counted. The
/// pictures uploaded to model after are sent to the assistant and not kept. Whoever may change a card may use it.
/// </summary>
[ApiController]
[Route("api/card-designs/{scope}/suggest")]
[Authorize]
public class CardDesignAiController(AppDbContext db, CurrentUserProvider users, IGroupSettingsStore settingsStore, IAiDrafter drafter, AiOptions options, AuditService audit) : ControllerBase
{
    private const int MaxPictureBytes = 5 * 1024 * 1024;
    private static readonly string[] PictureTypes = ["image/jpeg", "image/png", "image/webp", "image/gif"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The card being designed, when this person may change it: the group's (its administrators), or a restaurant's own (its owners with DesignCards).</summary>
    private async Task<(int GroupId, Restaurant? Restaurant)?> Target(CurrentUser me, string scope, CancellationToken ct)
    {
        if (me.GroupId is not int groupId) return null;
        if (scope == "group") return me.IsGroupWide ? (groupId, null) : null;
        if (!int.TryParse(scope, out var id) || !me.CanManageRestaurant(id) || !me.Has(ManagerPermission.DesignCards)) return null;
        var r = await db.Restaurants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.GroupId == groupId, ct);
        return r is null ? null : (groupId, r);
    }

    private async Task<CardDesignHelp> Tries(int groupId, CancellationToken ct)
    {
        var allowed = (await db.GroupBilling.AsNoTracking().FirstOrDefaultAsync(b => b.GroupId == groupId, ct))?.AiDesignTries ?? options.DefaultDesignTries;
        var used = await db.AiRequests.CountAsync(r => r.GroupId == groupId && r.Kind == AiKinds.Design && r.Outcome == AiOutcomes.Ok, ct);
        return new CardDesignHelp(drafter.Configured && allowed > 0, used, allowed);
    }

    /// <summary>Whether this person can have the card designed for them, and how many free tries the group has left. It never says why it is not offered.</summary>
    [HttpGet]
    public async Task<CardDesignHelp> Get(string scope, CancellationToken ct)
    {
        var me = await users.GetAsync();
        return await Target(me, scope, ct) is { } t ? await Tries(t.GroupId, ct) : new CardDesignHelp(false, 0, 0);
    }

    [HttpPost]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<ActionResult<CardDesignSuggestions>> Post(string scope, [FromForm] string? tone, [FromForm] string? wishes, [FromForm] string? requestKey, [FromForm] List<IFormFile>? pictures, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (await Target(me, scope, ct) is not { } t)
            return StatusCode(403, new { message = "Only the group's administrators can change the group's card, and only a restaurant's owners or the group's administrators can change its own card." });
        var key = (requestKey ?? "").Trim();
        if (key.Length is < 8 or > 64) return BadRequest(new { message = "That request could not be read. Reload the page and try again." });
        tone = string.IsNullOrWhiteSpace(tone) ? null : tone.Trim();
        wishes = string.IsNullOrWhiteSpace(wishes) ? null : wishes.Trim();
        if ((tone?.Length ?? 0) > 100 || (wishes?.Length ?? 0) > 600) return BadRequest(new { message = "Keep what you ask for to a few sentences (600 characters)." });
        pictures ??= [];
        if (pictures.Count > AiDesign.MostPictures) return BadRequest(new { message = $"Upload up to {AiDesign.MostPictures} pictures to model the design after." });
        foreach (var f in pictures)
        {
            if (!PictureTypes.Contains(f.ContentType)) return BadRequest(new { message = "Pictures to model after must be JPEG, PNG, WebP or GIF." });
            if (f.Length is 0 or > MaxPictureBytes) return BadRequest(new { message = "Each picture can be up to 5 MB." });
        }

        // The same press arriving again: the first answer, and no second try used.
        var earlier = await db.AiRequests.AsNoTracking().FirstOrDefaultAsync(r => r.RequestKey == key, ct);
        if (earlier is not null)
        {
            if (earlier.GroupId != t.GroupId || earlier.Outcome != AiOutcomes.Ok || earlier.ResultJson is null) return BadRequest(new { message = "That try did not finish. Press the button again." });
            var again = await Tries(t.GroupId, ct);
            return new CardDesignSuggestions(JsonSerializer.Deserialize<List<CardDesignSuggestion>>(earlier.ResultJson, Json) ?? [], again.Used, again.Allowed);
        }

        var tries = await Tries(t.GroupId, ct);
        if (!tries.Available) return StatusCode(403, new { message = "Having the card designed for you is not available just now." });
        if (tries.Used >= tries.Allowed)
            return BadRequest(new { message = $"Your investment group has used its {tries.Allowed} free designs. You can still design the card yourself below, or ask the site's administrator for more." });

        // The card's own logo and photo: the restaurant's, or the group's where the restaurant has none of its own.
        var designs = await db.CardDesigns.AsNoTracking().Where(d => d.GroupId == t.GroupId && (d.RestaurantId == null || d.RestaurantId == (t.Restaurant == null ? null : t.Restaurant.Id))).ToListAsync(ct);
        var mine = designs.FirstOrDefault(d => d.RestaurantId == t.Restaurant?.Id);
        var ids = designs.Select(d => d.Id).ToList();
        var art = await db.CardImages.AsNoTracking().Where(i => ids.Contains(i.DesignId) && (i.Kind == CardImageKinds.Logo || i.Kind == CardImageKinds.Photo)).ToListAsync(ct);
        CardImage? Own(string kind) => art.Where(i => i.Kind == kind && i.Data.Length is > 0 and <= MaxPictureBytes && PictureTypes.Contains(i.ContentType)).OrderByDescending(i => i.DesignId == mine?.Id).FirstOrDefault();
        var (logo, photo) = (Own(CardImageKinds.Logo), Own(CardImageKinds.Photo));
        var shown = new List<AiPicture>();
        if (logo is not null) shown.Add(new("The restaurant's logo, which the card prints", logo.ContentType, logo.Data));
        if (photo is not null) shown.Add(new("The restaurant's photo, which the photo and split layouts print", photo.ContentType, photo.Data));
        foreach (var (f, n) in pictures.Select((f, n) => (f, n)))
        {
            using var stream = new MemoryStream();
            await f.CopyToAsync(stream, ct);
            shown.Add(new($"Picture {n + 1} to model the design after", f.ContentType, stream.ToArray()));
        }

        var settings = await settingsStore.GetAsync(t.GroupId, ct);
        var restaurant = t.Restaurant?.Name ?? (string.IsNullOrWhiteSpace(settings.GroupDisplayName) ? (await db.Groups.AsNoTracking().FirstAsync(g => g.Id == t.GroupId, ct)).Name : settings.GroupDisplayName);
        var entry = new AiRequest
        {
            Kind = AiKinds.Design, RequestKey = key, GroupId = t.GroupId, RestaurantId = t.Restaurant?.Id ?? 0, UserId = me.Id, UserName = me.DisplayName, Level = AiKinds.Design, Model = AiDesign.Model,
            Effort = AiDesign.Effort, Input = $"How it should feel: {tone ?? "not said"}\nAsked for: {wishes ?? "nothing more"}\nPictures to model after: {pictures.Count}; logo: {(logo is null ? "no" : "yes")}; photo: {(photo is null ? "no" : "yes")}"
        };
        AiDesignAnswer answer;
        try
        {
            answer = await drafter.DesignAsync(new AiDesignAsk(restaurant, tone, wishes, logo is not null, photo is not null, shown), ct);
        }
        catch (AiException ex)
        {
            (entry.Outcome, entry.Error) = (AiOutcomes.Failed, ex.ForAdmin);
            db.AiRequests.Add(entry);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(me, "CardDesignHelpFailed", "AiRequest", entry.Id, new { Scope = t.Restaurant?.Name ?? "Group", Counted = false }, t.Restaurant?.Id);
            return StatusCode(502, new { message = ex.Message });
        }

        // Each design is made safe to print, and carries the rest of the card as it is now, so choosing one changes
        // the front, the colours and the note on the back and nothing else (the back's own layout, small print and web address stay).
        var current = mine ?? designs.FirstOrDefault(d => d.RestaurantId == null);
        var list = answer.Options.Take(AiDesign.Options).Select(o => AiDesign.Safe(o, photo is not null, PostGridCardDesign.SafeColor(settings.AccentColor))).Select(o => new CardDesignSuggestion(o.Name, o.Why,
            new CardDesignFields(CardFronts.Designed, o.Layout, o.Headline, o.Message, o.BackMessage, o.BackgroundColor, o.TextColor,
                current?.BackStyle ?? CardBacks.Classic, o.InkColor, current?.Website, current?.LegalText, CardElements.Parse(current?.BackLayoutJson), null))).ToList();

        entry.Outcome = AiOutcomes.Ok;
        (entry.InputTokens, entry.OutputTokens) = (answer.InputTokens, answer.OutputTokens);
        entry.CostMicros = (int)Math.Round(answer.InputTokens * AiDesign.InMicrosPerToken + answer.OutputTokens * AiDesign.OutMicrosPerToken);
        entry.ResultJson = JsonSerializer.Serialize(list, Json);
        db.AiRequests.Add(entry);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, "CardDesignHelpUsed", "AiRequest", entry.Id, new { Scope = t.Restaurant?.Name ?? "Group", Designs = list.Select(s => s.Name), PicturesToModelAfter = pictures.Count, Charged = "nothing" }, t.Restaurant?.Id);
        return new CardDesignSuggestions(list, tries.Used + 1, tries.Allowed);
    }
}
