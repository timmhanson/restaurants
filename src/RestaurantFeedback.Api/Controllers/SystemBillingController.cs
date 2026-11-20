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
/// The System Administrator's billing for mailed cards: what the mail house costs, the site's fee (for every group and
/// per group), monthly limits, how and when bills go out, every bill and whether it has been paid. The site pays the
/// mail house first, so this is where the System Administrator sees what each investment group owes.
/// </summary>
[ApiController]
[Route("api/system/billing")]
[Authorize(Roles = "SuperAdmin")]
public class SystemBillingController(AppDbContext db, AuditService audit, CurrentUserProvider users, SiteMailing site, MailLedger ledger, BillingService billing,
    PaymentAccountStore payAccount, IEmailSender email) : ControllerBase
{
    private static readonly string[] PaidHow = ["check", "bank", "card", "cash", "other"];
    /// <summary>The most one credit can be: $10,000. Larger amounts are given as more than one credit, each with its own details.</summary>
    public const int MaxCreditCents = 1_000_000;
    private static readonly string[] PayRules = ["site", "required", "notrequired"];

    /// <param name="groupId">Only this investment group's bills.</param>
    /// <param name="status">open, processing, paid, void or overdue.</param>
    /// <param name="month">yyyy-MM.</param>
    [HttpGet]
    public Task<SystemBillingDto> Get(int? groupId, string? status, string? month, CancellationToken ct) => Dto(groupId, status, month, ct);

    [HttpPut("settings")]
    public async Task<ActionResult<SystemBillingDto>> SaveSettings(BillingSettings req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (req.PrintBills && req.Normalize() is { HasAddress: false })
            return BadRequest(new { message = "To post printed statements, enter your business address: it is the return address on the envelope." });
        var before = site.Billing;
        await site.SaveBillingAsync(req, me.DisplayName, ct);
        var after = site.Billing;
        await audit.LogAsync(me, "BillingSettingsChanged", details: new
        {
            Before = new { before.CostCents, before.FeeCents, before.MonthlyLimitCents, before.BillDay, before.DueDays, before.EmailBills, before.PrintBills, before.CardFeePercent, before.BankFeePercent, before.RequirePaymentMethod, before.ChargeHandPrintFee, before.MailMode },
            After = new { after.CostCents, after.FeeCents, after.MonthlyLimitCents, after.BillDay, after.DueDays, after.EmailBills, after.PrintBills, after.CardFeePercent, after.BankFeePercent, after.RequirePaymentMethod, after.ChargeHandPrintFee, after.MailMode }
        });
        return await Dto(null, null, null, ct);
    }

    /// <summary>One group's own fee per card and monthly limit; null for either goes back to the site's.</summary>
    [HttpPut("groups/{id:int}")]
    public async Task<ActionResult<SystemBillingDto>> SetGroupTerms(int id, GroupBillingTermsRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null) return NotFound();
        if (req.FeeCents is < 0 or > 500) return BadRequest(new { message = "The fee per card is between $0.00 and $5.00." });
        if (req.LimitCents is < 0 or > 10_000_000) return BadRequest(new { message = "The monthly limit is between $0 (mailing off) and $100,000." });
        if (req.PayRule is not null && !PayRules.Contains(req.PayRule)) return BadRequest(new { message = "Choose whether this group needs a payment method before mailing." });
        var b = await db.GroupBilling.FirstOrDefaultAsync(x => x.GroupId == id, ct);
        if (b is null) db.GroupBilling.Add(b = new GroupBilling { GroupId = id });
        var before = new { b.FeeCents, b.MonthlyLimitCents, b.MailEnabled, b.RequirePayMethod };
        b.FeeCents = req.FeeCents;
        b.MonthlyLimitCents = req.LimitCents;
        // Switched on or off for all the group's restaurants; "each restaurant decides" is set with the group's settings.
        if (req.MailEnabled is bool on) { b.MailEnabled = on; b.MailPerRestaurant = false; }
        if (req.PayRule is not null) b.RequirePayMethod = req.PayRule switch { "required" => true, "notrequired" => false, _ => null };
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, "GroupBillingTermsChanged", "Group", id, new { Group = group.Name, before, after = new { b.FeeCents, b.MonthlyLimitCents, b.MailEnabled, b.RequirePayMethod } }, groupId: id);
        if (before.RequirePayMethod != b.RequirePayMethod)
            await audit.LogAsync(me, "GroupPaymentRuleChanged", "Group", id, new { Group = group.Name, before = before.RequirePayMethod, after = b.RequirePayMethod, SiteRule = site.Billing.RequirePaymentMethod }, groupId: id);
        if (before.MailEnabled != b.MailEnabled)
            await audit.LogAsync(me, b.MailEnabled ? "GroupMailingSwitchedOn" : "GroupMailingSwitchedOff", "Group", id, new { Group = group.Name }, groupId: id);
        return await Dto(null, null, null, ct);
    }

    /// <summary>
    /// Gives a group a credit: a check that arrived, a correction, a welcome bonus. The details are required and the
    /// line can never be edited or removed. The credit comes off the group's bills that are waiting, oldest first,
    /// and then off its future bills.
    /// </summary>
    [HttpPost("groups/{id:int}/credits")]
    public async Task<ActionResult<object>> AddCredit(int id, AddCreditRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null) return NotFound();
        var details = (req.Details ?? "").Trim();
        if (req.AmountCents <= 0) return BadRequest(new { message = "Enter how much the credit is for." });
        if (req.AmountCents > MaxCreditCents) return BadRequest(new { message = $"One credit can be up to {BillingService.Dollars(MaxCreditCents)}. Give a larger amount as more than one credit, each with its own details." });
        if (req.ConfirmCents != req.AmountCents) return BadRequest(new { message = "The amount confirmed does not match the amount entered. Nothing was recorded; check it and confirm again." });
        if (req.Kind is null || !CreditKinds.Given.Contains(req.Kind)) return BadRequest(new { message = "Choose what the credit is for." });
        if (details.Length < 5) return BadRequest(new { message = "Say what the credit is for, in a few words at least: the check number and date, what is being corrected, or why the bonus is given." });
        if (details.Length > 500) return BadRequest(new { message = "Keep the details to 500 characters." });
        if (string.IsNullOrWhiteSpace(req.Key) || req.Key.Length > 64) return BadRequest(new { message = "This page is out of date. Reload it and try again." });
        if (await db.GroupCredits.AsNoTracking().AnyAsync(c => c.Key == req.Key, ct))
            return new { message = "That credit was already recorded. Nothing was added twice.", billing = await Dto(null, null, null, ct) };

        var credit = new GroupCredit { GroupId = id, AmountCents = req.AmountCents, Kind = req.Kind, Details = details, CreatedBy = me.DisplayName, CreatedByUserId = me.Id, Key = req.Key };
        db.GroupCredits.Add(credit);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, "GroupCreditAdded", "GroupCredit", credit.Id, new { Group = group.Name, credit.AmountCents, credit.Kind, credit.Details }, groupId: id);
        var used = await billing.ApplyCreditAsync(id, me.DisplayName, ct);
        var balance = await billing.CreditBalanceAsync(id, ct);
        var message = $"{BillingService.Dollars(req.AmountCents)} credit recorded for {group.Name}."
                      + (used > 0 ? $" {BillingService.Dollars(used)} of it came straight off the bills that were waiting." : "")
                      + (balance > 0 ? $" {BillingService.Dollars(balance)} is left for its next bills." : " None is left over.");
        return new { message, billing = await Dto(null, null, null, ct) };
    }

    /// <summary>
    /// Takes back credit a group has not used yet (a credit entered by mistake, a check that bounced). It is a new
    /// line in the log with its own details; the original line stays. Credit already used on a bill is returned by
    /// cancelling that bill instead.
    /// </summary>
    [HttpPost("groups/{id:int}/credits/takeback")]
    public async Task<ActionResult<object>> TakeBackCredit(int id, TakeBackCreditRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null) return NotFound();
        var details = (req.Details ?? "").Trim();
        var balance = await billing.CreditBalanceAsync(id, ct);
        if (req.AmountCents <= 0) return BadRequest(new { message = "Enter how much to take back." });
        if (req.AmountCents > balance) return BadRequest(new { message = $"{group.Name} has {BillingService.Dollars(Math.Max(0, balance))} of unused credit, so that is the most that can be taken back." });
        if (details.Length < 5) return BadRequest(new { message = "Say why the credit is being taken back, in a few words at least." });
        if (details.Length > 500) return BadRequest(new { message = "Keep the details to 500 characters." });
        if (string.IsNullOrWhiteSpace(req.Key) || req.Key.Length > 64) return BadRequest(new { message = "This page is out of date. Reload it and try again." });
        if (await db.GroupCredits.AsNoTracking().AnyAsync(c => c.Key == req.Key, ct))
            return new { message = "That was already recorded. Nothing was taken back twice.", billing = await Dto(null, null, null, ct) };
        var line = new GroupCredit { GroupId = id, AmountCents = -req.AmountCents, Kind = CreditKinds.TakenBack, Details = details, CreatedBy = me.DisplayName, CreatedByUserId = me.Id, Key = req.Key };
        db.GroupCredits.Add(line);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, "GroupCreditTakenBack", "GroupCredit", line.Id, new { Group = group.Name, TakenBackCents = req.AmountCents, line.Details, LeftCents = balance - req.AmountCents }, groupId: id);
        return new { message = $"{BillingService.Dollars(req.AmountCents)} of credit taken back from {group.Name}. {BillingService.Dollars(balance - req.AmountCents)} is left.", billing = await Dto(null, null, null, ct) };
    }

    /// <summary>Creates and sends the bills for a finished month now, without waiting for the bill day.</summary>
    [HttpPost("issue")]
    public async Task<ActionResult<object>> Issue(IssueBillsRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        try
        {
            var created = await billing.IssueAsync(req.Month ?? "", me.DisplayName, ct);
            foreach (var invoice in created) await billing.DeliverAsync(invoice, me.DisplayName, again: false, ct);
            var message = created.Count == 0
                ? $"No new bills for {MailLedger.MonthName(req.Month ?? "")}: every group with mailed cards already has one, or nothing was mailed."
                : $"{created.Count} bill{(created.Count == 1 ? "" : "s")} created for {MailLedger.MonthName(req.Month!)}, {BillingService.Dollars(created.Sum(i => i.TotalCents))} in all.";
            return new { message, billing = await Dto(null, null, null, ct) };
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Records a payment received outside the site (a check, a bank transfer).</summary>
    [HttpPost("invoices/{id:int}/paid")]
    public async Task<ActionResult<SystemBillingDto>> MarkPaid(int id, MarkPaidRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        var invoice = await db.MailInvoices.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (invoice is null) return NotFound();
        if (invoice.Status == InvoiceStatuses.Processing) return BadRequest(new { message = "A bank payment for this bill is on its way. It will show as paid when it arrives." });
        if (invoice.Status != InvoiceStatuses.Open) return BadRequest(new { message = "This bill is not waiting for payment." });
        var how = PaidHow.Contains(req.How) ? req.How! : "other";
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (req.PaidOn is { } on && on > today) return BadRequest(new { message = "The payment date cannot be in the future." });
        invoice.Status = InvoiceStatuses.Paid;
        invoice.PaidAt = req.PaidOn is { } day ? day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc) : DateTime.UtcNow;
        invoice.PaidHow = how;
        invoice.PaidNote = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim().Length > 500 ? req.Note.Trim()[..500] : req.Note.Trim();
        invoice.PaidBy = me.DisplayName;
        invoice.PaymentError = null;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, "MailBillMarkedPaid", "MailInvoice", invoice.Id,
            new { invoice.Number, invoice.TotalCents, invoice.CreditCents, PaidCents = BillingService.Due(invoice), how, invoice.PaidNote, PaidOn = invoice.PaidAt }, groupId: invoice.GroupId);
        return await Dto(null, null, null, ct);
    }

    /// <summary>Puts back a bill that was marked paid by hand, or one that was cancelled.</summary>
    [HttpPost("invoices/{id:int}/unpaid")]
    public async Task<ActionResult<SystemBillingDto>> MarkUnpaid(int id, CancellationToken ct)
    {
        var me = await users.GetAsync();
        var invoice = await db.MailInvoices.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (invoice is null) return NotFound();
        if (invoice.Status is not (InvoiceStatuses.Paid or InvoiceStatuses.Void)) return BadRequest(new { message = "This bill is already waiting for payment." });
        if (invoice.Status == InvoiceStatuses.Paid && invoice.PaymentRef is not null)
            return BadRequest(new { message = "This bill was paid through Stripe. Refund it in Stripe if the money has to go back; it cannot be reopened here." });
        if (invoice.Status == InvoiceStatuses.Paid && invoice.PaidHow == BillingService.PaidWithCredit)
            return BadRequest(new { message = "This bill was paid with the group's credit, so there is no payment to undo." });
        var was = invoice.Status;
        invoice.Status = InvoiceStatuses.Open;
        invoice.PaidAt = null; invoice.PaidHow = null; invoice.PaidNote = null; invoice.PaidBy = null;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, "MailBillReopened", "MailInvoice", invoice.Id, new { invoice.Number, was }, groupId: invoice.GroupId);
        // A bill that is waiting again takes whatever credit the group has, like any other.
        await billing.ApplyCreditAsync(invoice.GroupId, me.DisplayName, ct);
        return await Dto(null, null, null, ct);
    }

    /// <summary>Cancels a bill that should not be collected. The group no longer sees it.</summary>
    [HttpPost("invoices/{id:int}/void")]
    public async Task<ActionResult<SystemBillingDto>> Void(int id, CancellationToken ct)
    {
        var me = await users.GetAsync();
        var invoice = await db.MailInvoices.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (invoice is null) return NotFound();
        if (invoice.Status != InvoiceStatuses.Open) return BadRequest(new { message = "Only a bill that is waiting for payment can be cancelled." });
        invoice.Status = InvoiceStatuses.Void;
        // Credit that had been used on the bill goes back to the group.
        var returned = invoice.CreditCents;
        if (returned > 0)
        {
            db.GroupCredits.Add(new GroupCredit { GroupId = invoice.GroupId, AmountCents = returned, Kind = CreditKinds.Returned, Details = $"Bill {invoice.Number} was cancelled", InvoiceId = invoice.Id, CreatedBy = me.DisplayName, CreatedByUserId = me.Id });
            invoice.CreditCents = 0;
        }
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, "MailBillCancelled", "MailInvoice", invoice.Id, new { invoice.Number, invoice.TotalCents, CreditReturnedCents = returned }, groupId: invoice.GroupId);
        return await Dto(null, null, null, ct);
    }

    /// <summary>Emails the bill again (and posts a printed copy, when that is on and none has gone yet).</summary>
    [HttpPost("invoices/{id:int}/send")]
    public async Task<ActionResult<object>> Send(int id, CancellationToken ct)
    {
        var me = await users.GetAsync();
        var invoice = await db.MailInvoices.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (invoice is null) return NotFound();
        if (invoice.Status == InvoiceStatuses.Void) return BadRequest(new { message = "This bill was cancelled." });
        await billing.DeliverAsync(invoice, me.DisplayName, again: true, ct);
        return new
        {
            ok = invoice.DeliveryError is null,
            message = invoice.DeliveryError ?? $"Bill {invoice.Number} was emailed to {invoice.EmailedTo}.",
            billing = await Dto(null, null, null, ct)
        };
    }

    /// <summary>
    /// One month's mailed cards by investment group and restaurant, split into what the mail house cost and the site's
    /// fee. Filters by investment group and restaurant.
    /// </summary>
    /// <summary>
    /// What Stripe has told the site, newest first: every notification its webhook has received.
    /// </summary>
    /// <param name="groupId">Only notifications about this investment group's bills.</param>
    /// <param name="kind">all, bills (about a bill on this site) or look (refunds and disputes).</param>
    [HttpGet("notifications")]
    public async Task<List<PaymentNotificationDto>> Notifications(int? groupId, string? kind, int take = 100, CancellationToken ct = default)
    {
        var q = db.StripeEvents.AsNoTracking().AsQueryable();
        if (groupId is not null) q = q.Where(e => e.GroupId == groupId);
        if (kind == "bills") q = q.Where(e => e.InvoiceId != null);
        if (kind == "look") q = q.Where(e => e.Type == "charge.refunded" || e.Type == "charge.dispute.created");
        var rows = await q.OrderByDescending(e => e.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        return await ToDtos(rows, ct);
    }

    /// <summary>One notification with every data point in it. Read by System Administrators only; reading it is audited.</summary>
    [HttpGet("notifications/{id:int}")]
    public async Task<ActionResult<PaymentNotificationDetail>> Notification(int id, CancellationToken ct)
    {
        var me = await users.GetAsync();
        var row = await db.StripeEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (row is null) return NotFound();
        await audit.LogAsync(me, "PaymentNotificationRead", "StripeEvent", row.Id, new { row.Type, row.EventId }, groupId: row.GroupId);
        return new PaymentNotificationDetail((await ToDtos([row], ct))[0], PaymentNotifications.DataPoints(row.Payload));
    }

    private async Task<List<PaymentNotificationDto>> ToDtos(List<StripeEvent> rows, CancellationToken ct)
    {
        var invoiceIds = rows.Where(r => r.InvoiceId != null).Select(r => r.InvoiceId!.Value).Distinct().ToList();
        var bills = await db.MailInvoices.AsNoTracking().Where(i => invoiceIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Number, ct);
        var groupIds = rows.Where(r => r.GroupId != null).Select(r => r.GroupId!.Value).Distinct().ToList();
        var groups = await db.Groups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, ct);
        return rows.Select(e => new PaymentNotificationDto(e.Id, e.EventId, e.Type, PaymentNotifications.Label(e.Type), DateTime.SpecifyKind(e.ReceivedAt, DateTimeKind.Utc),
            e.CreatedAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null, !e.LiveMode, e.Deliveries, e.ObjectType, e.ObjectId, e.PaymentId, e.InvoiceId,
            e.InvoiceId is int i ? bills.GetValueOrDefault(i) : null, e.GroupId, e.GroupId is int g ? groups.GetValueOrDefault(g) : null, e.AmountCents, e.Currency, e.Status, e.Reason, e.Outcome,
            PaymentNotifications.NeedsALook(e.Type))).ToList();
    }

    [HttpGet("statement")]
    public async Task<SiteMailStatement> Statement(string? month, int? groupId, int? restaurantId, CancellationToken ct)
    {
        // Cards mailed for groups, and cards they printed themselves where the fee is charged for that.
        var all = await ledger.ChargesAsync(null, ct);
        var months = all.Select(c => MailLedger.MonthKey(c.At)).Append(MailLedger.MonthKey(DateTime.UtcNow)).Distinct().OrderByDescending(m => m).ToList();
        var chosen = MailLedger.Range(month) is null ? months[0] : month!;
        var (from, to) = MailLedger.Range(chosen)!.Value;
        var cards = all.Where(c => c.At >= from && c.At < to && (groupId is null || c.GroupId == groupId) && (restaurantId is null || c.RestaurantId == restaurantId)).ToList();
        var rows = cards.GroupBy(c => new { c.GroupId, c.Group, c.RestaurantId, c.Restaurant }).OrderBy(x => x.Key.Group).ThenBy(x => x.Key.Restaurant)
            .Select(x => new SiteMailStatementRow(x.Key.GroupId, x.Key.Group, x.Key.RestaurantId, x.Key.Restaurant, x.Count(), x.Sum(c => c.CostCents), x.Sum(c => c.FeeCents), x.Sum(c => c.Cents))
            {
                PrintedCards = x.Count(c => c.Printed)
            }).ToList();
        return new SiteMailStatement(chosen, months, rows, rows.Sum(x => x.Cards), rows.Sum(x => x.CostCents), rows.Sum(x => x.FeeCents), rows.Sum(x => x.TotalCents));
    }

    private async Task<SystemBillingDto> Dto(int? groupId, string? status, string? month, CancellationToken ct)
    {
        var s = site.Billing;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthStart = MailLedger.MonthStart(DateTime.UtcNow);
        var groups = await db.Groups.AsNoTracking().OrderBy(g => g.Name).Select(g => new { g.Id, g.Name, g.IsActive }).ToListAsync(ct);
        var names = groups.ToDictionary(g => g.Id, g => g.Name);
        var rows = await db.GroupBilling.AsNoTracking().ToListAsync(ct);
        var billed = (await ledger.ChargesAsync(null, ct)).Select(c => new { c.GroupId, c.At, c.Cents }).ToList();
        var allInvoices = await db.MailInvoices.AsNoTracking().OrderByDescending(i => i.Month).ThenBy(i => i.GroupId).ToListAsync(ct);
        bool Overdue(MailInvoice i) => i.Status == InvoiceStatuses.Open && i.DueOn < today;
        var credits = await db.GroupCredits.AsNoTracking().ToListAsync(ct);
        var balances = credits.GroupBy(c => c.GroupId).ToDictionary(g => g.Key, g => g.Sum(c => c.AmountCents));
        var numbers = allInvoices.ToDictionary(i => i.Id, i => i.Number);

        var mailOnSomewhere = (await db.Restaurants.AsNoTracking().Where(r => r.IsActive && r.MailOn).Select(r => r.GroupId).Distinct().ToListAsync(ct)).ToHashSet();
        var groupRows = groups.Select(g =>
        {
            var b = rows.FirstOrDefault(x => x.GroupId == g.Id);
            var now = billed.Where(c => c.GroupId == g.Id && c.At >= monthStart).ToList();
            return new GroupBillingRow(g.Id, g.Name, g.IsActive, b?.FeeCents, b?.FeeCents ?? s.FeeCents, b?.MonthlyLimitCents, b?.MonthlyLimitCents ?? s.MonthlyLimitCents, now.Count, now.Sum(c => c.Cents),
                allInvoices.Where(i => i.GroupId == g.Id && i.Status == InvoiceStatuses.Open).Sum(BillingService.Due), b?.PayLabel, b?.AutoPay ?? false, b?.BillingEmail)
            {
                MailEnabled = GroupPolicy.Switch(b) == MailChoices.On || (GroupPolicy.Switch(b) == MailChoices.Each && mailOnSomewhere.Contains(g.Id)),
                Mail = GroupPolicy.Switch(b), MailMode = GroupPolicy.SiteMode(s) != MailChoices.Each ? GroupPolicy.SiteMode(s) : GroupPolicy.Mode(b), PayRequired = b?.RequirePayMethod ?? s.RequirePaymentMethod, OwnPayRule = b?.RequirePayMethod,
                CreditCents = balances.GetValueOrDefault(g.Id)
            };
        }).ToList();

        var shown = allInvoices.Where(i => (groupId is null || i.GroupId == groupId) && (MailLedger.Range(month) is null || i.Month == month) && status switch
        {
            null or "" or "all" => true,
            "overdue" => Overdue(i),
            _ => i.Status == status
        }).Take(300).Select(i => new SystemInvoiceDto(i.Id, i.GroupId, names.GetValueOrDefault(i.GroupId, "(removed group)"), i.Number, i.Month, MailLedger.MonthName(i.Month), i.Cards, i.CostCents,
            i.FeeCents, i.TotalCents, DateTime.SpecifyKind(i.IssuedAt, DateTimeKind.Utc), i.DueOn, i.Status, Overdue(i), i.PaidAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null, i.PaidHow,
            i.PaidNote, i.PaidBy, i.SurchargeCents, i.PaymentRef, i.PaymentError, i.EmailedAt is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : null, i.EmailedTo,
            i.PrintedAt is { } p ? DateTime.SpecifyKind(p, DateTimeKind.Utc) : null, i.DeliveryError, BillingService.Lines(i)) { CreditCents = i.CreditCents, DueCents = BillingService.Due(i), For = i.UnitName }).ToList();
        // What Stripe has said about each of the bills shown.
        var shownIds = shown.Select(i => i.Id).ToList();
        var said = (await ToDtos(await db.StripeEvents.AsNoTracking().Where(e => e.InvoiceId != null && shownIds.Contains(e.InvoiceId.Value)).OrderByDescending(e => e.Id).ToListAsync(ct), ct))
            .ToLookup(n => n.InvoiceId!.Value);
        shown = shown.Select(i => i with { Notifications = said[i.Id].ToList() }).ToList();

        var have = allInvoices.Select(i => (i.GroupId, i.Month)).ToHashSet();
        var unbilled = billed.Where(c => c.At < monthStart && c.Cents > 0).Select(c => (c.GroupId, Month: MailLedger.MonthKey(c.At))).Distinct()
            .Where(x => !have.Contains(x)).Select(x => x.Month).Distinct().OrderBy(m => m).ToList();
        var open = allInvoices.Where(i => i.Status == InvoiceStatuses.Open).ToList();
        return new SystemBillingDto(s, site.Any is not null, site.Live is null, payAccount.Current is not null, payAccount.Current?.Test ?? false, email is not LoggingEmailSender,
            groupRows, shown, unbilled, open.Sum(BillingService.Due), open.Where(Overdue).Sum(BillingService.Due))
        {
            Credits = credits.Where(c => groupId is null || c.GroupId == groupId).OrderByDescending(c => c.Id).Take(300)
                .Select(c => new GroupCreditDto(c.Id, c.GroupId, names.GetValueOrDefault(c.GroupId, "(removed group)"), DateTime.SpecifyKind(c.CreatedAt, DateTimeKind.Utc), c.Kind, CreditKinds.Label(c.Kind),
                    c.AmountCents, c.Details, c.CreatedBy, c.InvoiceId is int n ? numbers.GetValueOrDefault(n) : null)).ToList(),
            CreditCents = balances.Values.Where(v => v > 0).Sum(),
            MaxCreditCents = MaxCreditCents
        };
    }
}
