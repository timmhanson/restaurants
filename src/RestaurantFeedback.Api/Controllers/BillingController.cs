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
/// What an investment group sees of its billing for mailed cards: this month's use against its limit, each month by
/// restaurant, its bills, and the payment method it pays them with. Owners see the use of their own restaurants;
/// only the group's administrators see the bills, save a payment method or pay.
/// </summary>
[ApiController]
[Route("api/billing")]
[Authorize(Roles = "Owner,GroupAdmin,SuperAdmin")]
public class BillingController(AppDbContext db, AuditService audit, CurrentUserProvider users, SiteMailing site, MailLedger ledger, BillingService billing,
    IPaymentProvider payments, PaymentAccountStore payAccount, PostGridOptions mailOptions) : ControllerBase
{
    private const string AdminsOnly = "Only the investment group's administrators can change billing or pay.";

    [HttpGet]
    public async Task<ActionResult<GroupBillingDto>> Get(CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is not int groupId) return BadRequest(new { message = "Select an investment group first." });
        return await Dto(me, groupId, ct);
    }

    /// <summary>Where the group's bills are sent.</summary>
    [HttpPut("contact")]
    public async Task<ActionResult<GroupBillingDto>> SetContact(BillingContact req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is not int groupId) return BadRequest(new { message = "Select an investment group first." });
        if (!me.IsGroupWide) return StatusCode(403, new { message = AdminsOnly });
        string? Clean(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? s.Trim()[..max] : s.Trim();
        var emailAddress = Clean(req.Email, 200);
        if (emailAddress is not null && (!emailAddress.Contains('@') || emailAddress.Any(char.IsWhiteSpace)))
            return BadRequest(new { message = "Enter one email address for bills, or leave it empty to send them to the group's administrators." });
        var b = await Row(groupId, ct);
        b.BillingName = Clean(req.Name, 200);
        b.BillingEmail = emailAddress;
        b.Street = Clean(req.Street, 200); b.City = Clean(req.City, 100); b.State = Clean(req.State, 20); b.Zip = Clean(req.Zip, 20);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, "BillingContactChanged", "Group", groupId, new { b.BillingName, b.BillingEmail, b.Street, b.City, b.State, b.Zip });
        return await Dto(me, groupId, ct);
    }

    /// <summary>
    /// Starts saving a payment method: returns the payment service's own page, where the bank account or card is
    /// entered. Nothing typed there ever reaches this site.
    /// </summary>
    [HttpPost("payment-method/start")]
    public async Task<ActionResult<PaySetupStart>> StartPaymentMethod(CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is not int groupId) return BadRequest(new { message = "Select an investment group first." });
        if (!me.IsGroupWide) return StatusCode(403, new { message = AdminsOnly });
        if (payAccount.Current is not { } account) return BadRequest(new { message = "Paying on the site is not set up yet. Ask the site's administrator." });
        try
        {
            var b = await Row(groupId, ct);
            if (b.PayCustomerId is null)
            {
                var name = await db.Groups.Where(g => g.Id == groupId).Select(g => g.Name).FirstAsync(ct);
                b.PayCustomerId = await payments.CreateCustomerAsync(account.SecretKey, b.BillingName ?? name, b.BillingEmail, groupId, ct);
                await db.SaveChangesAsync(ct);
            }
            var origin = SiteOrigin();
            var link = await payments.StartSetupAsync(account.SecretKey, b.PayCustomerId, $"{origin}/billing?saved=1", $"{origin}/billing?saved=0", ct);
            await audit.LogAsync(me, "PaymentMethodSetupStarted", "Group", groupId);
            return new PaySetupStart(link.Url);
        }
        catch (PaymentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Back from the payment service's page: reads what was saved there and keeps its reference and label.</summary>
    [HttpPost("payment-method/finish")]
    public async Task<ActionResult<BillingActionResult>> FinishPaymentMethod(PaySetupFinish req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is not int groupId) return BadRequest(new { message = "Select an investment group first." });
        if (!me.IsGroupWide) return StatusCode(403, new { message = AdminsOnly });
        if (payAccount.Current is not { } account) return BadRequest(new { message = "Paying on the site is not set up yet." });
        var b = await Row(groupId, ct);
        if (b.PayCustomerId is null || string.IsNullOrWhiteSpace(req.SessionId)) return BadRequest(new { message = "Start again from Add a payment method." });
        try
        {
            var saved = await payments.FinishSetupAsync(account.SecretKey, req.SessionId.Trim(), b.PayCustomerId, ct);
            if (saved is null) return new BillingActionResult(false, "No payment method was saved. Nothing has changed.", await Dto(me, groupId, ct));
            var old = b.PayMethodId;
            b.PayMethodId = saved.MethodId; b.PayKind = saved.Kind; b.PayFunding = saved.Funding; b.PayLabel = saved.Label; b.PayMandateId = saved.MandateId;
            b.PaySetAt = DateTime.UtcNow; b.PaySetBy = me.DisplayName;
            await db.SaveChangesAsync(ct);
            if (old is not null && old != saved.MethodId)
                try { await payments.RemoveMethodAsync(account.SecretKey, old, ct); } catch (PaymentException) { }
            await audit.LogAsync(me, "PaymentMethodSaved", "Group", groupId, new { saved.Label, saved.Kind, saved.Funding, Test = account.Test });
            var percent = ledger.PayFeePercent(b);
            var fee = percent > 0 ? $" Bills paid with it carry a {percent:0.##}% {(saved.Kind == PayKinds.Bank ? "bank account" : "credit card")} fee." : "";
            return new BillingActionResult(true, $"Saved: {saved.Label}.{fee}", await Dto(me, groupId, ct));
        }
        catch (PaymentException ex)
        {
            return new BillingActionResult(false, ex.Message, await Dto(me, groupId, ct));
        }
    }

    [HttpDelete("payment-method")]
    public async Task<ActionResult<GroupBillingDto>> RemovePaymentMethod(CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is not int groupId) return BadRequest(new { message = "Select an investment group first." });
        if (!me.IsGroupWide) return StatusCode(403, new { message = AdminsOnly });
        var b = await Row(groupId, ct);
        if (b.PayMethodId is not null)
        {
            if (await db.MailInvoices.AnyAsync(i => i.GroupId == groupId && i.Status == InvoiceStatuses.Processing, ct))
                return BadRequest(new { message = "A payment from this method is still on its way. Remove it once that bill shows as paid." });
            var label = b.PayLabel;
            if (payAccount.Current is { } account)
                try { await payments.RemoveMethodAsync(account.SecretKey, b.PayMethodId, ct); } catch (PaymentException) { }
            b.PayMethodId = null; b.PayKind = null; b.PayFunding = null; b.PayLabel = null; b.PayMandateId = null; b.PaySetAt = null; b.PaySetBy = null; b.AutoPay = false;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync(me, "PaymentMethodRemoved", "Group", groupId, new { Label = label });
        }
        return await Dto(me, groupId, ct);
    }

    /// <summary>Whether each new bill is charged to the saved payment method without anyone pressing Pay.</summary>
    [HttpPut("auto-pay")]
    public async Task<ActionResult<GroupBillingDto>> SetAutoPay(AutoPayRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is not int groupId) return BadRequest(new { message = "Select an investment group first." });
        if (!me.IsGroupWide) return StatusCode(403, new { message = AdminsOnly });
        var b = await Row(groupId, ct);
        if (req.On && b.PayMethodId is null) return BadRequest(new { message = "Add a payment method first." });
        b.AutoPay = req.On;
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(me, "AutoPayChanged", "Group", groupId, new { req.On, With = b.PayLabel });
        return await Dto(me, groupId, ct);
    }

    /// <summary>
    /// Pays one bill with the saved payment method. The person confirms the exact amount (the bill, plus the fee
    /// for a credit card); a different amount is refused, and a bill can only be charged once.
    /// </summary>
    [HttpPost("invoices/{id:int}/pay")]
    public async Task<ActionResult<BillingActionResult>> Pay(int id, PayInvoiceRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (me.GroupId is not int groupId) return BadRequest(new { message = "Select an investment group first." });
        if (!me.IsGroupWide) return StatusCode(403, new { message = AdminsOnly });
        var invoice = await db.MailInvoices.FirstOrDefaultAsync(i => i.Id == id && i.GroupId == groupId, ct);
        if (invoice is null) return NotFound();
        var b = await db.GroupBilling.AsNoTracking().FirstOrDefaultAsync(x => x.GroupId == groupId, ct);
        var due = BillingService.Due(invoice);
        var amount = due + ledger.PayFee(due, b);
        if (req.ConfirmCents != amount)
            return BadRequest(new { message = $"The amount to pay is now {BillingService.Dollars(amount)}. Nothing was charged; review it and confirm again." });
        var (ok, message) = await billing.ChargeAsync(invoice, me.DisplayName, me.Id, me.Role, ct);
        return new BillingActionResult(ok, message, await Dto(me, groupId, ct));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private async Task<GroupBilling> Row(int groupId, CancellationToken ct)
    {
        var b = await db.GroupBilling.FirstOrDefaultAsync(x => x.GroupId == groupId, ct);
        if (b is null) db.GroupBilling.Add(b = new GroupBilling { GroupId = groupId });
        return b;
    }

    /// <summary>The address people use for this site, for the way back from the payment service's page.</summary>
    private string SiteOrigin()
    {
        if (!string.IsNullOrWhiteSpace(site.Billing.SiteUrl)) return site.Billing.SiteUrl;
        var local = Request.Host.Host is "localhost" or "127.0.0.1" or "[::1]";
        // Locally the page may be served by the dev server on another port, which the browser names in Origin.
        if (local && Uri.TryCreate(Request.Headers.Origin.ToString(), UriKind.Absolute, out var o) && o.Host is "localhost" or "127.0.0.1") return o.GetLeftPart(UriPartial.Authority);
        if (!string.IsNullOrWhiteSpace(mailOptions.PublicBaseUrl)) return mailOptions.PublicBaseUrl.TrimEnd('/');
        return $"{(local ? Request.Scheme : "https")}://{Request.Host}";
    }

    private async Task<GroupBillingDto> Dto(CurrentUser me, int groupId, CancellationToken ct)
    {
        var s = site.Billing;
        var terms = await ledger.TermsAsync(groupId, ct);
        var b = await db.GroupBilling.AsNoTracking().FirstOrDefaultAsync(x => x.GroupId == groupId, ct);
        var manage = me.IsGroupWide;
        var mine = me.RestaurantIds.ToList();
        var since = MailLedger.MonthStart(DateTime.UtcNow).AddMonths(-11);
        // Cards mailed for the group, and cards its restaurants printed themselves where that is charged for.
        var charges = (await ledger.ChargesAsync(groupId, ct)).Where(c => mine.Contains(c.RestaurantId) && c.At >= since).ToList();
        var cards = charges.Select(c => new { c.At, c.RestaurantId, c.Restaurant, c.Cents }).ToList();
        var months = cards.GroupBy(c => MailLedger.MonthKey(c.At)).OrderByDescending(g => g.Key)
            .Select(g => new MailMonth(g.Key, MailLedger.MonthName(g.Key), g.Count(), g.Sum(c => c.Cents),
                g.GroupBy(c => new { c.RestaurantId, c.Restaurant }).OrderBy(r => r.Key.Restaurant)
                    .Select(r => new MailMonthRow(r.Key.RestaurantId, r.Key.Restaurant, r.Count(), r.Sum(c => c.Cents))).ToList())).ToList();
        var thisMonth = MailLedger.MonthKey(DateTime.UtcNow);
        var current = months.FirstOrDefault(m => m.Month == thisMonth);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var invoices = manage
            ? (await db.MailInvoices.AsNoTracking().Where(i => i.GroupId == groupId && i.Status != InvoiceStatuses.Void).OrderByDescending(i => i.Month).Take(36).ToListAsync(ct))
                .Select(i => new InvoiceDto(i.Id, i.Number, i.Month, MailLedger.MonthName(i.Month), i.Cards, i.TotalCents, DateTime.SpecifyKind(i.IssuedAt, DateTimeKind.Utc), i.DueOn, i.Status,
                    i.Status == InvoiceStatuses.Open && i.DueOn < today, i.PaidAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null, i.PaidHow, i.SurchargeCents,
                    ledger.PayFee(BillingService.Due(i), b), i.PaymentError, BillingService.Lines(i)) { CreditCents = i.CreditCents, DueCents = BillingService.Due(i) }).ToList()
            : [];
        // The group's credit account, without the System Administrator's notes.
        var creditRows = manage ? await db.GroupCredits.AsNoTracking().Where(c => c.GroupId == groupId).OrderByDescending(c => c.Id).Take(100).ToListAsync(ct) : [];
        var billNumbers = manage ? await db.MailInvoices.AsNoTracking().Where(i => i.GroupId == groupId).ToDictionaryAsync(i => i.Id, i => i.Number, ct) : [];
        var creditBalance = manage ? await billing.CreditBalanceAsync(groupId, ct) : 0;
        var method = manage && b?.PayMethodId is not null
            ? new PayMethodDto(b.PayKind ?? PayKinds.Card, b.PayFunding, b.PayLabel ?? "Saved payment method", b.PaySetAt is { } set ? DateTime.SpecifyKind(set, DateTimeKind.Utc) : null, b.PaySetBy)
            : null;
        var account = site.Account;
        return new GroupBillingDto(manage, account is not null && terms.Enabled && terms.LimitCents > 0, account?.Test ?? false, terms.PriceCents, terms.LimitCents,
            // The monthly limit is about cards mailed for the group, so what counts against it leaves out the cards it printed itself.
            charges.Where(c => !c.Printed && MailLedger.MonthKey(c.At) == thisMonth).Sum(c => c.Cents), current?.Cards ?? 0, payAccount.Current is not null, terms.PayRequired, payAccount.Current?.Test ?? false, s.CardFeePercent, s.BankFeePercent, method, b?.AutoPay ?? false,
            manage ? new BillingContact(b?.BillingName, b?.BillingEmail, b?.Street, b?.City, b?.State, b?.Zip) : new BillingContact(null, null, null, null, null, null),
            s.BusinessName, s.BusinessEmail, s.PayInstructions, s.BillDay, s.DueDays, months, invoices,
            invoices.Where(i => i.Status == InvoiceStatuses.Open).Sum(i => i.DueCents))
        {
            CreditCents = creditBalance,
            PrintFeeCents = terms.PrintFeeCents,
            Credits = creditRows.Select(c => new GroupCreditLine(DateTime.SpecifyKind(c.CreatedAt, DateTimeKind.Utc), c.Kind, CreditKinds.Label(c.Kind), c.AmountCents,
                c.InvoiceId is int id ? billNumbers.GetValueOrDefault(id) : null)).ToList()
        };
    }
}
