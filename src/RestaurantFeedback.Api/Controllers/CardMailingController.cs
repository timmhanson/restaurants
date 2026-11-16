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
    CommentSummaryBuilder summaries, CardFollowUps followUps, IPostageProvider postage, PostageOptions postageOptions,
    GroupPostageAccounts postageAccounts, IPostGridMailer mailer, GroupPostGridAccounts mailAccounts, PostGridOptions mailOptions) : ControllerBase
{
    // ---- Printing and postage -------------------------------------------------------------------------------

    [HttpGet("comments/print-config")]
    public async Task<ActionResult<PrintConfig>> Config(CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.MarkResponded)) return Forbid();
        var settings = await settingsStore.GetAsync(me.GroupId ?? 0, ct);
        var own = me.GroupId is int gid && postage.AcceptsAccountKeys && (await postageAccounts.DescribeAsync(gid, ct)).HasOwnAccount;
        var available = (own || postage.Configured) && (settings.Postcard?.AllowBoughtPostage ?? true);
        var paidBy = own ? $"{postage.Description}, your investment group's account" : postage.Description;
        var mail = me.GroupId is int g2 ? await mailAccounts.DescribeAsync(g2, ct) : new PostageAccountInfo(false, null, false, null, null);
        var art = me.GroupId is int g3 && await db.CardArtworks.AnyAsync(a => a.GroupId == g3, ct);
        return new PrintConfig(available, paidBy, available && me.Has(ManagerPermission.BuyPostage), postageOptions.MaxCentsPerCard, MailedMethod(settings))
        {
            MailAvailable = mail.HasOwnAccount,
            CanMail = mail.HasOwnAccount && me.Has(ManagerPermission.BuyPostage),
            MailTest = mail.LooksLikeTestKey,
            MailCentsPerCard = mailOptions.EstimatedCentsPerCard,
            MailMaxCards = mailOptions.MaxCardsPerMailing,
            HasCardArt = art
        };
    }

    // ---- Printed and mailed by PostGrid ---------------------------------------------------------------------

    /// <summary>
    /// Sends each card to PostGrid, which prints both sides and mails it, billed to the investment group's own
    /// PostGrid account. Money is spent, so it needs the BuyPostage permission, a confirmed count, a cap per mailing,
    /// and never sends a card twice: a card already with PostGrid is skipped, and each request carries an idempotency
    /// key so a retried request cannot mail the same card again. Sent cards are marked as sent to the guest.
    /// </summary>
    [HttpPost("comments/mail")]
    public async Task<ActionResult<List<MailOutcome>>> Mail(MailRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.BuyPostage) || !me.Has(ManagerPermission.MarkResponded)) return Forbid();
        if (me.GroupId is not int groupId) return BadRequest(new { message = "Select an investment group first." });
        var ids = (req.CommentIds ?? []).Distinct().ToArray();
        if (ids.Length == 0) return BadRequest(new { message = "Choose at least one card to mail." });
        if (ids.Length > mailOptions.MaxCardsPerMailing)
            return BadRequest(new { message = $"Up to {mailOptions.MaxCardsPerMailing} cards can be mailed at a time. Mail these in smaller batches." });
        if (req.ConfirmCount != ids.Length)
            return BadRequest(new { message = "The number of cards confirmed does not match the cards chosen. Nothing was sent; review the cards and confirm again." });
        var key = await mailAccounts.KeyAsync(groupId, ct);
        if (key is null)
            return BadRequest(new { message = "This investment group has no PostGrid account. An Investment Group Administrator can add it in Settings, under PostGrid account." });
        var cards = await Cards(me, ids, ct);
        if (cards.Count == 0) return NotFound();
        var settings = await settingsStore.GetAsync(groupId, ct);
        var art = await db.CardArtworks.AsNoTracking().Where(a => a.GroupId == groupId).Select(a => new { a.Hash, a.ContentType }).FirstOrDefaultAsync(ct);
        var artUrl = art is null ? null : ArtUrl(groupId, art.Hash, art.ContentType);
        var test = MailStatuses.IsKeyForTest(key);
        var method = MailedMethod(settings);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var results = new List<MailOutcome>();
        foreach (var c in cards)
        {
            if (c.MailId is not null && c.MailStatus != MailStatuses.Cancelled)
            {
                results.Add(new(c.Id, true, c.MailStatus, c.MailTest, true, null));
                continue;
            }
            if (c.Responded)
            {
                results.Add(new(c.Id, false, null, false, false, "This card is already marked as sent. Reopen it first if it really needs to go out again."));
                continue;
            }
            var problem = MissingAddress(c);
            if (problem is not null)
            {
                results.Add(new(c.Id, false, null, false, false, problem.Replace("to buy postage", "to mail the card")));
                continue;
            }
            var r = c.Restaurant;
            var request = new MailPostcardRequest(
                new MailContact(c.GuestFirstName, c.GuestLastName, null, c.StreetAddress!, null, c.City!, c.State!, c.Zip!),
                new MailContact(null, null, r.Name, r.StreetAddress!, null, r.City!, r.State!, r.Zip!),
                PostGridCardDesign.Front(artUrl, settings.AccentColor, r.Name, c.CouponOffer),
                PostGridCardDesign.Back(c, r, settings.CouponValidityMonths, settings.AccentColor),
                $"Coupon card for {c.GuestFirstName} {c.GuestLastName} ({r.Name})",
                new Dictionary<string, string> { ["cardId"] = c.Id.ToString(), ["restaurant"] = r.Name });
            try
            {
                var sent = await mailer.CreatePostcardAsync(key, request, $"rf-g{groupId}-card{c.Id}-{c.MailAttempts}", ct);
                var now = DateTime.UtcNow;
                c.MailAttempts++;
                c.MailProvider = "PostGrid";
                c.MailId = sent.Id;
                c.MailStatus = sent.Status;
                c.MailTest = test || !sent.Live;
                c.MailedAt = now;
                c.MailedByName = me.DisplayName;
                c.MailEstimateCents = c.MailTest ? 0 : mailOptions.EstimatedCentsPerCard;
                c.Responded = true;
                c.RespondedAt = now;
                c.RespondedByUserId = me.Id;
                c.RespondedByName = me.DisplayName;
                c.ReplyMethod = method;
                c.RepliedOn = today < c.VisitDate ? c.VisitDate : today;
                var how = c.MailTest ? "PostGrid test order: not printed, mailed or charged" : "PostGrid prints and mails it";
                Activity(c, me, ActivityKind.Mailed, text: $"Sent to PostGrid to print and mail ({how}).");
                var entry = Activity(c, me, ActivityKind.Replied, method, c.RepliedOn, "Coupon card mailed by PostGrid.");
                c.LastActivityAt = entry.CreatedAt;
                // Saved card by card: once a card is with PostGrid the record must exist even if a later one fails.
                await db.SaveChangesAsync(ct);
                await audit.LogAsync(me, "CardMailed", "Comment", c.Id,
                    new { Provider = "PostGrid", sent.Id, sent.Status, Test = c.MailTest, EstimatedCents = c.MailEstimateCents, PaidBy = "the investment group's PostGrid account" }, c.RestaurantId);
                results.Add(new(c.Id, true, sent.Status, c.MailTest, false, null));
            }
            catch (PostGridException ex)
            {
                // A definite refusal: the next try for this card must use a new idempotency key, or PostGrid would
                // just repeat this answer. When PostGrid could not be reached the key is kept, so a retry can never
                // create a second card if the first request did get through.
                if (ex.Answered)
                {
                    c.MailAttempts++;
                    await db.SaveChangesAsync(ct);
                }
                await audit.LogAsync(me, "CardMailFailed", "Comment", c.Id, new { Provider = "PostGrid", error = ex.Message }, c.RestaurantId);
                results.Add(new(c.Id, false, null, false, false, ex.Message));
            }
        }
        return results;
    }

    /// <summary>Asks PostGrid where each mailed card is (and for a fresh link to its preview).</summary>
    [HttpPost("comments/mail/status")]
    public async Task<ActionResult<List<MailStatusDto>>> MailStatus(CardIdsRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.MarkResponded)) return Forbid();
        if (me.GroupId is not int groupId) return BadRequest(new { message = "Select an investment group first." });
        var cards = (await Cards(me, req.CommentIds, ct)).Where(c => c.MailId is not null).ToList();
        var key = await mailAccounts.KeyAsync(groupId, ct);
        var results = new List<MailStatusDto>();
        foreach (var c in cards)
        {
            if (key is null)
            {
                results.Add(new(c.Id, c.MailStatus, c.MailTest, null, "The group's PostGrid account has been removed, so the latest status cannot be fetched."));
                continue;
            }
            try
            {
                var now = await mailer.GetPostcardAsync(key, c.MailId!, ct);
                c.MailStatus = now.Status;
                results.Add(new(c.Id, now.Status, c.MailTest, now.PreviewUrl, null));
            }
            catch (PostGridException ex)
            {
                results.Add(new(c.Id, c.MailStatus, c.MailTest, null, ex.Message));
            }
        }
        await db.SaveChangesAsync(ct);
        return results;
    }

    /// <summary>
    /// Cancels a card PostGrid has not started printing, and puts it back in the list of cards waiting to be sent.
    /// </summary>
    [HttpPost("comments/{id:int}/mail/cancel")]
    public async Task<ActionResult<MailStatusDto>> CancelMail(int id, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.BuyPostage) || !me.Has(ManagerPermission.MarkResponded)) return Forbid();
        if (me.GroupId is not int groupId) return BadRequest(new { message = "Select an investment group first." });
        var c = await One(me, id, ct);
        if (c is null) return NotFound();
        if (c.MailId is null || c.MailStatus == MailStatuses.Cancelled) return BadRequest(new { message = "This card is not waiting to be mailed by PostGrid." });
        var key = await mailAccounts.KeyAsync(groupId, ct);
        if (key is null) return BadRequest(new { message = "The group's PostGrid account has been removed, so the card can only be cancelled in PostGrid's own dashboard." });
        MailedPostcard after;
        try
        {
            after = await mailer.CancelPostcardAsync(key, c.MailId, ct);
        }
        catch (PostGridException ex)
        {
            // Most likely it has started printing. Show where it is now.
            try { c.MailStatus = (await mailer.GetPostcardAsync(key, c.MailId, ct)).Status; await db.SaveChangesAsync(ct); } catch (PostGridException) { }
            await audit.LogAsync(me, "CardMailCancelFailed", "Comment", c.Id, new { c.MailId, error = ex.Message }, c.RestaurantId);
            return BadRequest(new { message = $"PostGrid could not cancel this card: {ex.Message} A card can only be cancelled until it starts printing." });
        }
        c.MailStatus = after.Status == MailStatuses.Ready ? MailStatuses.Cancelled : after.Status;
        var wasSentOn = c.RepliedOn;
        if (c.MailStatus == MailStatuses.Cancelled && c.Responded)
        {
            // Not going out after all: back to "not sent", so it shows up to print or mail again.
            c.Responded = false; c.RespondedAt = null; c.RespondedByUserId = null; c.RespondedByName = null; c.ReplyMethod = null; c.RepliedOn = null;
        }
        var entry = Activity(c, me, ActivityKind.MailCancelled, text: "PostGrid mailing cancelled before printing. The card is waiting to be sent again.");
        c.LastActivityAt = entry.CreatedAt;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, "CardMailCancelled", "Comment", c.Id, new { c.MailId, c.MailStatus, wasSentOn }, c.RestaurantId);
        return new MailStatusDto(c.Id, c.MailStatus, c.MailTest, null, null);
    }

    /// <summary>Both sides of a card exactly as they would be sent to PostGrid, for the preview.</summary>
    [HttpGet("comments/{id:int}/mail/preview")]
    public async Task<ActionResult<MailPreview>> MailPreview(int id, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.MarkResponded)) return Forbid();
        var c = await One(me, id, ct);
        if (c is null) return NotFound();
        var groupId = c.Restaurant.GroupId;
        var settings = await settingsStore.GetAsync(groupId, ct);
        var art = await db.CardArtworks.AsNoTracking().Where(a => a.GroupId == groupId).Select(a => new { a.Hash, a.ContentType }).FirstOrDefaultAsync(ct);
        var artUrl = art is null ? null : ArtUrl(groupId, art.Hash, art.ContentType);
        return new MailPreview(PostGridCardDesign.Front(artUrl, settings.AccentColor, c.Restaurant.Name, c.CouponOffer),
            PostGridCardDesign.Back(c, c.Restaurant, settings.CouponValidityMonths, settings.AccentColor));
    }

    /// <summary>The public address of a group's card artwork, which PostGrid downloads when it prints.</summary>
    private string ArtUrl(int groupId, string hash, string contentType)
    {
        var host = Request.Host.Host;
        var local = host is "localhost" or "127.0.0.1" or "[::1]";
        var site = !string.IsNullOrWhiteSpace(mailOptions.PublicBaseUrl)
            ? mailOptions.PublicBaseUrl.TrimEnd('/')
            : $"{(local ? Request.Scheme : "https")}://{Request.Host}";
        return $"{site}/card-art/{groupId}/{hash}{(contentType == "image/png" ? ".png" : ".jpg")}";
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
        // The group's own account pays. Without one, the site-wide account is used if the site has one.
        var accountKey = me.GroupId is int gid && postage.AcceptsAccountKeys ? await postageAccounts.KeyAsync(gid, ct) : null;
        if (accountKey is null && !postage.Configured)
            return BadRequest(new { message = "No postage account is set up for this investment group. An administrator can add the group's EasyPost account in Settings. Until then, print the cards and add stamps by hand." });
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
                    card.WidthIn, card.HeightIn, accountKey, ct);
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
                await audit.LogAsync(me, "PostagePurchased", "Comment", c.Id,
                    new { bought.Provider, bought.Id, bought.Cents, bought.Test, bought.TrackingCode, PaidBy = accountKey is null ? "the site's account" : "the investment group's account" }, c.RestaurantId);
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
