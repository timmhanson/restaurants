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
    GroupPostageAccounts postageAccounts, IPostGridMailer mailer, SiteMailing siteMailing, MailLedger ledger, CardDesigns designs,
    PostGridOptions mailOptions, IHttpClientFactory httpFactory, ILogger<CardMailingController> log, CardCodes codes, SiteAddress siteAddress, SendHistory history) : ControllerBase
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
        var terms = await ledger.TermsAsync(me.GroupId ?? 0, ct);
        var used = me.GroupId is int g2 ? (await ledger.ThisMonthAsync(g2, ct)).Cents : 0;
        // Whether cards can be mailed, and whether for real or as tests, is worked out restaurant by restaurant.
        var groupRow = await db.GroupBilling.AsNoTracking().FirstOrDefaultAsync(x => x.GroupId == me.GroupId, ct);
        var myIds = me.RestaurantIds.ToList();
        var byRestaurant = (await db.Restaurants.AsNoTracking().Where(r => r.GroupId == me.GroupId && r.IsActive && myIds.Contains(r.Id)).ToListAsync(ct))
            .Select(r => (r.Id, Rules: GroupPolicy.For(siteMailing.Billing, groupRow, r)))
            .Select(x => new MailRestaurantInfo(x.Id, x.Rules.MailOn, x.Rules.MailLive, siteMailing.For(x.Rules.MailLive) is not null)).ToList();
        var anyOn = byRestaurant.Any(x => x.On);
        var ready = byRestaurant.Any(x => x.On && x.Ready);
        var test = ready && !byRestaurant.Any(x => x.On && x.Ready && x.Live);
        var needsPayMethod = terms.NeedsPayMethod;
        var mailOn = ready && me.GroupId is not null && terms.LimitCents > 0;
        // The first thing in the way, in the order someone would have to deal with them.
        var blockedBy = siteMailing.Any is null || me.GroupId is null ? "site" : !anyOn ? "group" : !ready ? "site" : terms.LimitCents <= 0 ? "limit"
            : needsPayMethod ? "payment" : !me.Has(ManagerPermission.BuyPostage) ? "permission" : null;
        return new PrintConfig(available, paidBy, available && me.Has(ManagerPermission.BuyPostage), postageOptions.MaxCentsPerCard, MailedMethod(settings))
        {
            MailAvailable = mailOn,
            CanMail = mailOn && !needsPayMethod && me.Has(ManagerPermission.BuyPostage),
            MailNeedsPayMethod = mailOn && needsPayMethod,
            MailBlockedBy = blockedBy,
            MailOnCredit = mailOn && terms.OnCredit && !needsPayMethod,
            MailCreditCents = terms.CreditCents,
            PrintFeeCents = me.GroupId is null ? 0 : terms.PrintFeeCents,
            MailRestaurants = byRestaurant,
            MailTest = test,
            MailCentsPerCard = terms.PriceCents,
            MailMaxCards = mailOptions.MaxCardsPerMailing,
            MailLimitCents = terms.LimitCents,
            MailUsedCents = used
        };
    }

    /// <summary>
    /// The QR code for each card, for the cards printed in the restaurant. The code leads to this site's verify page
    /// for that card; it shows nothing by itself and needs a sign-in to open.
    /// </summary>
    [HttpPost("comments/qr")]
    public async Task<ActionResult<List<CardQr>>> QrCodes(CardIdsRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.MarkResponded)) return Forbid();
        var origin = siteAddress.Origin();
        return (await Cards(me, req.CommentIds, ct)).Select(c =>
        {
            var url = codes.Url(c.PublicId, origin);
            return new CardQr(c.Id, c.PublicId, url, CardCodes.QrDataUri(url));
        }).ToList();
    }

    // ---- Printed and mailed for the group ---------------------------------------------------------------------

    private static string Dollars(int cents) => $"${cents / 100m:0.00}";
    private const string NotSetUp = "Mailing cards is not set up on this site yet. Ask the site's administrator.";

    /// <summary>
    /// Sends each card to the mail house (PostGrid, on the site's own account), which prints both sides and mails
    /// it. The investment group is billed by the site, per card. Money is spent, so it needs the BuyPostage
    /// permission, a confirmed count and price, a cap per mailing and a monthly limit per group, and never sends a
    /// card twice: a card already with the mail house is skipped, and each request carries an idempotency key so a
    /// retried request cannot mail the same card again. Sent cards are marked as sent to the guest.
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
        if (siteMailing.Any is null) return BadRequest(new { message = NotSetUp });
        var pricing = await ledger.TermsAsync(groupId, ct);
        var groupRow = await db.GroupBilling.AsNoTracking().FirstOrDefaultAsync(x => x.GroupId == groupId, ct);
        if (!pricing.Enabled)
            return BadRequest(new { message = "Having cards mailed is not switched on for this investment group yet. Ask the site's administrator to switch it on. Until then, print the cards here." });
        if (pricing.NeedsPayMethod)
            return BadRequest(new
            {
                message = pricing.CreditCents > 0
                    ? $"This investment group's credit ({Dollars(pricing.CreditCents)}) does not cover a card at {Dollars(pricing.PriceCents)}. Add a payment method on Billing to keep mailing. Until then, print the cards here."
                    : "Add a payment method on Billing before cards can be mailed for this investment group. Until then, print the cards here."
            });
        var cards = await Cards(me, ids, ct);
        if (cards.Count == 0) return NotFound();
        // Each card goes by its own restaurant's settings: whether cards are mailed for it at all, and whether for
        // real (the live key) or as a test (the test key: a proof only, nothing printed, mailed or billed).
        var rules = cards.Select(c => c.Restaurant).DistinctBy(r => r.Id).ToDictionary(r => r.Id, r => GroupPolicy.For(siteMailing.Billing, groupRow, r));
        bool GoesLive(GuestComment c) => rules[c.RestaurantId] is { MailOn: true, MailLive: true } && siteMailing.Live is not null;
        var anyLive = cards.Any(GoesLive);
        if (anyLive && req.ConfirmCentsPerCard != pricing.PriceCents)
            return BadRequest(new { message = $"The price is now {Dollars(pricing.PriceCents)} a card. Nothing was sent; review the cards and confirm again." });

        // Sending again: a card that went before goes once more (it never arrived, or its address was wrong). The
        // earlier card must be past cancelling, or it is the one to cancel and correct instead.
        var again = req.Resend;
        bool NotPrintedYet(GuestComment c) => c.MailId is not null && c.MailStatus is MailStatuses.Ready or "printing";
        bool AlreadyWithMailHouse(GuestComment c) => c.MailId is not null && c.MailStatus != MailStatuses.Cancelled && (!again || NotPrintedYet(c));
        bool AlreadySent(GuestComment c) => c.Responded && !again;
        if (anyLive)
        {
            var limit = pricing.LimitCents;
            if (limit <= 0) return BadRequest(new { message = "Mailing cards is switched off for this investment group. Ask the site's administrator." });
            var used = (await ledger.ThisMonthAsync(groupId, ct)).Cents;
            var toSend = cards.Count(c => GoesLive(c) && !AlreadyWithMailHouse(c) && !AlreadySent(c) && MissingAddress(c) is null);
            // With no payment method saved, the group mails on its credit, and only as far as the credit goes.
            if (pricing.OnCredit && toSend * pricing.PriceCents > pricing.CreditCents)
            {
                await audit.LogAsync(me, "CardMailOverCredit", "Group", groupId, new { Cards = toSend, WouldBillCents = toSend * pricing.PriceCents, pricing.CreditCents });
                return BadRequest(new
                {
                    message = $"This investment group's credit ({Dollars(pricing.CreditCents)}) covers {pricing.CardsOnCredit} card{(pricing.CardsOnCredit == 1 ? "" : "s")} at {Dollars(pricing.PriceCents)} each, "
                              + $"and {toSend} were chosen. Nothing was sent. Mail fewer cards, or add a payment method on Billing."
                });
            }
            if (used + toSend * pricing.PriceCents > limit)
            {
                await audit.LogAsync(me, "CardMailOverLimit", "Group", groupId, new { Cards = toSend, WouldBillCents = toSend * pricing.PriceCents, UsedCents = used, LimitCents = limit });
                return BadRequest(new
                {
                    message = $"Mailing {toSend} card{(toSend == 1 ? "" : "s")} ({Dollars(toSend * pricing.PriceCents)}) would take this investment group over its limit of {Dollars(limit)} "
                              + $"for mailed cards this month ({Dollars(used)} so far). Nothing was sent. Mail fewer cards, or ask the site's administrator to raise the limit."
                });
            }
        }

        var settings = await settingsStore.GetAsync(groupId, ct);
        var allDesigns = await designs.ForGroupAsync(groupId, ct);
        var method = MailedMethod(settings);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var origin = siteAddress.Origin();

        var results = new List<MailOutcome>();
        foreach (var c in cards)
        {
            if (again && NotPrintedYet(c))
            {
                results.Add(new(c.Id, false, c.MailStatus, c.MailTest, true, "The card sent before has not been printed yet. Cancel it, correct the address if need be, and send it again; that way it is not paid for twice."));
                continue;
            }
            if (AlreadyWithMailHouse(c))
            {
                results.Add(new(c.Id, true, c.MailStatus, c.MailTest, true, null));
                continue;
            }
            if (AlreadySent(c))
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
            var rule = rules[r.Id];
            if (!rule.MailOn)
            {
                results.Add(new(c.Id, false, null, false, false, $"Having cards mailed is not switched on for {r.Name}. Print this card here instead, or have it switched on for the restaurant."));
                continue;
            }
            var test = !rule.MailLive;
            if (siteMailing.For(rule.MailLive) is not { } account)
            {
                results.Add(new(c.Id, false, null, false, false,
                    $"{r.Name} is set to {(test ? "test" : "real")} mailing, and that is not set up on this site yet. Ask the site's administrator, or print this card here instead."));
                continue;
            }
            var look = designs.Look(CardDesigns.For(allDesigns, r.Id), groupId, settings.AccentColor) with { TeamSignature = GroupPolicy.SignatureForAll(groupRow) };
            var request = new MailPostcardRequest(
                new MailContact(c.GuestFirstName, c.GuestLastName, null, c.StreetAddress!, null, c.City!, c.State!, c.Zip!),
                new MailContact(null, null, r.Name, r.StreetAddress!, null, r.City!, r.State!, r.Zip!),
                PostGridCardDesign.Front(look, c, r, settings.CouponValidityMonths),
                PostGridCardDesign.Back(c, r, settings.CouponValidityMonths, settings.AccentColor, look, CardCodes.QrDataUri(codes.Url(c.PublicId, origin))),
                $"Coupon card for {c.GuestFirstName} {c.GuestLastName} ({r.Name})",
                new Dictionary<string, string> { ["cardId"] = c.Id.ToString(), ["groupId"] = groupId.ToString(), ["restaurant"] = r.Name });
            try
            {
                var sent = await mailer.CreatePostcardAsync(account.ApiKey, request, $"rf-g{groupId}-card{c.Id}-{c.MailAttempts}", ct);
                // The same request arriving twice gets the same card back from the mail house: it is not recorded, or billed, twice.
                if (await db.CardSends.AnyAsync(s => s.CommentId == c.Id && s.MailId == sent.Id, ct))
                {
                    results.Add(new(c.Id, true, sent.Status, c.MailTest, true, null));
                    continue;
                }
                var sentBefore = c.Responded || await history.SentBeforeAsync(c.Id, ct);
                var firstReplyAt = c.Responded ? c.RespondedAt : null;
                var now = DateTime.UtcNow;
                c.MailAttempts++;
                c.MailProvider = "PostGrid";
                c.MailId = sent.Id;
                c.MailStatus = sent.Status;
                c.MailTest = test || !sent.Live;
                c.MailedAt = now;
                c.MailedByName = me.DisplayName;
                c.MailEstimateCents = c.MailTest ? 0 : pricing.CostCents;
                c.MailFeeCents = c.MailTest ? 0 : pricing.FeeCents;
                var billed = (c.MailEstimateCents ?? 0) + (c.MailFeeCents ?? 0);
                var record = await history.AddAsync(c, me, SendMethods.Mail, SendOutcomes.Sent, sentBefore, ct);
                (record.MailId, record.MailStatus, record.Test, record.CostCents, record.FeeCents) = (sent.Id, sent.Status, c.MailTest, c.MailEstimateCents ?? 0, c.MailFeeCents ?? 0);
                var again2 = sentBefore ? " again" : "";
                Activity(c, me, ActivityKind.Mailed, text: c.MailTest
                    ? $"Sent{again2} to be printed and mailed (test order: not printed, mailed or charged)."
                    : $"Sent{again2} to be printed and mailed for the restaurant ({Dollars(billed)}).");
                if (firstReplyAt is null)
                {
                    // The first time the guest is replied to. A card sent again keeps the day it was first replied to.
                    c.Responded = true;
                    c.RespondedAt = now;
                    c.RespondedByUserId = me.Id;
                    c.RespondedByName = me.DisplayName;
                    c.ReplyMethod = method;
                    c.RepliedOn = today < c.VisitDate ? c.VisitDate : today;
                    var entry = Activity(c, me, ActivityKind.Replied, method, c.RepliedOn, "Coupon card printed and mailed for the restaurant.");
                    c.LastActivityAt = entry.CreatedAt;
                }
                else c.LastActivityAt = now;
                // Saved card by card: once a card is with the mail house the record must exist even if a later one fails.
                await db.SaveChangesAsync(ct);
                await audit.LogAsync(me, "CardMailed", "Comment", c.Id, new { sent.Id, sent.Status, Test = c.MailTest, BilledCents = billed, SentAgain = sentBefore, Attempt = record.Number }, c.RestaurantId);
                results.Add(new(c.Id, true, sent.Status, c.MailTest, false, null));
            }
            catch (PostGridException ex)
            {
                // A definite refusal: the next try for this card must use a new idempotency key, or the mail house
                // would just repeat this answer. When it could not be reached the key is kept, so a retry can never
                // create a second card if the first request did get through.
                if (ex.Answered)
                {
                    c.MailAttempts++;
                    await db.SaveChangesAsync(ct);
                }
                log.LogWarning("PostGrid did not take card {CardId} for group {GroupId}: {Message}", c.Id, groupId, ex.Message);
                var said = MailStatuses.ForClients(ex.Message);
                if (ex.Answered)
                {
                    // The refusal goes into the card's history, with the address that was refused.
                    var refused = await history.AddAsync(c, me, SendMethods.Mail, SendOutcomes.Refused, c.Responded, ct);
                    (refused.Test, refused.Error) = (test, said);
                    await db.SaveChangesAsync(ct);
                }
                await audit.LogAsync(me, "CardMailFailed", "Comment", c.Id, new { error = said }, c.RestaurantId);
                results.Add(new(c.Id, false, null, false, false, said) { AddressProblem = MailStatuses.IsAboutTheAddress(ex.Message) });
            }
        }
        return results;
    }

    /// <summary>Asks the mail house where each mailed card is, and whether it has a proof to show.</summary>
    [HttpPost("comments/mail/status")]
    public async Task<ActionResult<List<MailStatusDto>>> MailStatus(CardIdsRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.MarkResponded)) return Forbid();
        if (me.GroupId is null) return BadRequest(new { message = "Select an investment group first." });
        var cards = (await Cards(me, req.CommentIds, ct)).Where(c => c.MailId is not null).ToList();
        var results = new List<MailStatusDto>();
        foreach (var c in cards)
        {
            // A card is asked about with the key it was sent with: the test key for a test order, the live key for a real one.
            var account = siteMailing.For(!c.MailTest);
            if (account is null)
            {
                results.Add(new(c.Id, c.MailStatus, c.MailTest, false, "The latest status cannot be fetched just now. Ask the site's administrator."));
                continue;
            }
            try
            {
                var now = await mailer.GetPostcardAsync(account.ApiKey, c.MailId!, ct);
                c.MailStatus = now.Status;
                await history.SyncStatusAsync(c, ct);
                results.Add(new(c.Id, now.Status, c.MailTest, !string.IsNullOrWhiteSpace(now.PreviewUrl), null));
            }
            catch (PostGridException ex)
            {
                results.Add(new(c.Id, c.MailStatus, c.MailTest, false, MailStatuses.ForClients(ex.Message)));
            }
        }
        await db.SaveChangesAsync(ct);
        return results;
    }

    /// <summary>
    /// Asks the mailing service where the cards of this person's restaurants are, for the ones it has not finished
    /// with, so a status is never left standing at "waiting to print". Sent Postcards calls it when it opens; a
    /// background worker does the same for every restaurant. Returns how many statuses changed.
    /// </summary>
    [HttpPost("comments/mail/refresh")]
    public async Task<ActionResult<object>> RefreshMailStatuses([FromServices] MailStatusRefresher refresher, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.ViewComments)) return Forbid();
        if (me.GroupId is null) return new { changed = 0 };
        return new { changed = await refresher.RefreshAsync(me.RestaurantIds.ToList(), 200, ct) };
    }

    /// <summary>
    /// The mail house's proof of one printed card (a PDF), fetched by the site and passed on, so the person never
    /// leaves this site to see it.
    /// </summary>
    [HttpGet("comments/{id:int}/mail/proof")]
    public async Task<IActionResult> MailProof(int id, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.MarkResponded)) return Forbid();
        var c = await One(me, id, ct);
        if (c?.MailId is null) return NotFound(new { message = "This card has not been sent to be mailed." });
        if (siteMailing.For(!c.MailTest) is not { } account) return BadRequest(new { message = NotSetUp });
        var notReady = NotFound(new { message = "The proof is not ready yet. Try again in a minute." });
        try
        {
            var now = await mailer.GetPostcardAsync(account.ApiKey, c.MailId, ct);
            if (!Uri.TryCreate(now.PreviewUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps) return notReady;
            var client = httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            using var res = await client.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return notReady;
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length is 0 or > 25_000_000) return notReady;
            var type = res.Content.Headers.ContentType?.MediaType;
            return File(bytes, type is "image/png" or "image/jpeg" ? type : "application/pdf");
        }
        catch (PostGridException ex)
        {
            return BadRequest(new { message = MailStatuses.ForClients(ex.Message) });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return notReady;
        }
    }

    /// <summary>
    /// Cancels a card the mail house has not started printing, and puts it back in the list of cards waiting to be sent.
    /// </summary>
    [HttpPost("comments/{id:int}/mail/cancel")]
    public async Task<ActionResult<MailStatusDto>> CancelMail(int id, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.BuyPostage) || !me.Has(ManagerPermission.MarkResponded)) return Forbid();
        if (me.GroupId is null) return BadRequest(new { message = "Select an investment group first." });
        var c = await One(me, id, ct);
        if (c is null) return NotFound();
        if (c.MailId is null || c.MailStatus == MailStatuses.Cancelled) return BadRequest(new { message = "This card is not waiting to be mailed." });
        if (siteMailing.For(!c.MailTest) is not { } account)
            return BadRequest(new { message = "This card cannot be cancelled from here just now. Ask the site's administrator to cancel it." });
        MailedPostcard after;
        try
        {
            after = await mailer.CancelPostcardAsync(account.ApiKey, c.MailId, ct);
        }
        catch (PostGridException ex)
        {
            // Most likely it has started printing. Show where it is now.
            try { c.MailStatus = (await mailer.GetPostcardAsync(account.ApiKey, c.MailId, ct)).Status; await db.SaveChangesAsync(ct); } catch (PostGridException) { }
            var said = MailStatuses.ForClients(ex.Message);
            await audit.LogAsync(me, "CardMailCancelFailed", "Comment", c.Id, new { c.MailId, error = said }, c.RestaurantId);
            return BadRequest(new { message = $"This card could not be cancelled: {said} A card can only be cancelled until it starts printing." });
        }
        c.MailStatus = after.Status == MailStatuses.Ready ? MailStatuses.Cancelled : after.Status;
        await history.SyncStatusAsync(c, ct);
        var wasSentOn = c.RepliedOn;
        // A card that had gone before this send stays "sent": only this latest attempt is called off.
        var wentBefore = await db.CardSends.AnyAsync(s => s.CommentId == c.Id && s.Outcome == SendOutcomes.Sent && s.MailId != c.MailId, ct);
        if (c.MailStatus == MailStatuses.Cancelled && c.Responded && !wentBefore)
        {
            // Not going out after all: back to "not sent", so it shows up to print or mail again.
            c.Responded = false; c.RespondedAt = null; c.RespondedByUserId = null; c.RespondedByName = null; c.ReplyMethod = null; c.RepliedOn = null;
        }
        var entry = Activity(c, me, ActivityKind.MailCancelled, text: "Mailing cancelled before printing, so it is not billed. The card is waiting to be sent again.");
        c.LastActivityAt = entry.CreatedAt;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, "CardMailCancelled", "Comment", c.Id, new { c.MailId, c.MailStatus, wasSentOn }, c.RestaurantId);
        return new MailStatusDto(c.Id, c.MailStatus, c.MailTest, false, null);
    }

    /// <summary>Both sides of a card exactly as they would be sent to the mail house, for the preview.</summary>
    [HttpGet("comments/{id:int}/mail/preview")]
    public async Task<ActionResult<MailPreview>> MailPreview(int id, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.MarkResponded)) return Forbid();
        var c = await One(me, id, ct);
        if (c is null) return NotFound();
        var groupId = c.Restaurant.GroupId;
        var settings = await settingsStore.GetAsync(groupId, ct);
        var groupRow = await db.GroupBilling.AsNoTracking().FirstOrDefaultAsync(x => x.GroupId == groupId, ct);
        var look = designs.Look(CardDesigns.For(await designs.ForGroupAsync(groupId, ct), c.RestaurantId), groupId, settings.AccentColor) with { TeamSignature = GroupPolicy.SignatureForAll(groupRow) };
        return new MailPreview(PostGridCardDesign.Front(look, c, c.Restaurant, settings.CouponValidityMonths),
            PostGridCardDesign.Back(c, c.Restaurant, settings.CouponValidityMonths, settings.AccentColor, look, CardCodes.QrDataUri(codes.Url(c.PublicId, siteAddress.Origin()))));
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
        // A card the restaurant prints itself for a stamp by hand carries the site's fee, once, at its first print.
        var printFee = me.GroupId is int feeGroup ? (await ledger.TermsAsync(feeGroup, ct)).PrintFeeCents : 0;
        var charged = new List<GuestComment>();

        foreach (var c in cards)
        {
            var bought = req.Postage == "bought" && c.PostageId is not null;
            if (!bought && printFee > 0 && c.PrintCount == 0 && c.PrintFeeCents is null)
            {
                c.PrintFeeCents = printFee;
                c.PrintFeeAt = now;
                charged.Add(c);
            }
            var how = bought ? (c.PostageTest ? "test postage printed" : "postage printed on the card") : "stamp added by hand";
            c.PrintedAt = now;
            c.PrintedByName = me.DisplayName;
            c.PrintCount++;
            Activity(c, me, ActivityKind.Printed, text: c.PrintCount > 1 ? $"Printed again ({how})" : $"Printed on a postcard ({how})");
            // Into the card's history when this printing is what sends it: the first time, or printed to be sent again.
            if (req.MarkSent && (!c.Responded || req.Resend))
                await history.AddAsync(c, me, SendMethods.Stamp, SendOutcomes.Sent, c.Responded, ct);
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
            await audit.LogAsync(me, "CardPrinted", "Comment", c.Id, new { c.PrintCount, req.Postage, MarkedSent = req.MarkSent && c.RespondedAt == now, PrintFeeCents = charged.Contains(c) ? c.PrintFeeCents : null }, c.RestaurantId);
        foreach (var byRestaurant in charged.GroupBy(c => c.RestaurantId))
            await audit.LogAsync(me, "CardPrintFeeCharged", "Restaurant", byRestaurant.Key,
                new { Cards = byRestaurant.Count(), CentsPerCard = printFee, TotalCents = byRestaurant.Count() * printFee, CardIds = byRestaurant.Select(c => c.Id).ToArray() }, byRestaurant.Key);
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
        me.Has(ManagerPermission.SetFollowUpDays) && (me.CanManageRestaurant(restaurantId) || (me.Role == UserRole.Manager && me.CanAccessRestaurant(restaurantId)));

    /// <summary>The group's own wording for a mailed reply when it has one, so reports group together.</summary>
    private static string MailedMethod(GroupSettings settings) =>
        settings.ReplyMethods.FirstOrDefault(m => m.Contains("mailed", StringComparison.OrdinalIgnoreCase)) ?? CardFollowUps.MailedMethod;

    private static string? MissingAddress(GuestComment c)
    {
        string?[] guest = [c.StreetAddress, c.City, c.State, c.Zip];
        if (guest.Any(string.IsNullOrWhiteSpace)) return "The guest's street, city, state and zip are all needed to buy postage.";
        var r = c.Restaurant;
        string?[] from = [r.StreetAddress, r.City, r.State, r.Zip];
        if (from.Any(string.IsNullOrWhiteSpace)) return $"{r.Name} needs its full address (street, city, state, zip) as the return address. Add it on Restaurants & Staff.";
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
