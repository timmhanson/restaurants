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
    GroupPostageAccounts postageAccounts, IPostageProvider postage) : ControllerBase
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

    [HttpGet("storage")]
    [Authorize(Roles = "Owner,GroupAdmin,SuperAdmin")]
    public ActionResult<object> Storage() => new { store = store.Description };


    /// <summary>The group's name lives on the group (renamed by a System Administrator); settings only echo it.</summary>
    private Task<string?> GroupName(int groupId, CancellationToken ct) =>
        db.Groups.Where(g => g.Id == groupId).Select(g => (string?)g.Name).FirstOrDefaultAsync(ct);

    private static List<string> Clean(IEnumerable<string>? items) =>
        (items ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
