using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RestaurantFeedback.Api.Auth;
using RestaurantFeedback.Api.Models;
using RestaurantFeedback.Api.Services;

namespace RestaurantFeedback.Api.Controllers;

/// <summary>Per-investment-group settings, persisted as a JSON blob per group in Azure Blob Storage.</summary>
[ApiController]
[Route("api/settings")]
[Authorize]
public class SettingsController(IGroupSettingsStore store, AuditService audit, CurrentUserProvider users) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<GroupSettings>> Get(CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is null) return new GroupSettings();
        return await store.GetAsync(me.GroupId.Value, ct);
    }

    /// <summary>Settings apply to every restaurant in the group, so only system and group administrators change them.</summary>
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
        settings.DuplicateWindowDays = Math.Clamp(settings.DuplicateWindowDays, 0, 365);
        settings.RepeatGuestAlertThreshold = Math.Clamp(settings.RepeatGuestAlertThreshold, 2, 100);
        settings.UpdatedAt = DateTime.UtcNow;
        settings.UpdatedBy = me.Username;
        await store.SaveAsync(me.GroupId.Value, settings, ct);
        await audit.LogAsync(me, "SettingsUpdated", "GroupSettings", me.GroupId, settings);
        return settings;
    }

    [HttpGet("storage")]
    [Authorize(Roles = "Owner,GroupAdmin,SuperAdmin")]
    public ActionResult<object> Storage() => new { store = store.Description };


    private static List<string> Clean(IEnumerable<string>? items) =>
        (items ?? []).Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
