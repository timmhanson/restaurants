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
/// Everything after a card is entered and before the guest is back in the restaurant: printing the coupon card onto
/// a postcard (with bought postage or a stamp by hand), which marks it sent; following up some days later to check
/// it arrived, with notes about the conversation; and recording that the coupon was redeemed.
/// </summary>
[ApiController]
[Route("api")]
[Authorize]
public class CardMailingController(AppDbContext db, AuditService audit, IGroupSettingsStore settingsStore, CurrentUserProvider users,
    CommentSummaryBuilder summaries, CardFollowUps followUps, IPostageProvider postage, PostageOptions postageOptions) : ControllerBase
{
    // ---- Printing and postage -------------------------------------------------------------------------------

    [HttpGet("comments/print-config")]
    public async Task<ActionResult<PrintConfig>> Config(CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.MarkResponded)) return Forbid();
        var settings = await settingsStore.GetAsync(me.GroupId ?? 0, ct);
        var available = postage.Configured && (settings.Postcard?.AllowBoughtPostage ?? true);
        return new PrintConfig(available, postage.Description, available && me.Has(ManagerPermission.BuyPostage), postageOptions.MaxCentsPerCard, MailedMethod(settings));
    }

    /// <summary>
    /// Buys USPS postage for each card that does not have it yet. Money is spent, so it needs the BuyPostage
    /// permission and never buys twice for one card. Each card succeeds or fails on its own.
    /// </summary>
    [HttpPost("comments/postage")]
    public async Task<ActionResult<List<PostageOutcome>>> BuyPostage(CardIdsRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.BuyPostage) || !me.Has(ManagerPermission.MarkResponded)) return Forbid();
        var settings = await settingsStore.GetAsync(me.GroupId ?? 0, ct);
        if (!postage.Configured) return BadRequest(new { message = "Buying postage is not set up on this site. Print the cards and add stamps by hand." });
        if (!(settings.Postcard?.AllowBoughtPostage ?? true)) return BadRequest(new { message = "Buying postage is turned off for this investment group. Print the cards and add stamps by hand." });
        var cards = await Cards(me, req.CommentIds, ct);
        if (cards.Count == 0) return NotFound();
        var card = settings.Postcard ?? new PostcardSettings();

        var results = new List<PostageOutcome>();
        foreach (var c in cards)
        {
            if (c.PostageId is not null)
            {
                results.Add(new(c.Id, true, c.PostageCents, c.PostageLabelUrl, c.PostageTest, true, null));
                continue;
            }
            var problem = MissingAddress(c);
            if (problem is not null)
            {
                results.Add(new(c.Id, false, null, null, false, false, problem));
                continue;
            }
            try
            {
                var r = c.Restaurant;
                var bought = await postage.BuyPostcardAsync(
                    new PostageAddress(r.Name, r.StreetAddress!, r.City!, r.State!, r.Zip!, r.Phone),
                    new PostageAddress($"{c.GuestFirstName} {c.GuestLastName}".Trim(), c.StreetAddress!, c.City!, c.State!, c.Zip!),
                    card.WidthIn, card.HeightIn, ct);
                c.PostageProvider = bought.Provider;
                c.PostageId = bought.Id;
                c.PostageCents = bought.Cents;
                c.PostageLabelUrl = bought.LabelUrl;
                c.PostageTrackingCode = bought.TrackingCode;
                c.PostageTest = bought.Test;
                c.PostagePurchasedAt = DateTime.UtcNow;
                c.PostageByName = me.DisplayName;
                var entry = Activity(c, me, ActivityKind.PostageBought, text: $"{(bought.Test ? "Test postage (not valid for mailing)" : "USPS postage")} bought for ${bought.Cents / 100m:0.00}");
                c.LastActivityAt = entry.CreatedAt;
                // Saved card by card: once money is spent the record must exist even if a later card fails.
                await db.SaveChangesAsync(ct);
                await audit.LogAsync(me, "PostagePurchased", "Comment", c.Id, new { bought.Provider, bought.Id, bought.Cents, bought.Test, bought.TrackingCode }, c.RestaurantId);
                results.Add(new(c.Id, true, bought.Cents, bought.LabelUrl, bought.Test, false, null));
            }
            catch (PostageException ex)
            {
                await audit.LogAsync(me, "PostagePurchaseFailed", "Comment", c.Id, new { error = ex.Message }, c.RestaurantId);
                results.Add(new(c.Id, false, null, null, false, false, ex.Message));
            }
        }
        return results;
    }

    /// <summary>
    /// Records that cards were printed and, unless told otherwise, marks each as sent to the guest by mail
    /// (the same as recording a reply by hand), so the reply queue and follow-up clock stay right.
    /// </summary>
    [HttpPost("comments/printed")]
    public async Task<ActionResult<List<CommentSummary>>> Printed(PrintedRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.MarkResponded)) return Forbid();
        var cards = await Cards(me, req.CommentIds, ct);
        if (cards.Count == 0) return NotFound();
        var settings = await settingsStore.GetAsync(me.GroupId ?? 0, ct);
        var method = MailedMethod(settings);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var now = DateTime.UtcNow;

        foreach (var c in cards)
        {
            var bought = req.Postage == "bought" && c.PostageId is not null;
            var how = bought ? (c.PostageTest ? "test postage printed" : "postage printed on the card") : "stamp added by hand";
            c.PrintedAt = now;
            c.PrintedByName = me.DisplayName;
            c.PrintCount++;
            Activity(c, me, ActivityKind.Printed, text: c.PrintCount > 1 ? $"Printed again ({how})" : $"Printed on a postcard ({how})");
            if (req.MarkSent && !c.Responded)
            {
                c.Responded = true;
                c.RespondedAt = now;
                c.RespondedByUserId = me.Id;
                c.RespondedByName = me.DisplayName;
                c.ReplyMethod = method;
                c.RepliedOn = today < c.VisitDate ? c.VisitDate : today;
                Activity(c, me, ActivityKind.Replied, method, c.RepliedOn, $"Coupon card printed and mailed ({how}).");
            }
            c.LastActivityAt = now;
        }
        await db.SaveChangesAsync(ct);
        foreach (var c in cards)
            await audit.LogAsync(me, "CardPrinted", "Comment", c.Id, new { c.PrintCount, req.Postage, MarkedSent = req.MarkSent && c.RespondedAt == now }, c.RestaurantId);
        return await summaries.BuildAsync(me.GroupId, cards, _ => 0, ct);
    }

    // ---- Redeeming the coupon -------------------------------------------------------------------------------

    [HttpPost("comments/{id:int}/redeem")]
    public async Task<ActionResult<FollowUpResponse>> Redeem(int id, RedeemRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.MarkResponded)) return Forbid();
        var c = await One(me, id, ct);
        if (c is null) return NotFound();
        if (c.RedeemedOn is not null) return BadRequest(new { message = $"This coupon was already marked redeemed on {c.RedeemedOn:MMM d, yyyy} by {c.RedeemedByName}." });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var on = req.RedeemedOn ?? today;
        if (on > today.AddDays(1)) return BadRequest(new { message = "The redeemed date cannot be in the future." });
        if (on < c.DateIssued) return BadRequest(new { message = $"The coupon was issued on {c.DateIssued:MMM d, yyyy}, so it cannot have been redeemed before then." });
        c.RedeemedOn = on;
        c.RedeemedAt = DateTime.UtcNow;
        c.RedeemedByName = me.DisplayName;
        var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        var late = on > c.ExpiresOn ? $"Accepted after it expired on {c.ExpiresOn:MMM d, yyyy}." : null;
        var entry = Activity(c, me, ActivityKind.Redeemed, on: on, text: string.Join(" ", new[] { late, note }.Where(x => x is not null)) is { Length: > 0 } t ? t : null);
        c.LastActivityAt = entry.CreatedAt;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, "CouponRedeemed", "Comment", c.Id, new { RedeemedOn = on, AfterExpiry = late is not null, note }, c.RestaurantId);
        return await Response(me, c, ct);
    }

    [HttpPost("comments/{id:int}/unredeem")]
    public async Task<ActionResult<FollowUpResponse>> Unredeem(int id, UndoRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.MarkResponded)) return Forbid();
        var c = await One(me, id, ct);
        if (c is null) return NotFound();
        if (c.RedeemedOn is null) return BadRequest(new { message = "This coupon is not marked redeemed." });
        if (string.IsNullOrWhiteSpace(req.Reason)) return BadRequest(new { message = "Say why the redemption is being taken back, so the other managers know." });
        var was = c.RedeemedOn;
        c.RedeemedOn = null; c.RedeemedAt = null; c.RedeemedByName = null;
        var entry = Activity(c, me, ActivityKind.RedemptionUndone, text: req.Reason.Trim());
        c.LastActivityAt = entry.CreatedAt;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, "CouponRedemptionUndone", "Comment", c.Id, new { was, reason = req.Reason.Trim() }, c.RestaurantId);
        return await Response(me, c, ct);
    }

    // ---- Following up after the card was sent -----------------------------------------------------------------

    /// <summary>
    /// Records the call (or other contact) to check the card arrived, with notes about the conversation. When the
    /// guest was not reached it is kept as an attempt and the follow-up stays due.
    /// </summary>
    [HttpPost("comments/{id:int}/followed-up")]
    public async Task<ActionResult<FollowUpResponse>> FollowedUp(int id, FollowedUpRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.MarkResponded)) return Forbid();
        var c = await One(me, id, ct);
        if (c is null) return NotFound();
        if (!c.Responded) return BadRequest(new { message = "This card has not been sent yet, so there is nothing to follow up on. Record the reply first." });
        if (c.FollowedUpOn is not null) return BadRequest(new { message = $"{c.FollowedUpByName} already followed up on {c.FollowedUpOn:MMM d, yyyy}. Add a note instead." });
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var on = req.ContactedOn ?? today;
        if (on > today.AddDays(1)) return BadRequest(new { message = "The follow-up date cannot be in the future." });
        if (c.RepliedOn is { } sent && on < sent) return BadRequest(new { message = $"The card was sent on {sent:MMM d, yyyy}, so the follow-up cannot be before then." });
        var notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
        var method = string.IsNullOrWhiteSpace(req.Method) ? "Phone call" : req.Method.Trim();
        if (req.Reached && notes is null) return BadRequest(new { message = "Add a note about the conversation: did the card arrive, and what did the guest say?" });

        var entry = Activity(c, me, req.Reached ? ActivityKind.FollowedUp : ActivityKind.FollowUpAttempt, method, on, notes ?? "Did not reach the guest.");
        if (req.Reached)
        {
            c.FollowedUpOn = on;
            c.FollowedUpAt = DateTime.UtcNow;
            c.FollowedUpByName = me.DisplayName;
        }
        c.LastActivityAt = entry.CreatedAt;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, req.Reached ? "GuestFollowedUp" : "GuestFollowUpAttempted", "Comment", c.Id, new { method, on, notes }, c.RestaurantId);
        return await Response(me, c, ct);
    }

    /// <summary>How many guests are due a follow-up, and each restaurant's timing. Drives the notice at the top of the site.</summary>
    [HttpGet("follow-ups/summary")]
    public async Task<ActionResult<FollowUpSummary>> Summary(CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.ViewComments) || me.GroupId is null) return new FollowUpSummary(0, null, 0, []);
        var mine = me.RestaurantIds.ToList();
        var settings = await settingsStore.GetAsync(me.GroupId.Value, ct);
        var due = me.Has(ManagerPermission.MarkResponded) ? await followUps.DueAsync(me.GroupId, mine, ct) : [];
        var restaurants = await db.Restaurants.AsNoTracking().Where(r => mine.Contains(r.Id) && r.IsActive).OrderBy(r => r.Name)
            .Select(r => new { r.Id, r.Name, r.FollowUpDays }).ToListAsync(ct);
        return new FollowUpSummary(due.Count, due.Count == 0 ? null : due[0].DueOn, settings.FollowUpDays,
            restaurants.Select(r => new FollowUpRestaurant(r.Id, r.Name, r.FollowUpDays, r.FollowUpDays ?? settings.FollowUpDays, CanSetDays(me, r.Id))).ToList());
    }

    /// <summary>
    /// Sets how many days after a card is sent the guest is followed up with at one restaurant (null = the group's
    /// default, 0 = off). Its owners and the group's administrators may; managers only with the SetFollowUpDays permission.
    /// </summary>
    [HttpPut("follow-ups/restaurants/{id:int}")]
    public async Task<ActionResult<FollowUpRestaurant>> SetDays(int id, FollowUpDaysRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.CanAccessRestaurant(id)) return NotFound();
        if (!CanSetDays(me, id)) return StatusCode(403, new { message = "Only this restaurant's owners, the group's administrators, or a manager given that permission can change the follow-up timing." });
        if (req.Days is < 0 or > 90) return BadRequest(new { message = "Choose between 0 (off) and 90 days." });
        var r = await db.Restaurants.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();
        var before = r.FollowUpDays;
        r.FollowUpDays = req.Days;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, "FollowUpDaysChanged", "Restaurant", r.Id, new { r.Name, before, after = req.Days }, r.Id);
        var fallback = (await settingsStore.GetAsync(me.GroupId ?? 0, ct)).FollowUpDays;
        return new FollowUpRestaurant(r.Id, r.Name, r.FollowUpDays, r.FollowUpDays ?? fallback, true);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private static bool CanSetDays(CurrentUser me, int restaurantId) =>
        me.CanManageRestaurant(restaurantId) || (me.Role == UserRole.Manager && me.Has(ManagerPermission.SetFollowUpDays) && me.CanAccessRestaurant(restaurantId));

    /// <summary>The group's own wording for a mailed reply when it has one, so reports group together.</summary>
    private static string MailedMethod(GroupSettings settings) =>
        settings.ReplyMethods.FirstOrDefault(m => m.Contains("mailed", StringComparison.OrdinalIgnoreCase)) ?? CardFollowUps.MailedMethod;

    private static string? MissingAddress(GuestComment c)
    {
        string?[] guest = [c.StreetAddress, c.City, c.State, c.Zip];
        if (guest.Any(string.IsNullOrWhiteSpace)) return "The guest's street, city, state and zip are all needed to buy postage.";
        var r = c.Restaurant;
        string?[] from = [r.StreetAddress, r.City, r.State, r.Zip];
        if (from.Any(string.IsNullOrWhiteSpace)) return $"{r.Name} needs its full address (street, city, state, zip) as the return address. Add it on Restaurants & staff.";
        return null;
    }

    private async Task<List<GuestComment>> Cards(CurrentUser me, int[]? ids, CancellationToken ct)
    {
        var wanted = (ids ?? []).Distinct().Take(200).ToList();
        var mine = me.RestaurantIds.ToList();
        var found = await db.Comments.Include(c => c.Restaurant).Where(c => wanted.Contains(c.Id) && mine.Contains(c.RestaurantId)).ToListAsync(ct);
        return found.OrderBy(c => wanted.IndexOf(c.Id)).ToList();
    }

    private async Task<GuestComment?> One(CurrentUser me, int id, CancellationToken ct)
    {
        var c = await db.Comments.Include(x => x.Restaurant).FirstOrDefaultAsync(x => x.Id == id, ct);
        return c is null || !me.CanAccessRestaurant(c.RestaurantId) ? null : c;
    }

    private CommentActivity Activity(GuestComment c, CurrentUser me, ActivityKind kind, string? method = null, DateOnly? on = null, string? text = null)
    {
        var entry = new CommentActivity
        {
            CommentId = c.Id, Kind = kind, Method = method, ContactedOn = on, Text = text,
            CreatedByUserId = me.Id, CreatedByName = me.DisplayName, CreatedByRole = me.Role
        };
        db.CommentActivities.Add(entry);
        return entry;
    }

    private async Task<FollowUpResponse> Response(CurrentUser me, GuestComment c, CancellationToken ct)
    {
        var count = await db.Comments.CountAsync(x => x.GuestKey == c.GuestKey && x.Restaurant.GroupId == c.Restaurant.GroupId, ct);
        var summary = (await summaries.BuildAsync(me.GroupId, [c], _ => count, ct))[0];
        var history = (await db.CommentActivities.Where(a => a.CommentId == c.Id).OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync(ct)).Select(ActivityDto.From).ToList();
        return new FollowUpResponse(summary, history);
    }
}
