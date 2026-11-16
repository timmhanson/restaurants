using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Auth;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Models;
using RestaurantFeedback.Api.Services;

namespace RestaurantFeedback.Api.Controllers;

/// <summary>Per-investment-group settings, persisted as a JSON blob per group in Azure Blob Storage.</summary>
[ApiController]
[Route("api/settings")]
[Authorize]
public class SettingsController(IGroupSettingsStore store, AuditService audit, CurrentUserProvider users, AppDbContext db,
    GroupPostageAccounts postageAccounts, IPostageProvider postage, GroupPostGridAccounts mailAccounts, IPostGridMailer mailer) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<GroupSettings>> Get(CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is null) return new GroupSettings();
        var settings = await store.GetAsync(me.GroupId.Value, ct);
        settings.Postcard ??= new PostcardSettings();
        settings.Postcard.Normalize();
        settings.GroupDisplayName = await GroupName(me.GroupId.Value, ct) ?? settings.GroupDisplayName;
        return settings;
    }

    /// <summary>Settings apply to every restaurant in the group, so only system and investment group administrators change them.</summary>
    [HttpPut]
    [Authorize(Roles = "GroupAdmin,SuperAdmin")]
    public async Task<ActionResult<GroupSettings>> Put(GroupSettings settings, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is null) return BadRequest(new { message = "Select an investment group first." });
        settings.CouponOffers = Clean(settings.CouponOffers);
        settings.ReasonCategories = Clean(settings.ReasonCategories);
        settings.ReplyMethods = Clean(settings.ReplyMethods);
        if (settings.ReplyMethods.Count == 0) settings.ReplyMethods = new GroupSettings().ReplyMethods;
        if (settings.ReasonCategories.Count == 0) return BadRequest(new { message = "At least one reason category is required." });
        settings.CouponValidityMonths = Math.Clamp(settings.CouponValidityMonths, 1, 24);
        settings.Postcard ??= new PostcardSettings();
        settings.Postcard.Normalize();
        settings.FollowUpDays = Math.Clamp(settings.FollowUpDays, 0, 90);
        settings.DuplicateWindowDays = Math.Clamp(settings.DuplicateWindowDays, 0, 365);
        settings.RepeatGuestAlertThreshold = Math.Clamp(settings.RepeatGuestAlertThreshold, 2, 100);
        settings.GroupDisplayName = await GroupName(me.GroupId.Value, ct) ?? settings.GroupDisplayName;
        settings.UpdatedAt = DateTime.UtcNow;
        settings.UpdatedBy = me.Username;
        await store.SaveAsync(me.GroupId.Value, settings, ct);
        await audit.LogAsync(me, "SettingsUpdated", "GroupSettings", me.GroupId, settings);
        return settings;
    }

    // ---- The group's own postage account ----------------------------------------------------------------------

    /// <summary>Whether this group has its own EasyPost account. Never returns the key, only its last four characters.</summary>
    [HttpGet("postage-account")]
    [Authorize(Roles = "GroupAdmin,SuperAdmin")]
    public async Task<ActionResult<PostageAccountDto>> PostageAccount(CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is null) return BadRequest(new { message = "Select an investment group first." });
        return ToDto(await postageAccounts.DescribeAsync(me.GroupId.Value, ct));
    }

    /// <summary>
    /// Saves the group's EasyPost API key so the group pays for its own postage. The key is encrypted before it is
    /// stored and is never shown again or written to any log; replacing it needs the new key typed in.
    /// </summary>
    [HttpPut("postage-account")]
    [Authorize(Roles = "GroupAdmin,SuperAdmin")]
    public async Task<ActionResult<PostageAccountDto>> SetPostageAccount(PostageAccountRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is null) return BadRequest(new { message = "Select an investment group first." });
        if (!postage.AcceptsAccountKeys) return BadRequest(new { message = "This site cannot take postage accounts yet." });
        var key = (req.ApiKey ?? "").Trim();
        if (key.Length is < 20 or > 200 || key.Any(char.IsWhiteSpace))
            return BadRequest(new { message = "That does not look like an EasyPost API key. Copy the whole key from EasyPost (Account Settings, API Keys), with no spaces." });
        var info = await postageAccounts.SetAsync(me.GroupId.Value, key, me.DisplayName, ct);
        await audit.LogAsync(me, "PostageAccountSet", "Group", me.GroupId, new { KeyEnding = info.KeyEnding, TestKey = info.LooksLikeTestKey });
        return ToDto(info);
    }

    [HttpDelete("postage-account")]
    [Authorize(Roles = "GroupAdmin,SuperAdmin")]
    public async Task<ActionResult<PostageAccountDto>> RemovePostageAccount(CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is null) return BadRequest(new { message = "Select an investment group first." });
        var before = await postageAccounts.DescribeAsync(me.GroupId.Value, ct);
        await postageAccounts.RemoveAsync(me.GroupId.Value, ct);
        if (before.HasOwnAccount) await audit.LogAsync(me, "PostageAccountRemoved", "Group", me.GroupId, new { KeyEnding = before.KeyEnding });
        return ToDto(await postageAccounts.DescribeAsync(me.GroupId.Value, ct));
    }

    private PostageAccountDto ToDto(PostageAccountInfo a) =>
        new(a.HasOwnAccount, a.KeyEnding, a.LooksLikeTestKey, a.AddedAt, a.AddedBy, postage.AcceptsAccountKeys, postage.Configured, postage.Description);

    // ---- The group's own PostGrid account (prints and mails cards) -------------------------------------------

    [HttpGet("postgrid-account")]
    [Authorize(Roles = "GroupAdmin,SuperAdmin")]
    public async Task<ActionResult<PostageAccountDto>> PostGridAccount(CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is null) return BadRequest(new { message = "Select an investment group first." });
        return MailDto(await mailAccounts.DescribeAsync(me.GroupId.Value, ct));
    }

    /// <summary>
    /// Checks the key with PostGrid, then saves it encrypted. The key is never shown again or written to any log;
    /// only its last four characters are kept in the clear.
    /// </summary>
    [HttpPut("postgrid-account")]
    [Authorize(Roles = "GroupAdmin,SuperAdmin")]
    public async Task<ActionResult<PostageAccountDto>> SetPostGridAccount(PostageAccountRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is null) return BadRequest(new { message = "Select an investment group first." });
        var key = (req.ApiKey ?? "").Trim();
        if (key.Length is < 12 or > 200 || key.Any(char.IsWhiteSpace))
            return BadRequest(new { message = "That does not look like a PostGrid API key. Copy the whole Print & Mail key from PostGrid (Settings, API Keys), with no spaces." });
        var (ok, message) = await mailer.TestKeyAsync(key, ct);
        if (!ok)
        {
            await audit.LogAsync(me, "PostGridAccountRejected", "Group", me.GroupId, new { KeyEnding = key[^4..], message });
            return BadRequest(new { message });
        }
        var info = await mailAccounts.SetAsync(me.GroupId.Value, key, me.DisplayName, ct);
        await audit.LogAsync(me, "PostGridAccountSet", "Group", me.GroupId, new { info.KeyEnding, TestKey = info.LooksLikeTestKey });
        return MailDto(info);
    }

    [HttpPost("postgrid-account/test")]
    [Authorize(Roles = "GroupAdmin,SuperAdmin")]
    public async Task<ActionResult<ServiceTestResult>> TestPostGridAccount(CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is null) return BadRequest(new { message = "Select an investment group first." });
        var key = await mailAccounts.KeyAsync(me.GroupId.Value, ct);
        if (key is null) return new ServiceTestResult(false, "No PostGrid account has been added for this group.");
        var (ok, message) = await mailer.TestKeyAsync(key, ct);
        await audit.LogAsync(me, ok ? "PostGridAccountTestPassed" : "PostGridAccountTestFailed", "Group", me.GroupId, new { message });
        return new ServiceTestResult(ok, message);
    }

    [HttpDelete("postgrid-account")]
    [Authorize(Roles = "GroupAdmin,SuperAdmin")]
    public async Task<ActionResult<PostageAccountDto>> RemovePostGridAccount(CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is null) return BadRequest(new { message = "Select an investment group first." });
        var before = await mailAccounts.DescribeAsync(me.GroupId.Value, ct);
        await mailAccounts.RemoveAsync(me.GroupId.Value, ct);
        if (before.HasOwnAccount) await audit.LogAsync(me, "PostGridAccountRemoved", "Group", me.GroupId, new { before.KeyEnding });
        return MailDto(await mailAccounts.DescribeAsync(me.GroupId.Value, ct));
    }

    private PostageAccountDto MailDto(PostageAccountInfo a) =>
        new(a.HasOwnAccount, a.KeyEnding, a.LooksLikeTestKey, a.AddedAt, a.AddedBy, true, false, mailer.Description);

    // ---- The picture on the front of cards PostGrid prints --------------------------------------------------

    private const int MaxArtBytes = 5 * 1024 * 1024;

    [HttpGet("card-front")]
    [Authorize(Roles = "GroupAdmin,SuperAdmin")]
    public async Task<ActionResult<CardArtDto>> CardFront(CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is null) return BadRequest(new { message = "Select an investment group first." });
        return await ArtDto(me.GroupId.Value, ct);
    }

    /// <summary>Uploads the front picture: a PNG or JPEG up to 5 MB, ideally 1875 x 1275 pixels (6.25 x 4.25 inches at 300 dpi).</summary>
    [HttpPut("card-front")]
    [Authorize(Roles = "GroupAdmin,SuperAdmin")]
    [RequestSizeLimit(MaxArtBytes + 100_000)]
    public async Task<ActionResult<CardArtDto>> SetCardFront(IFormFile? file, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is null) return BadRequest(new { message = "Select an investment group first." });
        if (file is null || file.Length == 0) return BadRequest(new { message = "Choose a picture to upload." });
        if (file.Length > MaxArtBytes) return BadRequest(new { message = "The picture is larger than 5 MB. Save it as a JPEG, or at a smaller size, and try again." });
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        var info = ImageInfo.Read(bytes);
        if (info is null) return BadRequest(new { message = "Upload a PNG or JPEG picture." });
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16].ToLowerInvariant();
        var art = await db.CardArtworks.FirstOrDefaultAsync(a => a.GroupId == me.GroupId.Value, ct);
        if (art is null) db.CardArtworks.Add(art = new CardArtwork { GroupId = me.GroupId.Value });
        art.ContentType = info.Value.ContentType;
        art.Data = bytes;
        art.Hash = hash;
        art.WidthPx = info.Value.Width > 0 ? info.Value.Width : null;
        art.HeightPx = info.Value.Height > 0 ? info.Value.Height : null;
        art.UploadedAt = DateTime.UtcNow;
        art.UploadedBy = me.DisplayName;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, "CardFrontUploaded", "Group", me.GroupId, new { info.Value.ContentType, art.WidthPx, art.HeightPx, Bytes = bytes.Length, hash });
        return await ArtDto(me.GroupId.Value, ct);
    }

    [HttpDelete("card-front")]
    [Authorize(Roles = "GroupAdmin,SuperAdmin")]
    public async Task<ActionResult<CardArtDto>> RemoveCardFront(CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is null) return BadRequest(new { message = "Select an investment group first." });
        var art = await db.CardArtworks.FirstOrDefaultAsync(a => a.GroupId == me.GroupId.Value, ct);
        if (art is not null)
        {
            db.CardArtworks.Remove(art);
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(me, "CardFrontRemoved", "Group", me.GroupId, new { art.Hash });
        }
        return await ArtDto(me.GroupId.Value, ct);
    }

    private async Task<CardArtDto> ArtDto(int groupId, CancellationToken ct)
    {
        var a = await db.CardArtworks.AsNoTracking().Where(x => x.GroupId == groupId)
            .Select(x => new { x.Hash, x.ContentType, x.WidthPx, x.HeightPx, Bytes = (long)x.Data.Length, x.UploadedAt, x.UploadedBy }).FirstOrDefaultAsync(ct);
        return a is null
            ? new CardArtDto(false, null, null, null, 0, null, null)
            : new CardArtDto(true, $"/card-art/{groupId}/{a.Hash}{(a.ContentType == "image/png" ? ".png" : ".jpg")}", a.WidthPx, a.HeightPx, a.Bytes,
                DateTime.SpecifyKind(a.UploadedAt, DateTimeKind.Utc), a.UploadedBy);
    }

    [HttpGet("storage")]
    [Authorize(Roles = "Owner,GroupAdmin,SuperAdmin")]
    public ActionResult<object> Storage() => new { store = store.Description };


    /// <summary>The group's name lives on the group (renamed by a System Administrator); settings only echo it.</summary>
    private Task<string?> GroupName(int groupId, CancellationToken ct) =>
        db.Groups.Where(g => g.Id == groupId).Select(g => (string?)g.Name).FirstOrDefaultAsync(ct);

    private static List<string> Clean(IEnumerable<string>? items) =>
        (items ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
