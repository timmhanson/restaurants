using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Auth;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Models;
using RestaurantFeedback.Api.Services;

namespace RestaurantFeedback.Api.Controllers;

/// <summary>Per-investment-group settings, persisted as a JSON blob per group in Azure Blob Storage.</summary>
[ApiController]
[Route("api/settings")]
[Authorize]
public class SettingsController(IGroupSettingsStore store, AuditService audit, CurrentUserProvider users, AppDbContext db) : ControllerBase
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

    [HttpGet("storage")]
    [Authorize(Roles = "Owner,GroupAdmin,SuperAdmin")]
    public ActionResult<object> Storage() => new { store = store.Description };


    /// <summary>The group's name lives on the group (renamed by a System Administrator); settings only echo it.</summary>
    private Task<string?> GroupName(int groupId, CancellationToken ct) =>
        db.Groups.Where(g => g.Id == groupId).Select(g => (string?)g.Name).FirstOrDefaultAsync(ct);

    private static List<string> Clean(IEnumerable<string>? items) =>
        (items ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
