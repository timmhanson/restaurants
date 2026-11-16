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
/// How the cards the site prints and mails look. The investment group has one design, used by every restaurant; a
/// restaurant can have its own instead. Group administrators change any of them; an owner changes their own
/// restaurants'. The scope in each address is "group" or a restaurant's id.
/// </summary>
[ApiController]
[Route("api/card-designs")]
[Authorize(Roles = "Owner,GroupAdmin,SuperAdmin")]
public class CardDesignController(AppDbContext db, AuditService audit, CurrentUserProvider users, IGroupSettingsStore settingsStore, CardDesigns designs) : ControllerBase
{
    private const int MaxPictureBytes = 5 * 1024 * 1024;
    private const string GroupScope = "group";

    private record Target(int GroupId, Restaurant? Restaurant, bool CanEdit)
    {
        public int? RestaurantId => Restaurant?.Id;
        public string Scope => Restaurant is null ? GroupScope : Restaurant.Id.ToString();
    }

    private async Task<(Target? Target, ActionResult? Problem)> Resolve(CurrentUser me, string scope, CancellationToken ct)
    {
        if (me.GroupId is not int groupId) return (null, BadRequest(new { message = "Select an investment group first." }));
        if (scope == GroupScope) return (new Target(groupId, null, me.IsGroupWide), null);
        if (!int.TryParse(scope, out var id) || !me.CanAccessRestaurant(id)) return (null, NotFound());
        var r = await db.Restaurants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.GroupId == groupId, ct);
        return r is null ? (null, NotFound()) : (new Target(groupId, r, me.CanManageRestaurant(id)), null);
    }

    /// <summary>The designs this person can see: the group's, then each restaurant they manage.</summary>
    [HttpGet]
    public async Task<ActionResult<List<CardDesignScope>>> List(CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is not int groupId) return new List<CardDesignScope>();
        var own = await db.CardDesigns.AsNoTracking().Where(d => d.GroupId == groupId).Select(d => d.RestaurantId).ToListAsync(ct);
        var mine = me.RestaurantIds.ToList();
        var restaurants = await db.Restaurants.AsNoTracking().Where(r => r.GroupId == groupId && r.IsActive && mine.Contains(r.Id)).OrderBy(r => r.Name)
            .Select(r => new { r.Id, r.Name }).ToListAsync(ct);
        var list = new List<CardDesignScope> { new(GroupScope, null, "Every restaurant (the group's card)", me.IsGroupWide, own.Contains(null)) };
        list.AddRange(restaurants.Select(r => new CardDesignScope(r.Id.ToString(), r.Id, r.Name, me.CanManageRestaurant(r.Id), own.Contains(r.Id))));
        return list;
    }

    [HttpGet("{scope}")]
    public async Task<ActionResult<CardDesignDto>> Get(string scope, CancellationToken ct)
    {
        var me = await users.GetAsync();
        var (t, problem) = await Resolve(me, scope, ct);
        return t is null ? problem! : await Dto(t, ct);
    }

    /// <summary>Saves the words, layout and colours. Pictures are uploaded on their own.</summary>
    [HttpPut("{scope}")]
    public async Task<ActionResult<CardDesignDto>> Put(string scope, CardDesignFields req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        var (t, problem) = await Resolve(me, scope, ct);
        if (t is null) return problem!;
        if (!t.CanEdit) return Refuse();
        var check = Validate(req);
        if (check is not null) return BadRequest(new { message = check });
        var d = await Own(t, ct, create: true, me);
        Fill(d!, req);
        d!.UpdatedAt = DateTime.UtcNow;
        d.UpdatedBy = me.DisplayName;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, "CardDesignSaved", "CardDesign", d.Id, new { Scope = t.Restaurant?.Name ?? "Group", d.Front, d.Layout, d.Headline, d.Message, d.BackMessage, d.BackgroundColor, d.TextColor }, t.RestaurantId);
        return await Dto(t, ct);
    }

    /// <summary>Removes a restaurant's own design (it goes back to the group's), or puts the group's back to the plain card.</summary>
    [HttpDelete("{scope}")]
    public async Task<ActionResult<CardDesignDto>> Delete(string scope, CancellationToken ct)
    {
        var me = await users.GetAsync();
        var (t, problem) = await Resolve(me, scope, ct);
        if (t is null) return problem!;
        if (!t.CanEdit) return Refuse();
        var d = await Own(t, ct, create: false, me);
        if (d is not null)
        {
            db.CardDesigns.Remove(d);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(me, "CardDesignRemoved", "CardDesign", d.Id, new { Scope = t.Restaurant?.Name ?? "Group" }, t.RestaurantId);
        }
        return await Dto(t, ct);
    }

    /// <summary>
    /// Uploads one picture: "picture" (finished artwork for the whole front, ideally 1875 x 1275 pixels), "logo" or
    /// "photo". PNG or JPEG up to 5 MB.
    /// </summary>
    [HttpPut("{scope}/images/{kind}")]
    [RequestSizeLimit(MaxPictureBytes + 100_000)]
    public async Task<ActionResult<CardDesignDto>> SetImage(string scope, string kind, IFormFile? file, CancellationToken ct)
    {
        var me = await users.GetAsync();
        var (t, problem) = await Resolve(me, scope, ct);
        if (t is null) return problem!;
        if (!t.CanEdit) return Refuse();
        if (!CardImageKinds.All.Contains(kind)) return NotFound();
        if (file is null || file.Length == 0) return BadRequest(new { message = "Choose a picture to upload." });
        if (file.Length > MaxPictureBytes) return BadRequest(new { message = "The picture is larger than 5 MB. Save it as a JPEG, or at a smaller size, and try again." });
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        var info = ImageInfo.Read(bytes);
        if (info is null) return BadRequest(new { message = "Upload a PNG or JPEG picture." });
        var d = (await Own(t, ct, create: true, me))!;
        if (d.Id == 0) await db.SaveChangesAsync(ct);
        var image = await db.CardImages.FirstOrDefaultAsync(i => i.DesignId == d.Id && i.Kind == kind, ct);
        if (image is null) db.CardImages.Add(image = new CardImage { DesignId = d.Id, GroupId = t.GroupId, Kind = kind });
        image.ContentType = info.Value.ContentType;
        image.Data = bytes;
        image.Hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16].ToLowerInvariant();
        image.WidthPx = info.Value.Width > 0 ? info.Value.Width : null;
        image.HeightPx = info.Value.Height > 0 ? info.Value.Height : null;
        image.UploadedAt = DateTime.UtcNow;
        image.UploadedBy = me.DisplayName;
        d.UpdatedAt = DateTime.UtcNow;
        d.UpdatedBy = me.DisplayName;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, "CardPictureUploaded", "CardDesign", d.Id,
            new { Scope = t.Restaurant?.Name ?? "Group", kind, image.ContentType, image.WidthPx, image.HeightPx, Bytes = bytes.Length, image.Hash }, t.RestaurantId);
        return await Dto(t, ct);
    }

    [HttpDelete("{scope}/images/{kind}")]
    public async Task<ActionResult<CardDesignDto>> RemoveImage(string scope, string kind, CancellationToken ct)
    {
        var me = await users.GetAsync();
        var (t, problem) = await Resolve(me, scope, ct);
        if (t is null) return problem!;
        if (!t.CanEdit) return Refuse();
        var d = await Own(t, ct, create: false, me);
        var image = d is null ? null : await db.CardImages.FirstOrDefaultAsync(i => i.DesignId == d.Id && i.Kind == kind, ct);
        if (image is not null)
        {
            db.CardImages.Remove(image);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(me, "CardPictureRemoved", "CardDesign", d!.Id, new { Scope = t.Restaurant?.Name ?? "Group", kind, image.Hash }, t.RestaurantId);
        }
        return await Dto(t, ct);
    }

    /// <summary>
    /// Both sides of a sample card as the mail house would print them, from words and colours that have not been
    /// saved yet (with the pictures already uploaded), so the designer shows exactly what will be sent.
    /// </summary>
    [HttpPost("{scope}/preview")]
    public async Task<ActionResult<MailPreview>> Preview(string scope, CardDesignFields req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        var (t, problem) = await Resolve(me, scope, ct);
        if (t is null) return problem!;
        var settings = await settingsStore.GetAsync(t.GroupId, ct);
        var all = await designs.ForGroupAsync(t.GroupId, ct);
        // The pictures of the design being edited; a restaurant borrows the ones it has not uploaded from the group's card.
        var own = all.FirstOrDefault(l => l.Design.RestaurantId == t.RestaurantId);
        var group = all.FirstOrDefault(l => l.Design.RestaurantId == null);
        var pictures = t.Restaurant is null ? own?.Images ?? [] : CardDesigns.WithGroupPictures(own?.Images ?? [], group);
        var draft = new CardDesign { GroupId = t.GroupId, RestaurantId = t.RestaurantId };
        Fill(draft, req);
        var look = designs.Look(new CardDesigns.Loaded(draft, pictures), t.GroupId, settings.AccentColor);
        var restaurant = t.Restaurant ?? await db.Restaurants.AsNoTracking().Where(r => r.GroupId == t.GroupId && r.IsActive).OrderBy(r => r.Name).FirstOrDefaultAsync(ct)
                         ?? new Restaurant { Name = "Your restaurant" };
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var sample = new GuestComment
        {
            CouponOffer = settings.CouponOffers.FirstOrDefault() ?? "Free Value Basket", DateIssued = today,
            ExpiresOn = today.AddMonths(settings.CouponValidityMonths), AuthorizedBy = "A. Manager"
        };
        return new MailPreview(PostGridCardDesign.Front(look, restaurant.Name, sample.CouponOffer),
            PostGridCardDesign.Back(sample, restaurant, settings.CouponValidityMonths, settings.AccentColor, look.BackMessage));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private ObjectResult Refuse() =>
        StatusCode(403, new { message = "Only the group's administrators can change the group's card, and only a restaurant's owners or the group's administrators can change its own card." });

    private static string? Validate(CardDesignFields f)
    {
        if (f.Front is not null && !CardFronts.All.Contains(f.Front)) return "Choose how the front is made.";
        if (f.Layout is not null && !CardLayouts.All.Contains(f.Layout)) return "Choose a layout.";
        if ((f.Headline?.Trim().Length ?? 0) > 60) return "Keep the headline to 60 characters so it fits the card.";
        if ((f.Message?.Trim().Length ?? 0) > 220) return "Keep the message on the front to 220 characters so it fits the card.";
        if ((f.BackMessage?.Trim().Length ?? 0) > 160) return "Keep the note on the back to 160 characters so it fits beside the address.";
        foreach (var c in new[] { f.BackgroundColor, f.TextColor })
            if (!string.IsNullOrWhiteSpace(c) && PostGridCardDesign.SafeColor(c, "") != c) return "Colours are written like #005daa.";
        return null;
    }

    private static void Fill(CardDesign d, CardDesignFields f)
    {
        string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        d.Front = f.Front ?? CardFronts.Plain;
        d.Layout = f.Layout ?? CardLayouts.Centered;
        d.Headline = Clean(f.Headline);
        d.Message = Clean(f.Message);
        d.BackMessage = Clean(f.BackMessage);
        d.BackgroundColor = Clean(f.BackgroundColor);
        d.TextColor = Clean(f.TextColor);
    }

    private async Task<CardDesign?> Own(Target t, CancellationToken ct, bool create, CurrentUser me)
    {
        var d = await db.CardDesigns.FirstOrDefaultAsync(x => x.GroupId == t.GroupId && x.RestaurantId == t.RestaurantId, ct);
        if (d is null && create) db.CardDesigns.Add(d = new CardDesign { GroupId = t.GroupId, RestaurantId = t.RestaurantId, UpdatedBy = me.DisplayName });
        return d;
    }

    private async Task<CardDesignDto> Dto(Target t, CancellationToken ct)
    {
        var settings = await settingsStore.GetAsync(t.GroupId, ct);
        var all = await designs.ForGroupAsync(t.GroupId, ct);
        var own = all.FirstOrDefault(l => l.Design.RestaurantId == t.RestaurantId);
        var group = all.FirstOrDefault(l => l.Design.RestaurantId == null);
        // A restaurant without its own design shows what it inherits from the group.
        var d = (own ?? (t.Restaurant is null ? null : group))?.Design;
        var name = t.Restaurant?.Name ?? "Every restaurant (the group's card)";
        var mine = own?.Images ?? [];
        var pictures = t.Restaurant is null ? mine : CardDesigns.WithGroupPictures(mine, group);
        var images = pictures.Select(i => new CardImageDto(i.Kind, designs.ImageUrl(t.GroupId, i.Hash, i.ContentType, absolute: false), i.WidthPx, i.HeightPx, i.Bytes,
            DateTime.SpecifyKind(i.UploadedAt, DateTimeKind.Utc), i.UploadedBy, !mine.Contains(i))).ToList();
        return new CardDesignDto(t.Scope, t.RestaurantId, name, t.CanEdit, own is not null, d?.Front ?? CardFronts.Plain, d?.Layout ?? CardLayouts.Centered, d?.Headline, d?.Message,
            d?.BackMessage, PostGridCardDesign.SafeColor(d?.BackgroundColor, PostGridCardDesign.SafeColor(settings.AccentColor)), PostGridCardDesign.SafeColor(d?.TextColor, "#ffffff"),
            images, own is null ? null : DateTime.SpecifyKind(own.Design.UpdatedAt, DateTimeKind.Utc), own?.Design.UpdatedBy);
    }
}
