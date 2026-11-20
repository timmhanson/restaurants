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
    /// <summary>How many pictures of its own a laid-out card can place, besides the logo, photo and the other named ones.</summary>
    private const int MaxPlacedPictures = 12;
    /// <summary>A stand-in code, to show where each card's own QR code is printed.</summary>
    private static readonly string SampleQr = CardCodes.QrDataUri("https://example.com/v/sample-card-code-shown-in-the-preview");
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
        return r is null ? (null, NotFound()) : (new Target(groupId, r, me.CanManageRestaurant(id) && me.Has(ManagerPermission.DesignCards)), null);
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
        list.AddRange(restaurants.Select(r => new CardDesignScope(r.Id.ToString(), r.Id, r.Name, me.CanManageRestaurant(r.Id) && me.Has(ManagerPermission.DesignCards), own.Contains(r.Id))));
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
        await DropUnusedPictures(t, ct);
        await audit.LogAsync(me, "CardDesignSaved", "CardDesign", d.Id, new
        {
            Scope = t.Restaurant?.Name ?? "Group", d.Front, d.Layout, d.Headline, d.Message, d.BackMessage, d.BackgroundColor, d.TextColor, d.BackStyle, d.InkColor, d.Website, d.LegalText,
            BackItems = req.BackLayout?.Count, FrontItems = req.FrontLayout?.Count
        }, t.RestaurantId);
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
    /// "photo" for a designed front, "backlogo" (the logo on a classic back) or "footer" (a small picture for the
    /// back's bottom corner, such as social media icons), or a picture placed in the card designer (el- and a few
    /// letters and digits). PNG or JPEG up to 5 MB.
    /// </summary>
    [HttpPut("{scope}/images/{kind}")]
    [RequestSizeLimit(MaxPictureBytes + 100_000)]
    public async Task<ActionResult<CardDesignDto>> SetImage(string scope, string kind, IFormFile? file, CancellationToken ct)
    {
        var me = await users.GetAsync();
        var (t, problem) = await Resolve(me, scope, ct);
        if (t is null) return problem!;
        if (!t.CanEdit) return Refuse();
        if (!CardElements.IsImageKind(kind)) return NotFound();
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
        if (image is null && !CardImageKinds.All.Contains(kind) && await db.CardImages.CountAsync(i => i.DesignId == d.Id && i.Kind.StartsWith("el-"), ct) >= MaxPlacedPictures)
            return BadRequest(new { message = $"A card can place up to {MaxPlacedPictures} pictures of its own. Remove one from the card, save, and try again." });
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
        var check = Validate(req);
        if (check is not null) return BadRequest(new { message = check });
        var draft = new CardDesign { GroupId = t.GroupId, RestaurantId = t.RestaurantId };
        Fill(draft, req);
        var groupRow = await db.GroupBilling.AsNoTracking().FirstOrDefaultAsync(b => b.GroupId == t.GroupId, ct);
        var look = designs.Look(new CardDesigns.Loaded(draft, pictures), t.GroupId, settings.AccentColor) with { TeamSignature = GroupPolicy.SignatureForAll(groupRow) };
        var restaurant = await SampleRestaurant(t, ct);
        var sample = SampleCard(settings, restaurant, GroupPolicy.Signature(groupRow, restaurant));
        return new MailPreview(PostGridCardDesign.Front(look, sample, restaurant, settings.CouponValidityMonths),
            PostGridCardDesign.Back(sample, restaurant, settings.CouponValidityMonths, settings.AccentColor, look,
                SampleQr));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private ObjectResult Refuse() =>
        StatusCode(403, new { message = "Only the group's administrators can change the group's card, and only a restaurant's owners or the group's administrators can change its own card." });

    /// <summary>The restaurant a sample card is drawn for: the one being designed, or the group's first.</summary>
    private async Task<Restaurant> SampleRestaurant(Target t, CancellationToken ct) =>
        t.Restaurant ?? await db.Restaurants.AsNoTracking().Where(r => r.GroupId == t.GroupId && r.IsActive).OrderBy(r => r.Name).FirstOrDefaultAsync(ct)
        ?? new Restaurant { Name = "Your restaurant" };

    private static GuestComment SampleCard(GroupSettings settings, Restaurant? restaurant = null, string? teamSignature = null)
    {
        teamSignature ??= restaurant?.SignatureName;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return new GuestComment
        {
            GuestFirstName = "Pat", GuestLastName = "Sample", CouponOffer = settings.CouponOffers.FirstOrDefault() ?? "Free Value Basket", DateIssued = today,
            ExpiresOn = today.AddMonths(settings.CouponValidityMonths),
            // The sample is signed as a card entered with the team login would be, when the restaurant has a name set for that.
            AuthorizedBy = string.IsNullOrWhiteSpace(teamSignature) ? "A. Manager" : teamSignature.Trim()
        };
    }

    /// <summary>
    /// Pictures placed in the card designer are removed once no card uses them. The group's are kept while any
    /// restaurant's own card still places them, since a restaurant borrows the group's pictures.
    /// </summary>
    private async Task DropUnusedPictures(Target t, CancellationToken ct)
    {
        var all = await db.CardDesigns.AsNoTracking().Where(x => x.GroupId == t.GroupId).Select(x => new { x.Id, x.RestaurantId, x.BackLayoutJson, x.FrontLayoutJson }).ToListAsync(ct);
        var mine = all.FirstOrDefault(x => x.RestaurantId == t.RestaurantId);
        if (mine is null) return;
        var used = all.Where(x => x.Id == mine.Id || t.Restaurant is null)
            .SelectMany(x => new[] { x.BackLayoutJson, x.FrontLayoutJson }).SelectMany(j => CardElements.Parse(j) ?? [])
            .Where(e => e.Type == CardElements.Image && e.Image is not null).Select(e => e.Image!).ToHashSet();
        var unused = (await db.CardImages.Where(i => i.DesignId == mine.Id && i.Kind.StartsWith("el-")).ToListAsync(ct)).Where(i => !used.Contains(i.Kind)).ToList();
        if (unused.Count == 0) return;
        db.CardImages.RemoveRange(unused);
        await db.SaveChangesAsync(ct);
    }

    private static string? Validate(CardDesignFields f)
    {
        if (f.Front is not null && !CardFronts.All.Contains(f.Front)) return "Choose how the front is made.";
        if (f.Layout is not null && !CardLayouts.All.Contains(f.Layout)) return "Choose a layout.";
        if ((f.Headline?.Trim().Length ?? 0) > 60) return "Keep the headline to 60 characters so it fits the card.";
        if ((f.Message?.Trim().Length ?? 0) > 220) return "Keep the message on the front to 220 characters so it fits the card.";
        if ((f.BackMessage?.Trim().Length ?? 0) > 160) return "Keep the note on the back to 160 characters so it fits beside the address.";
        if (f.BackStyle is not null && !CardBacks.All.Contains(f.BackStyle)) return "Choose how the back is laid out.";
        if ((f.Website?.Trim().Length ?? 0) > 60) return "Keep the web address to 60 characters.";
        if ((f.LegalText?.Trim().Length ?? 0) > 420) return "Keep the small print to 420 characters so it fits the card.";
        foreach (var c in new[] { f.BackgroundColor, f.TextColor, f.InkColor })
            if (!string.IsNullOrWhiteSpace(c) && PostGridCardDesign.SafeColor(c, "") != c) return "Colours are written like #005daa.";
        // Layouts from the card designer are checked and tidied in place, so what is saved is what was checked.
        foreach (var layout in new[] { f.BackLayout, f.FrontLayout })
            if (layout is not null && CardElements.Clean(layout) is { } wrong) return wrong;
        if (f.BackStyle == CardBacks.Custom && f.BackLayout is not { Count: > 0 }) return "Put at least one item on the back, or choose one of the ready-made backs.";
        if (f.Front == CardFronts.Custom && f.FrontLayout is not { Count: > 0 }) return "Put at least one item on the front, or choose one of the ready-made fronts.";
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
        d.BackStyle = f.BackStyle ?? CardBacks.Classic;
        d.InkColor = Clean(f.InkColor);
        d.Website = Clean(f.Website);
        // The standard wording is kept as "none", so a later change to it reaches every card that never changed it.
        d.LegalText = Clean(f.LegalText) == CardBacks.DefaultLegal ? null : Clean(f.LegalText);
        // A layout is kept even while a ready-made side is chosen, so switching back does not lose the work.
        d.BackLayoutJson = f.BackLayout is { Count: > 0 } ? CardElements.Serialize(f.BackLayout) : null;
        d.FrontLayoutJson = f.FrontLayout is { Count: > 0 } ? CardElements.Serialize(f.FrontLayout) : null;
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
            images, own is null ? null : DateTime.SpecifyKind(own.Design.UpdatedAt, DateTimeKind.Utc), own?.Design.UpdatedBy,
            d is not null && CardBacks.All.Contains(d.BackStyle) ? d.BackStyle : CardBacks.Classic, PostGridCardDesign.SafeColor(d?.InkColor, CardBacks.DefaultInk),
            d?.Website, d?.LegalText, CardBacks.DefaultLegal,
            CardElements.Parse(d?.BackLayoutJson), CardElements.Parse(d?.FrontLayoutJson), CardElements.ClassicBack(), CardElements.StarterFront(), await MergeFields(t, settings, d, ct), SampleQr);
    }

    /// <summary>The fields a card's words can carry, each with what it prints on the sample card, so the designer shows real words.</summary>
    private async Task<List<CardMergeField>> MergeFields(Target t, GroupSettings settings, CardDesign? d, CancellationToken ct)
    {
        var look = CardLook.Plain(settings.AccentColor) with { Website = d?.Website, LegalText = d?.LegalText, BackMessage = d?.BackMessage };
        var sampleRestaurant = await SampleRestaurant(t, ct);
        var groupRow = await db.GroupBilling.AsNoTracking().FirstOrDefaultAsync(b => b.GroupId == t.GroupId, ct);
        var merge = CardMerge.For(SampleCard(settings, sampleRestaurant, GroupPolicy.Signature(groupRow, sampleRestaurant)), sampleRestaurant, settings.CouponValidityMonths, look);
        return CardMerge.Fields.Select(f => new CardMergeField(f.Key, f.Label, merge.Values.GetValueOrDefault(f.Key, ""))).ToList();
    }
}
