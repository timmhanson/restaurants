using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Services;

/// <summary>
/// How the site bills investment groups for the cards it mails for them, set by the System Administrator on Billing.
/// The site pays the mail house first and bills each group monthly: the mail house's cost plus the site's fee per card.
/// </summary>
public class BillingSettings
{
    /// <summary>What the mail house charges the site for one card, in cents.</summary>
    public int CostCents { get; set; } = 91;
    /// <summary>The site's own fee per card, in cents, unless a group has its own.</summary>
    public int FeeCents { get; set; } = 5;
    /// <summary>The most a group may be billed in a calendar month unless it has its own limit; 0 switches mailing off.</summary>
    public int MonthlyLimitCents { get; set; } = 5000;
    /// <summary>The day of the month (1 to 28) on which last month's bills are created and sent.</summary>
    public int BillDay { get; set; } = 1;
    /// <summary>Days from the bill's date until it is due.</summary>
    public int DueDays { get; set; } = 15;
    public bool EmailBills { get; set; } = true;
    /// <summary>Also post a printed statement through the mail house (the site pays for each one).</summary>
    public bool PrintBills { get; set; }
    /// <summary>Added to a bill when it is paid by credit card (not debit cards).</summary>
    public decimal CardFeePercent { get; set; } = 3m;
    /// <summary>Added to a bill when it is paid from a connected bank account.</summary>
    public decimal BankFeePercent { get; set; } = 0.08m;
    /// <summary>
    /// A group must have a payment method saved before any of its cards is sent to the mail house (test orders
    /// included). On unless the System Administrator switches it off, so the site never pays for cards it has no
    /// way to collect on.
    /// </summary>
    public bool RequirePaymentMethod { get; set; } = true;
    /// <summary>
    /// A group is also charged the site's fee per card (not the mail house's cost) for each card it prints itself
    /// for a stamp by hand. Charged once per card, at its first print. On unless the System Administrator switches it off.
    /// </summary>
    public bool ChargeHandPrintFee { get; set; } = true;
    /// <summary>Who the bills are from.</summary>
    public string BusinessName { get; set; } = "Guest Feedback";
    public string? BusinessEmail { get; set; }
    public string? Street { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip { get; set; }
    /// <summary>Printed on every bill, such as how to pay by check.</summary>
    public string? PayInstructions { get; set; }
    /// <summary>The site's public address, for the link in emailed bills.</summary>
    public string? SiteUrl { get; set; }

    public BillingSettings Normalize()
    {
        CostCents = Math.Clamp(CostCents, 1, 1000);
        FeeCents = Math.Clamp(FeeCents, 0, 500);
        MonthlyLimitCents = Math.Clamp(MonthlyLimitCents, 0, 10_000_000);
        BillDay = Math.Clamp(BillDay, 1, 28);
        DueDays = Math.Clamp(DueDays, 0, 90);
        CardFeePercent = Math.Clamp(Math.Round(CardFeePercent, 2), 0m, 10m);
        BankFeePercent = Math.Clamp(Math.Round(BankFeePercent, 2), 0m, 10m);
        BusinessName = string.IsNullOrWhiteSpace(BusinessName) ? "Guest Feedback" : Short(BusinessName, 120)!;
        BusinessEmail = Short(BusinessEmail, 200);
        Street = Short(Street, 200); City = Short(City, 100); State = Short(State, 20); Zip = Short(Zip, 20);
        PayInstructions = Short(PayInstructions, 600);
        SiteUrl = Uri.TryCreate(SiteUrl?.Trim(), UriKind.Absolute, out var u) && u.Scheme is "https" or "http" ? u.GetLeftPart(UriPartial.Authority) : null;
        return this;
    }

    public bool HasAddress => !new[] { Street, City, State, Zip }.Any(string.IsNullOrWhiteSpace);

    private static string? Short(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim() is { } t && t.Length > max ? t[..max] : s.Trim();
}

/// <summary>What applies to one group right now: its price per card, its monthly limit and whether it may mail.</summary>
public record GroupTerms(int CostCents, int FeeCents, int LimitCents, bool HasPayMethod, bool PayRequired)
{
    /// <summary>What the group is billed for one card.</summary>
    public int PriceCents => CostCents + FeeCents;
    /// <summary>The System Administrator has switched mailing on for this group.</summary>
    public bool Enabled { get; init; }
    /// <summary>What the group is billed for a card it prints itself for a stamp by hand: its fee per card, or 0 when that is not charged.</summary>
    public int PrintFeeCents { get; init; }
    /// <summary>
    /// The group's credit that is free to spend on more cards: what it has, less the cards already mailed that are
    /// not on a bill yet (the credit will be used on those when their bill is made).
    /// </summary>
    public int CreditCents { get; init; }
    /// <summary>
    /// A payment method is required and the group has none, so it mails on its credit: only as many cards as the
    /// credit covers.
    /// </summary>
    public bool OnCredit => PayRequired && !HasPayMethod;
    /// <summary>The group cannot mail until it saves a payment method: one is required, it has none, and its credit does not cover even one card.</summary>
    public bool NeedsPayMethod => OnCredit && CreditCents < PriceCents;
    /// <summary>How many cards the credit covers, when the group mails on its credit.</summary>
    public int CardsOnCredit => PriceCents > 0 ? CreditCents / PriceCents : 0;
}

public record InvoiceLine(int RestaurantId, string Restaurant, int Cards, int Cents)
{
    /// <summary>Cards the restaurant printed itself for a stamp by hand (the site's fee only), not cards mailed for it.</summary>
    public bool Printed { get; init; }
}

/// <summary>
/// One thing a group is billed for: a card mailed for it (the mail house's cost plus the site's fee), or a card it
/// printed itself for a stamp by hand (the site's fee only).
/// </summary>
public record Charge(int GroupId, string Group, int RestaurantId, string Restaurant, DateTime At, int CostCents, int FeeCents, bool Printed)
{
    public int Cents => CostCents + FeeCents;
}

/// <summary>
/// What each investment group has been billed for mailed cards. A card counts in the calendar month (UTC) it was
/// sent, at the cost and fee recorded on it then; test orders and cancelled cards are never billed.
/// </summary>
public class MailLedger(AppDbContext db, SiteMailing site)
{
    public static DateTime MonthStart(DateTime utc) => new(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    public static string MonthKey(DateTime utc) => utc.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>The first moment of a yyyy-MM month and of the next, or null when it is not a month.</summary>
    public static (DateTime From, DateTime To)? Range(string? month) =>
        DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? (MonthStart(d), MonthStart(d).AddMonths(1)) : null;

    public static string MonthName(string month) => Range(month) is { } r ? r.From.ToString("MMMM yyyy", CultureInfo.InvariantCulture) : month;

    /// <summary>Cards that are billed: sent for real and not cancelled.</summary>
    public IQueryable<GuestComment> Billed() =>
        db.Comments.AsNoTracking().Where(c => c.MailId != null && !c.MailTest && c.MailStatus != MailStatuses.Cancelled && c.MailedAt != null);

    public async Task<(int Cards, int Cents)> ThisMonthAsync(int groupId, CancellationToken ct = default)
    {
        var from = MonthStart(DateTime.UtcNow);
        var rows = await Billed().Where(c => c.Restaurant.GroupId == groupId && c.MailedAt >= from)
            .Select(c => new { Cost = c.MailEstimateCents ?? 0, Fee = c.MailFeeCents ?? 0 }).ToListAsync(ct);
        return (rows.Count, rows.Sum(r => r.Cost + r.Fee));
    }

    public async Task<GroupTerms> TermsAsync(int groupId, CancellationToken ct = default)
    {
        var s = site.Billing;
        var b = await db.GroupBilling.AsNoTracking().FirstOrDefaultAsync(x => x.GroupId == groupId, ct);
        return new GroupTerms(s.CostCents, b?.FeeCents ?? s.FeeCents, b?.MonthlyLimitCents ?? s.MonthlyLimitCents, b?.PayMethodId is not null, b?.RequirePayMethod ?? s.RequirePaymentMethod)
        {
            Enabled = b?.MailEnabled ?? false, CreditCents = await SpendableCreditAsync(groupId, ct),
            PrintFeeCents = s.ChargeHandPrintFee ? b?.FeeCents ?? s.FeeCents : 0
        };
    }

    /// <summary>
    /// Everything billed to groups: cards mailed for them, and cards they printed themselves where the site's fee
    /// is charged for that. For one group, or every group.
    /// </summary>
    public async Task<List<Charge>> ChargesAsync(int? groupId = null, CancellationToken ct = default)
    {
        var mailed = Billed();
        var printed = db.Comments.AsNoTracking().Where(c => c.PrintFeeCents != null && c.PrintFeeCents > 0 && c.PrintFeeAt != null);
        if (groupId is int g)
        {
            mailed = mailed.Where(c => c.Restaurant.GroupId == g);
            printed = printed.Where(c => c.Restaurant.GroupId == g);
        }
        var a = await mailed.Select(c => new { c.Restaurant.GroupId, Group = c.Restaurant.Group.Name, c.RestaurantId, Restaurant = c.Restaurant.Name, At = c.MailedAt!.Value, Cost = c.MailEstimateCents ?? 0, Fee = c.MailFeeCents ?? 0 }).ToListAsync(ct);
        var b = await printed.Select(c => new { c.Restaurant.GroupId, Group = c.Restaurant.Group.Name, c.RestaurantId, Restaurant = c.Restaurant.Name, At = c.PrintFeeAt!.Value, Fee = c.PrintFeeCents!.Value }).ToListAsync(ct);
        return [.. a.Select(c => new Charge(c.GroupId, c.Group, c.RestaurantId, c.Restaurant, c.At, c.Cost, c.Fee, false)),
                .. b.Select(c => new Charge(c.GroupId, c.Group, c.RestaurantId, c.Restaurant, c.At, 0, c.Fee, true))];
    }

    /// <summary>
    /// A group's credit that is not already spoken for. Credit comes off a bill when the bill is made, at the end of
    /// the month; until then the cards mailed since the last bill are counted against it here, so the same credit
    /// cannot be spent twice.
    /// </summary>
    public async Task<int> SpendableCreditAsync(int groupId, CancellationToken ct = default)
    {
        var balance = await db.GroupCredits.Where(c => c.GroupId == groupId).SumAsync(c => (int?)c.AmountCents, ct) ?? 0;
        if (balance <= 0) return 0;
        var billedMonths = await db.MailInvoices.AsNoTracking().Where(i => i.GroupId == groupId).Select(i => i.Month).ToListAsync(ct);
        var charges = await ChargesAsync(groupId, ct);
        var notBilledYet = charges.Where(c => !billedMonths.Contains(MonthKey(c.At))).Sum(c => c.Cents);
        return Math.Max(0, balance - notBilledYet);
    }

    /// <summary>
    /// The percentage the saved payment method adds to a bill: the card fee for a credit card, the bank fee for a
    /// connected bank account, nothing for a debit or prepaid card (or when no method is saved).
    /// </summary>
    public decimal PayFeePercent(GroupBilling? b) => b switch
    {
        { PayKind: PayKinds.Card, PayFunding: "credit" } => site.Billing.CardFeePercent,
        { PayKind: PayKinds.Bank } => site.Billing.BankFeePercent,
        _ => 0m
    };

    /// <summary>That fee on an amount, to the nearest cent.</summary>
    public int PayFee(int cents, GroupBilling? b) => (int)Math.Round(cents * PayFeePercent(b) / 100m, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Monthly bills for mailed cards: creating them for a finished month, emailing them (and posting a printed copy
/// when that is switched on), and charging a group's saved payment method. Every step writes an audit row.
/// </summary>
public class BillingService(AppDbContext db, SiteMailing site, MailLedger ledger, IEmailSender email, IPostGridMailer mailer, IPaymentProvider payments,
    PaymentAccountStore payAccount, AuditService audit, ILogger<BillingService> log)
{
    public const string Schedule = "Billing schedule";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Dollars(int cents) => (cents / 100m).ToString("$#,##0.00", CultureInfo.InvariantCulture);
    public static List<InvoiceLine> Lines(MailInvoice i) => JsonSerializer.Deserialize<List<InvoiceLine>>(i.LinesJson, Json) ?? [];

    /// <summary>Creates the bill for every group that had cards mailed in a finished month and has no bill for it yet.</summary>
    public async Task<List<MailInvoice>> IssueAsync(string month, string by, CancellationToken ct = default)
    {
        if (MailLedger.Range(month) is not { } range) throw new InvalidOperationException("Choose a month.");
        if (range.To > DateTime.UtcNow) throw new InvalidOperationException($"{MailLedger.MonthName(month)} is not over yet. Bills are created once the month has ended.");
        var rows = (await ledger.ChargesAsync(null, ct)).Where(c => c.At >= range.From && c.At < range.To)
            .Select(c => new { c.GroupId, c.RestaurantId, c.Restaurant, Cost = c.CostCents, Fee = c.FeeCents, c.Printed }).ToList();
        var have = await db.MailInvoices.Where(i => i.Month == month).Select(i => i.GroupId).ToListAsync(ct);
        var settings = site.Billing;
        var created = new List<MailInvoice>();
        foreach (var g in rows.GroupBy(r => r.GroupId).Where(g => !have.Contains(g.Key)))
        {
            var cost = g.Sum(r => r.Cost);
            var fee = g.Sum(r => r.Fee);
            if (cost + fee <= 0) continue;
            // Cards mailed for the restaurant and cards it printed itself are separate lines, mailed first.
            var lines = g.GroupBy(r => new { r.RestaurantId, r.Restaurant, r.Printed }).OrderBy(r => r.Key.Restaurant).ThenBy(r => r.Key.Printed)
                .Select(r => new InvoiceLine(r.Key.RestaurantId, r.Key.Restaurant, r.Count(), r.Sum(x => x.Cost + x.Fee)) { Printed = r.Key.Printed }).ToList();
            var invoice = new MailInvoice
            {
                GroupId = g.Key, Month = month, Number = $"{month.Replace("-", "")}-{g.Key:000}", Cards = g.Count(), CostCents = cost, FeeCents = fee,
                TotalCents = cost + fee, LinesJson = JsonSerializer.Serialize(lines, Json), IssuedAt = DateTime.UtcNow,
                DueOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(settings.DueDays)
            };
            db.MailInvoices.Add(invoice);
            await db.SaveChangesAsync(ct);
            await audit.LogRawAsync(null, by, null, g.Key, "MailBillCreated", "MailInvoice", invoice.Id, new { invoice.Number, invoice.Month, invoice.Cards, invoice.TotalCents, invoice.DueOn });
            // Any credit the group has comes off the new bill straight away, so the bill it is sent already shows it.
            await ApplyCreditAsync(g.Key, by, ct);
            created.Add(invoice);
        }
        return created;
    }

    /// <summary>What a bill still needs paying: its total less the credit already used on it.</summary>
    public static int Due(MailInvoice i) => Math.Max(0, i.TotalCents - i.CreditCents);

    /// <summary>A group's credit: everything given or returned, less everything used or taken back.</summary>
    public async Task<int> CreditBalanceAsync(int groupId, CancellationToken ct = default) =>
        await db.GroupCredits.Where(c => c.GroupId == groupId).SumAsync(c => (int?)c.AmountCents, ct) ?? 0;

    /// <summary>
    /// Uses a group's credit on its bills that are waiting for payment, oldest first. Each use is a line in the
    /// credit account and an audit row; a bill the credit covers in full is marked paid. Returns what was used.
    /// </summary>
    public async Task<int> ApplyCreditAsync(int groupId, string by, CancellationToken ct = default)
    {
        var left = await CreditBalanceAsync(groupId, ct);
        if (left <= 0) return 0;
        var waiting = (await db.MailInvoices.Where(i => i.GroupId == groupId && i.Status == InvoiceStatuses.Open).ToListAsync(ct)).OrderBy(i => i.Month).ToList();
        var used = 0;
        foreach (var invoice in waiting)
        {
            var use = Math.Min(left, Due(invoice));
            if (use <= 0) continue;
            invoice.CreditCents += use;
            left -= use;
            used += use;
            db.GroupCredits.Add(new GroupCredit { GroupId = groupId, AmountCents = -use, Kind = CreditKinds.Applied, Details = $"Used on bill {invoice.Number}", InvoiceId = invoice.Id, CreatedBy = by });
            if (Due(invoice) == 0)
            {
                invoice.Status = InvoiceStatuses.Paid;
                invoice.PaidAt = DateTime.UtcNow;
                invoice.PaidHow = PaidWithCredit;
                invoice.PaidBy = by;
                invoice.PaymentError = null;
            }
            await db.SaveChangesAsync(ct);
            await audit.LogRawAsync(null, by, null, groupId, "MailBillCreditApplied", "MailInvoice", invoice.Id,
                new { invoice.Number, AppliedCents = use, invoice.CreditCents, invoice.TotalCents, StillOwedCents = Due(invoice), invoice.Status });
            if (left <= 0) break;
        }
        return used;
    }

    /// <summary>How a bill was paid when the group's credit covered all of it.</summary>
    public const string PaidWithCredit = "credit";

    /// <summary>Where a group's bills go: its billing email, or its administrators when none is set.</summary>
    public async Task<List<string>> RecipientsAsync(int groupId, CancellationToken ct = default)
    {
        var own = await db.GroupBilling.AsNoTracking().Where(b => b.GroupId == groupId).Select(b => b.BillingEmail).FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(own)) return [own.Trim()];
        var admins = await db.Users.AsNoTracking().Where(u => u.GroupId == groupId && u.IsActive && u.Role == UserRole.GroupAdmin && u.Email != null).Select(u => u.Email!).ToListAsync(ct);
        return admins.Where(e => e.Contains('@')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Emails the bill (when emailing is on, or when asked to send it again) and posts a printed copy when that is
    /// on and both addresses are known. A failure is kept on the bill for the System Administrator; it is not retried by itself.
    /// </summary>
    public async Task DeliverAsync(MailInvoice invoice, string by, bool again, CancellationToken ct = default)
    {
        var settings = site.Billing;
        var group = await db.Groups.AsNoTracking().Where(g => g.Id == invoice.GroupId).Select(g => g.Name).FirstOrDefaultAsync(ct) ?? "";
        var billing = await db.GroupBilling.AsNoTracking().FirstOrDefaultAsync(b => b.GroupId == invoice.GroupId, ct);
        var problems = new List<string>();

        if ((settings.EmailBills || again) && (invoice.EmailedAt is null || again))
        {
            var to = await RecipientsAsync(invoice.GroupId, ct);
            if (email is LoggingEmailSender) problems.Add("Email is not set up on this site, so the bill was not emailed.");
            else if (to.Count == 0) problems.Add("Nobody to email: the group has no billing email and none of its administrators has an email address.");
            else
            {
                try
                {
                    var subject = $"{settings.BusinessName}: bill {invoice.Number} for cards mailed in {MailLedger.MonthName(invoice.Month)}";
                    foreach (var address in to)
                        await email.SendAsync(address, subject, BillHtml(invoice, group, billing, settings, forPrint: false), BillText(invoice, group, settings), ct);
                    invoice.EmailedAt = DateTime.UtcNow;
                    invoice.EmailedTo = string.Join(", ", to);
                    await audit.LogRawAsync(null, by, null, invoice.GroupId, "MailBillEmailed", "MailInvoice", invoice.Id, new { invoice.Number, To = invoice.EmailedTo });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Bill {Number} could not be emailed", invoice.Number);
                    problems.Add("The bill could not be emailed. System status shows what went wrong.");
                }
            }
        }

        if (settings.PrintBills && invoice.PrintedId is null && site.Account is { } account)
        {
            if (billing is null || new[] { billing.Street, billing.City, billing.State, billing.Zip }.Any(string.IsNullOrWhiteSpace))
                problems.Add("No printed copy was posted: the group has no billing address.");
            else if (!settings.HasAddress)
                problems.Add("No printed copy was posted: enter your own business address under Billing settings (it is the return address).");
            else
            {
                try
                {
                    var letter = await mailer.CreateLetterAsync(account.ApiKey, new MailLetterRequest(
                        new MailContact(null, null, string.IsNullOrWhiteSpace(billing.BillingName) ? group : billing.BillingName, billing.Street!, null, billing.City!, billing.State!, billing.Zip!),
                        new MailContact(null, null, settings.BusinessName, settings.Street!, null, settings.City!, settings.State!, settings.Zip!),
                        BillHtml(invoice, group, billing, settings, forPrint: true), $"Bill {invoice.Number} for {group}",
                        new Dictionary<string, string> { ["invoice"] = invoice.Number, ["groupId"] = invoice.GroupId.ToString() }), $"rf-bill-{invoice.Id}", ct);
                    invoice.PrintedId = letter.Id;
                    invoice.PrintedAt = DateTime.UtcNow;
                    await audit.LogRawAsync(null, by, null, invoice.GroupId, "MailBillPosted", "MailInvoice", invoice.Id, new { invoice.Number, Test = !letter.Live });
                }
                catch (PostGridException ex)
                {
                    log.LogWarning("PostGrid did not take the printed copy of bill {Number}: {Message}", invoice.Number, ex.Message);
                    problems.Add("The printed copy could not be posted: " + ex.Message);
                }
            }
        }

        invoice.DeliveryError = problems.Count == 0 ? null : string.Join(" ", problems);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Charges a bill to the group's saved payment method: the bill's total, plus the fee for that kind of method (a
    /// credit card or a bank account). The idempotency key is the bill and its attempt number, so a repeated request cannot charge twice.
    /// </summary>
    public async Task<(bool Ok, string Message)> ChargeAsync(MailInvoice invoice, string by, int? userId, UserRole? role, CancellationToken ct = default)
    {
        if (invoice.Status != InvoiceStatuses.Open) return (false, "This bill is not waiting for payment.");
        if (payAccount.Current is not { } account) return (false, "Paying on the site is not set up yet.");
        var b = await db.GroupBilling.FirstOrDefaultAsync(x => x.GroupId == invoice.GroupId, ct);
        if (b?.PayMethodId is null || b.PayCustomerId is null || b.PayKind is null) return (false, "No payment method is saved for this investment group.");
        // Credit already used on the bill is not charged again: only what is still owed, plus the fee on that.
        var fee = ledger.PayFee(Due(invoice), b);
        var amount = Due(invoice) + fee;
        if (amount < 50) return (false, "This bill is under 50 cents, which is too small to charge. It can be paid another way and marked as paid.");
        try
        {
            var state = await payments.ChargeAsync(account.SecretKey, new ChargeRequest(b.PayCustomerId, b.PayMethodId, b.PayKind, b.PayMandateId, amount,
                $"{site.Billing.BusinessName} bill {invoice.Number}", new Dictionary<string, string> { ["invoice"] = invoice.Number, ["groupId"] = invoice.GroupId.ToString() }),
                $"rf-bill{invoice.Id}-try{invoice.PaymentAttempts}", ct);
            invoice.SurchargeCents = fee;
            invoice.PaidHow = b.PayKind;
            Apply(invoice, state, by);
            await db.SaveChangesAsync(ct);
            await audit.LogRawAsync(userId, by, role, invoice.GroupId, invoice.Status == InvoiceStatuses.Open ? "MailBillPaymentFailed" : "MailBillCharged", "MailInvoice", invoice.Id,
                new { invoice.Number, ChargedCents = amount, PayFeeCents = fee, With = b.PayLabel, invoice.Status, PaymentRef = state.Id, Error = invoice.PaymentError, Test = account.Test });
            return invoice.Status switch
            {
                InvoiceStatuses.Paid => (true, $"Paid: {Dollars(amount)} charged to {b.PayLabel}."),
                InvoiceStatuses.Processing => (true, $"{Dollars(amount)} is being taken from {b.PayLabel}. Bank payments take a few working days to arrive; the bill shows as paid when it does."),
                _ => (false, invoice.PaymentError ?? "The payment did not go through.")
            };
        }
        catch (PaymentException ex)
        {
            // A definite refusal needs a new key next time; when the service could not be reached the key is kept,
            // so a retry can never charge twice if the first request did get through.
            if (ex.Answered) invoice.PaymentAttempts++;
            invoice.PaymentError = ex.Message;
            await db.SaveChangesAsync(ct);
            await audit.LogRawAsync(userId, by, role, invoice.GroupId, "MailBillPaymentFailed", "MailInvoice", invoice.Id, new { invoice.Number, ChargedCents = amount, Error = ex.Message });
            return (false, ex.Message);
        }
    }

    private static void Apply(MailInvoice invoice, PaymentState state, string by)
    {
        if (!string.IsNullOrEmpty(state.Id)) invoice.PaymentRef = state.Id;
        switch (state.Status)
        {
            case PayStates.Succeeded:
                invoice.Status = InvoiceStatuses.Paid;
                invoice.PaidAt = DateTime.UtcNow;
                invoice.PaidBy = by;
                invoice.PaymentError = null;
                break;
            case PayStates.Processing:
                invoice.Status = InvoiceStatuses.Processing;
                invoice.PaidBy = by;
                invoice.PaymentError = null;
                break;
            default:
                // Nothing was taken, so the bill carries no payment: it can be paid again, or marked paid by hand.
                invoice.Status = InvoiceStatuses.Open;
                invoice.PaymentRef = null;
                invoice.PaymentAttempts++;
                invoice.SurchargeCents = 0;
                invoice.PaidHow = null;
                invoice.PaymentError = state.Error ?? "The payment did not go through.";
                break;
        }
    }

    /// <summary>Asks the payment service how each bank payment still on its way has ended.</summary>
    public async Task RefreshPaymentsAsync(CancellationToken ct = default)
    {
        if (payAccount.Current is not { } account) return;
        var waiting = await db.MailInvoices.Where(i => i.Status == InvoiceStatuses.Processing && i.PaymentRef != null).ToListAsync(ct);
        foreach (var invoice in waiting)
        {
            try { await SettleAsync(invoice, account, Schedule, ct); }
            catch (PaymentException ex) { log.LogWarning("Could not check the payment for bill {Number}: {Message}", invoice.Number, ex.Message); }
        }
    }

    /// <summary>
    /// The payment service says something happened to a payment. Its word is not taken for what: the site asks the
    /// payment service itself how that payment stands and settles the bill it belongs to. True when a bill was waiting on it.
    /// </summary>
    public async Task<bool> PaymentNotifiedAsync(string paymentId, CancellationToken ct = default)
    {
        if (payAccount.Current is not { } account) return false;
        var invoice = await db.MailInvoices.FirstOrDefaultAsync(i => i.PaymentRef == paymentId && i.Status == InvoiceStatuses.Processing, ct);
        if (invoice is null) return false;
        await SettleAsync(invoice, account, "Payment notification", ct);
        return true;
    }

    private async Task SettleAsync(MailInvoice invoice, PaymentAccount account, string by, CancellationToken ct)
    {
        var state = await payments.GetPaymentAsync(account.SecretKey, invoice.PaymentRef!, ct);
        if (state.Status == PayStates.Processing) return;
        Apply(invoice, state, invoice.PaidBy ?? by);
        await db.SaveChangesAsync(ct);
        await audit.LogRawAsync(null, by, null, invoice.GroupId, invoice.Status == InvoiceStatuses.Paid ? "MailBillPaymentArrived" : "MailBillPaymentFailed", "MailInvoice", invoice.Id,
            new { invoice.Number, invoice.Status, PaymentRef = state.Id, Error = invoice.PaymentError });
    }

    /// <summary>
    /// The hourly round: on or after the bill day, create last month's bills; send any bill not yet sent; charge the
    /// bills of groups that chose automatic payment; and see how bank payments on their way have ended.
    /// </summary>
    public async Task RunScheduledAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        if (now.Day >= site.Billing.BillDay)
            await IssueAsync(MailLedger.MonthKey(MailLedger.MonthStart(now).AddMonths(-1)), Schedule, ct);

        // A bill the group's credit paid in full is still sent, so the group has it for its records.
        var unsent = await db.MailInvoices.Where(i => (i.Status == InvoiceStatuses.Open || (i.Status == InvoiceStatuses.Paid && i.PaidHow == PaidWithCredit))
                                                      && i.EmailedAt == null && i.PrintedId == null && i.DeliveryError == null).ToListAsync(ct);
        foreach (var invoice in unsent) await DeliverAsync(invoice, Schedule, again: false, ct);

        if (payAccount.Current is not null)
        {
            var auto = await db.GroupBilling.AsNoTracking().Where(b => b.AutoPay && b.PayMethodId != null).Select(b => b.GroupId).ToListAsync(ct);
            var due = await db.MailInvoices.Where(i => i.Status == InvoiceStatuses.Open && i.PaymentRef == null && i.PaymentError == null && auto.Contains(i.GroupId)).ToListAsync(ct);
            foreach (var invoice in due) await ChargeAsync(invoice, "Automatic payment", null, null, ct);
            await RefreshPaymentsAsync(ct);
        }
    }

    // ---- The bill itself ----------------------------------------------------------------------------------------

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    public string BillHtml(MailInvoice i, string group, GroupBilling? billing, BillingSettings s, bool forPrint)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><style>body{font-family:Arial,Helvetica,sans-serif;color:#1d1d1f;font-size:11pt;line-height:1.4;margin:")
          .Append(forPrint ? ".75in" : "0").Append("}table{border-collapse:collapse;width:100%;max-width:6.5in}th,td{text-align:left;padding:6px 8px;border-bottom:1px solid #ddd}")
          .Append("th.n,td.n{text-align:right}h1{font-size:18pt;margin:0 0 4px}.muted{color:#555}.total td{font-weight:bold;border-top:2px solid #333;border-bottom:0}</style></head><body>");
        sb.Append($"<h1>{H(s.BusinessName)}</h1>");
        if (s.HasAddress) sb.Append($"<p class=\"muted\">{H(s.Street)}, {H(s.City)}, {H(s.State)} {H(s.Zip)}</p>");
        sb.Append($"<p><strong>Bill {H(i.Number)}</strong> for {H(string.IsNullOrWhiteSpace(billing?.BillingName) ? group : billing!.BillingName)}<br>");
        sb.Append($"Coupon cards for {H(MailLedger.MonthName(i.Month))}<br>");
        sb.Append($"Date {i.IssuedAt.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)} &middot; Due {i.DueOn.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}</p>");
        sb.Append("<table><tr><th>Restaurant</th><th class=\"n\">Cards</th><th class=\"n\">Amount</th></tr>");
        foreach (var l in Lines(i)) sb.Append($"<tr><td>{H(l.Restaurant)}<br><span class=\"muted\">{(l.Printed ? "Printed by you, stamped by hand" : "Printed and mailed for you")}</span></td><td class=\"n\">{l.Cards}</td><td class=\"n\">{Dollars(l.Cents)}</td></tr>");
        if (i.CreditCents > 0)
        {
            sb.Append($"<tr class=\"total\"><td>Total</td><td class=\"n\">{i.Cards}</td><td class=\"n\">{Dollars(i.TotalCents)}</td></tr>");
            sb.Append($"<tr><td>Less your credit</td><td></td><td class=\"n\">-{Dollars(i.CreditCents)}</td></tr>");
            sb.Append($"<tr class=\"total\"><td>{(Due(i) == 0 ? "Nothing to pay" : "Total due")}</td><td></td><td class=\"n\">{Dollars(Due(i))}</td></tr></table>");
        }
        else sb.Append($"<tr class=\"total\"><td>Total due</td><td class=\"n\">{i.Cards}</td><td class=\"n\">{Dollars(i.TotalCents)}</td></tr></table>");
        if (!string.IsNullOrWhiteSpace(s.SiteUrl))
            sb.Append(forPrint
                ? $"<p>To see this bill or pay it, sign in at {H(s.SiteUrl)} and open Billing.</p>"
                : $"<p><a href=\"{H(s.SiteUrl)}/billing\">See this bill and pay it on the site</a> (sign in, then Billing).</p>");
        if (payAccount.Current is not null && (s.CardFeePercent > 0 || s.BankFeePercent > 0))
            sb.Append($"<p class=\"muted\">Paying from a bank account adds {(s.BankFeePercent > 0 ? $"{s.BankFeePercent:0.##}%" : "nothing")}. "
                      + $"Paying by credit card adds {(s.CardFeePercent > 0 ? $"{s.CardFeePercent:0.##}%" : "nothing")}. A debit card adds nothing.</p>");
        if (!string.IsNullOrWhiteSpace(s.PayInstructions)) sb.Append($"<p>{H(s.PayInstructions).Replace("\n", "<br>")}</p>");
        if (!string.IsNullOrWhiteSpace(s.BusinessEmail)) sb.Append($"<p class=\"muted\">Questions about this bill: {H(s.BusinessEmail)}</p>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string BillText(MailInvoice i, string group, BillingSettings s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{s.BusinessName}: bill {i.Number} for {group}");
        sb.AppendLine($"Coupon cards for {MailLedger.MonthName(i.Month)}");
        foreach (var l in Lines(i)) sb.AppendLine($"  {l.Restaurant}, {(l.Printed ? "printed by you" : "mailed for you")}: {l.Cards} card{(l.Cards == 1 ? "" : "s")}, {Dollars(l.Cents)}");
        if (i.CreditCents > 0) sb.AppendLine($"Total: {Dollars(i.TotalCents)}, less your credit of {Dollars(i.CreditCents)}");
        sb.AppendLine(Due(i) == 0 && i.CreditCents > 0 ? "Nothing to pay: your credit covered this bill."
            : $"Total due: {Dollars(Due(i))} by {i.DueOn.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(s.SiteUrl)) sb.AppendLine($"See it and pay it: {s.SiteUrl}/billing");
        if (!string.IsNullOrWhiteSpace(s.PayInstructions)) sb.AppendLine(s.PayInstructions);
        return sb.ToString();
    }
}

/// <summary>Runs the billing round once an hour. Bills are only ever created once per group and month, so a restart or a second run changes nothing.</summary>
public class BillingWorker(IServiceScopeFactory scopes, PostGridOptions options, ILogger<BillingWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!options.BillingSchedule) return;
        try { await Task.Delay(TimeSpan.FromMinutes(2), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<BillingService>().RunScheduledAsync(stop);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { log.LogError(ex, "The billing round failed"); }
            try { await Task.Delay(TimeSpan.FromHours(1), stop); } catch (OperationCanceledException) { return; }
        }
    }
}
