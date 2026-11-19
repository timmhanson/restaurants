using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Models;
using RestaurantFeedback.Api.Services;

namespace RestaurantFeedback.Api.Tests;

/// <summary>
/// Cards printed and mailed for investment groups on the site's own PostGrid account: the site's key, sending
/// (confirmed, capped, never twice, cancellable), what each group is billed (the mail house's cost plus the site's
/// fee, up to a monthly limit), monthly bills and paying them, each restaurant's own card design, and reading
/// PostGrid's and Stripe's answers. Groups never see the mail house's name.
/// </summary>
[Collection(ApiCollection.Name)]
public class PostGridTests(ApiFactory factory)
{
    private const string TestKey = "test_sk_0123456789abcdefPGKEYwxyz";
    private const string LiveKey = "live_sk_0123456789abcdefPGKEYlive";
    private static int _n;

    private static CommentRequest Card(int rid, string last) => new()
    {
        RestaurantId = rid, Source = "SMG survey", GuestFirstName = "Grid", GuestLastName = last, Phone = $"815-555-{8000 + Interlocked.Increment(ref _n)}",
        StreetAddress = "9 Mailer Rd", City = "Joliet", State = "IL", Zip = "60431",
        VisitDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1), Reason = "Order mistake", CouponOffer = "Free <Concrete> Mixer", AuthorizedBy = "M. Smith", Force = true
    };

    /// <summary>
    /// The owner (who may spend), a System Administrator viewing the owner's group, a System Administrator in their
    /// own area, and the restaurant; with the site's mailing key set to <paramref name="key"/>.
    /// </summary>
    private async Task<(HttpClient Owner, HttpClient Admin, HttpClient Site, int Rid, int GroupId)> SetUpAsync(string key = TestKey)
    {
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        var r = (await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants"))!.First();
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/restaurants/{r.Id}", new RestaurantRequest
        {
            Name = r.Name, StreetAddress = "1 Test St", City = "Joliet", State = "IL", Zip = "60431", Phone = "815-555-0000"
        })).StatusCode);
        var groupId = (await owner.GetFromJsonAsync<UserDto>("/api/auth/me"))!.GroupId!.Value;
        var admin = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword, groupId);
        var site = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync("/api/system/postgrid/mail", new PostGridMailKeyRequest(key))).StatusCode);
        // A payment method is required before mailing unless the System Administrator switches that off; the tests
        // that are not about payment switch it off.
        await SaveSettings(site, s => s.RequirePaymentMethod = false);
        return (owner, admin, site, r.Id, groupId);
    }

    private static async Task<CommentSummary> Create(HttpClient client, CommentRequest card)
    {
        var res = await client.PostAsJsonAsync("/api/comments", card);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<CommentSummary>())!;
    }

    private static async Task<int> Price(HttpClient client) => (await client.GetFromJsonAsync<PrintConfig>("/api/comments/print-config"))!.MailCentsPerCard;

    private static async Task<List<MailOutcome>> Mail(HttpClient client, int[] ids)
    {
        var res = await client.PostAsJsonAsync("/api/comments/mail", new MailRequest(ids, ids.Length, await Price(client)));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<List<MailOutcome>>())!;
    }

    private async Task<T> Db<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static async Task<SystemBillingDto> Billing(HttpClient site) => (await site.GetFromJsonAsync<SystemBillingDto>("/api/system/billing"))!;

    private static async Task SaveSettings(HttpClient site, Action<BillingSettings> change)
    {
        var s = (await Billing(site)).Settings;
        change(s);
        Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync("/api/system/billing/settings", s)).StatusCode);
    }

    [Fact]
    public async Task The_sites_mailing_key_is_checked_stored_encrypted_and_only_the_System_Administrator_sets_it()
    {
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        var site = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword);

        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync("/api/system/postgrid/mail", new PostGridMailKeyRequest(TestKey))).StatusCode);
        var refused = await site.PutAsJsonAsync("/api/system/postgrid/mail", new PostGridMailKeyRequest("test_sk_bad_key_0000000000"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("did not accept", await refused.Content.ReadAsStringAsync());

        var saved = await site.PutAsJsonAsync("/api/system/postgrid/mail", new PostGridMailKeyRequest(TestKey));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.DoesNotContain(TestKey, await saved.Content.ReadAsStringAsync());
        var dto = (await site.GetFromJsonAsync<PostGridSiteDto>("/api/system/postgrid"))!;
        Assert.True(dto.Mail.Configured && dto.Mail.Test);
        Assert.Equal("wxyz", dto.Mail.KeyEnding);
        Assert.True((await site.PostAsync("/api/system/postgrid/mail/test", null)).IsSuccessStatusCode);

        var (stored, audited) = await Db(async db => ((await db.SiteSettings.FindAsync("postgrid.mail"))!.Value, db.AuditLogs.ToList().Any(a => (a.Details ?? "").Contains(TestKey))));
        Assert.DoesNotContain(TestKey, stored);
        Assert.False(audited);

        // Out of the box a group cannot send anything to the mail house, test orders included, until it has saved a
        // payment method; the System Administrator can switch that requirement off.
        Assert.True(new BillingSettings().RequirePaymentMethod);
        await SaveSettings(site, s => s.RequirePaymentMethod = true);
        var blocked = (await owner.GetFromJsonAsync<PrintConfig>("/api/comments/print-config"))!;
        Assert.True(blocked.MailAvailable && blocked.MailTest && blocked.MailNeedsPayMethod && !blocked.CanMail);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/comments/mail", new MailRequest([1], 1, blocked.MailCentsPerCard))).StatusCode);
        await SaveSettings(site, s => s.RequirePaymentMethod = false);
        var config = (await owner.GetFromJsonAsync<PrintConfig>("/api/comments/print-config"))!;
        Assert.True(config.MailAvailable && config.CanMail && config.MailTest && !config.MailNeedsPayMethod);
    }

    [Fact]
    public async Task Cards_are_mailed_once_marked_sent_and_can_be_cancelled_before_printing()
    {
        var (owner, _, _, rid, _) = await SetUpAsync();
        var card = await Create(owner, Card(rid, "Mailed"));
        var refused = await Create(owner, Card(rid, "Refused"));
        var before = factory.Mail.Created;

        // The confirmed count must match what is sent.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/comments/mail", new MailRequest([card.Id, refused.Id], 1, await Price(owner)))).StatusCode);
        Assert.Equal(before, factory.Mail.Created);

        var res = await owner.PostAsJsonAsync("/api/comments/mail", new MailRequest([card.Id, refused.Id], 2, await Price(owner)));
        var raw = await res.Content.ReadAsStringAsync();
        var outcomes = JsonSerializer.Deserialize<List<MailOutcome>>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var ok = outcomes.Single(o => o.CommentId == card.Id);
        Assert.True(ok.Ok && ok.Test && !ok.AlreadyMailed);
        Assert.Equal(MailStatuses.Ready, ok.Status);
        // The group is told why, without the mail house's name.
        Assert.Contains("The mailing service refused", outcomes.Single(o => o.CommentId == refused.Id).Error);
        Assert.DoesNotContain("PostGrid", raw);
        Assert.Equal(before + 1, factory.Mail.Created);

        // What the mail house was sent: the site's key, the guest and the restaurant, and the coupon with the text escaped.
        var sent = factory.Mail.Requests.Last(r => r.Request.To.LastName == "Mailed");
        Assert.Equal(TestKey, sent.Key);
        Assert.Equal("9 Mailer Rd", sent.Request.To.AddressLine1);
        Assert.Equal("1 Test St", sent.Request.From.AddressLine1);
        Assert.Contains("Free &lt;Concrete&gt; Mixer", sent.Request.BackHtml);
        Assert.Contains($"card{card.Id}-0", sent.IdempotencyKey);

        // The card is marked as sent, with a follow-up due, and shows where it is. A test order bills nothing.
        var listed = await owner.GetAsync($"/api/comments?ids={card.Id}");
        var listedRaw = await listed.Content.ReadAsStringAsync();
        Assert.DoesNotContain("PostGrid", listedRaw);
        var after = JsonSerializer.Deserialize<PagedResult<CommentSummary>>(listedRaw, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!.Items.Single();
        Assert.True(after.Responded && after.MailTest);
        Assert.Equal(MailStatuses.Ready, after.MailStatus);
        Assert.NotNull(after.FollowUpDueOn);
        Assert.Equal((0, 0), await Db(async db => { var c = await db.Comments.FindAsync(card.Id); return (c!.MailEstimateCents ?? -1, c.MailFeeCents ?? -1); }));

        // Never mailed twice.
        var again = (await Mail(owner, [card.Id])).Single();
        Assert.True(again.Ok && again.AlreadyMailed);
        Assert.Equal(before + 1, factory.Mail.Created);

        // A refused card can be tried again; it gets a new idempotency key so the mail house does not just repeat its refusal.
        await Mail(owner, [refused.Id]);
        var keys = factory.Mail.Requests.Where(r => r.Request.To.LastName == "Refused").Select(r => r.IdempotencyKey).ToList();
        Assert.Equal(2, keys.Distinct().Count());

        // Cancelled before printing: back to "not sent", and mailing it again creates a new card.
        var cancel = await owner.PostAsync($"/api/comments/{card.Id}/mail/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(MailStatuses.Cancelled, (await cancel.Content.ReadFromJsonAsync<MailStatusDto>())!.Status);
        var reopened = (await owner.GetFromJsonAsync<PagedResult<CommentSummary>>($"/api/comments?ids={card.Id}"))!.Items.Single();
        Assert.False(reopened.Responded);
        Assert.Contains(card.Id, (await owner.GetFromJsonAsync<PagedResult<CommentSummary>>("/api/comments?status=waiting&pageSize=500"))!.Items.Select(c => c.Id));
        var remailed = (await Mail(owner, [card.Id])).Single();
        Assert.True(remailed.Ok && !remailed.AlreadyMailed);
        Assert.Equal(before + 2, factory.Mail.Created);

        // Once it is printing it can no longer be cancelled.
        factory.Mail.SetStatus(await Db(async db => (await db.Comments.FindAsync(card.Id))!.MailId!), "printing");
        var late = await owner.PostAsync($"/api/comments/{card.Id}/mail/cancel", null);
        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
        Assert.DoesNotContain("PostGrid", await late.Content.ReadAsStringAsync());
        var status = (await (await owner.PostAsJsonAsync("/api/comments/mail/status", new CardIdsRequest([card.Id]))).Content.ReadFromJsonAsync<List<MailStatusDto>>())!.Single();
        Assert.Equal("printing", status.Status);
        Assert.True(status.HasProof);

        var history = (await owner.GetFromJsonAsync<List<ActivityDto>>($"/api/comments/{card.Id}/activity"))!;
        Assert.Equal(2, history.Count(a => a.Kind == "Mailed"));
        Assert.Single(history, a => a.Kind == "MailCancelled");
        Assert.DoesNotContain(history, a => (a.Text ?? "").Contains("PostGrid"));
    }

    [Fact]
    public async Task Only_people_allowed_to_spend_can_mail_and_a_mailing_is_capped()
    {
        var (owner, _, _, rid, _) = await SetUpAsync();
        var card = await Create(owner, Card(rid, "Capped"));
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/users", new UserRequest
        {
            Username = "grid-mgr", DisplayName = "Grid Manager", Password = "MgrPass!1", Role = "Manager", RestaurantIds = [rid], Permissions = ["ViewComments", "MarkResponded"]
        })).StatusCode);
        var manager = await factory.LoginAsync("grid-mgr", "MgrPass!1");
        Assert.False((await manager.GetFromJsonAsync<PrintConfig>("/api/comments/print-config"))!.CanMail);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync("/api/comments/mail", new MailRequest([card.Id], 1, await Price(owner)))).StatusCode);
        // Billing and card designs are not a manager's either.
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/billing")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/card-designs")).StatusCode);

        var tooMany = Enumerable.Range(1, 101).Select(i => card.Id + i * 100000).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/comments/mail", new MailRequest(tooMany, tooMany.Length, await Price(owner)))).StatusCode);
    }

    [Fact]
    public async Task Real_cards_are_billed_at_cost_plus_the_groups_fee_and_stop_at_the_monthly_limit()
    {
        var (owner, admin, site, rid, groupId) = await SetUpAsync(LiveKey);
        try
        {
            // Only the System Administrator sets prices, and a group can have its own fee.
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/system/billing")).StatusCode);
            await SaveSettings(site, s => { s.CostCents = 90; s.FeeCents = 6; s.MonthlyLimitCents = 5000; s.RequirePaymentMethod = false; });
            Assert.Equal(96, await Price(owner));
            Assert.Equal(HttpStatusCode.BadRequest, (await site.PutAsJsonAsync($"/api/system/billing/groups/{groupId}", new GroupBillingTermsRequest(900, null))).StatusCode);
            var usedBefore = (await owner.GetFromJsonAsync<PrintConfig>("/api/comments/print-config"))!.MailUsedCents;
            Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/billing/groups/{groupId}", new GroupBillingTermsRequest(10, usedBefore + 300))).StatusCode);
            var config = (await owner.GetFromJsonAsync<PrintConfig>("/api/comments/print-config"))!;
            Assert.True(config.CanMail && !config.MailTest);
            Assert.Equal(100, config.MailCentsPerCard);
            Assert.Equal(usedBefore + 300, config.MailLimitCents);

            var cards = new List<CommentSummary>();
            foreach (var name in new[] { "Billed One", "Billed Two", "Billed Three", "Billed Four" }) cards.Add(await Create(owner, Card(rid, name)));
            var before = factory.Mail.Created;

            // The price the person confirmed must be the price charged.
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/comments/mail", new MailRequest([cards[0].Id], 1, 96))).StatusCode);
            Assert.Equal(before, factory.Mail.Created);

            var sent = await Mail(owner, [cards[0].Id, cards[1].Id]);
            Assert.All(sent, o => Assert.True(o.Ok && !o.Test));
            Assert.Equal((90, 10), await Db(async db => { var c = await db.Comments.FindAsync(cards[0].Id); return (c!.MailEstimateCents ?? -1, c.MailFeeCents ?? -1); }));
            Assert.Equal(usedBefore + 200, (await owner.GetFromJsonAsync<PrintConfig>("/api/comments/print-config"))!.MailUsedCents);

            // Two more would pass the limit: nothing is sent, and the refusal is audited.
            var over = await owner.PostAsJsonAsync("/api/comments/mail", new MailRequest([cards[2].Id, cards[3].Id], 2, 100));
            Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
            Assert.Contains("over its limit", await over.Content.ReadAsStringAsync());
            Assert.Equal(before + 2, factory.Mail.Created);
            Assert.True(await Db(db => Task.FromResult(db.AuditLogs.Any(a => a.Action == "CardMailOverLimit" && a.GroupId == groupId))));

            // A cancelled card is not billed, which makes room for one more.
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/comments/{cards[1].Id}/mail/cancel", null)).StatusCode);
            var billing = (await owner.GetFromJsonAsync<GroupBillingDto>("/api/billing"))!;
            Assert.False(billing.CanManage);
            Assert.Equal(100, billing.CentsPerCard);
            Assert.True(billing.UsedCents >= 100);
            Assert.Empty(billing.Invoices);
            Assert.All(await Mail(owner, [cards[2].Id, cards[3].Id]), o => Assert.True(o.Ok));

            // A limit of nothing switches mailing off for the group.
            Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/billing/groups/{groupId}", new GroupBillingTermsRequest(10, 0))).StatusCode);
            Assert.False((await owner.GetFromJsonAsync<PrintConfig>("/api/comments/print-config"))!.MailAvailable);

            // The System Administrator's statement splits cost from fee; the group never sees the split.
            var statement = (await site.GetFromJsonAsync<SiteMailStatement>($"/api/system/billing/statement?groupId={groupId}"))!;
            Assert.True(statement.FeeCents >= 30 && statement.CostCents >= 270);
            Assert.DoesNotContain("feeCents", await (await admin.GetAsync("/api/billing")).Content.ReadAsStringAsync());
        }
        finally
        {
            await site.PutAsJsonAsync($"/api/system/billing/groups/{groupId}", new GroupBillingTermsRequest(null, 1_000_000));
        }
    }

    [Fact]
    public async Task Monthly_bills_are_created_once_emailed_paid_on_the_site_or_marked_paid_and_can_be_required_before_mailing()
    {
        var (owner, admin, site, rid, groupId) = await SetUpAsync(LiveKey);
        await SaveSettings(site, s => { s.CostCents = 90; s.FeeCents = 6; s.DueDays = 15; s.EmailBills = true; s.PrintBills = false; s.CardFeePercent = 3m; s.BankFeePercent = 1m; s.RequirePaymentMethod = false; s.BusinessName = "Hanson Mailing"; });
        Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/billing/groups/{groupId}", new GroupBillingTermsRequest(null, 1_000_000))).StatusCode);
        // The publishable key is refused; a restricted key is what the site asks for.
        Assert.Equal(HttpStatusCode.BadRequest, (await site.PutAsJsonAsync("/api/system/stripe", new StripeKeyRequest("pk_test_fake-key-for-unit-tests-NOPE"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync("/api/system/stripe", new StripeKeyRequest("rk_test_fake-key-for-unit-tests-STRP"))).StatusCode);
        var stripeAccount = (await site.GetFromJsonAsync<StripeAccountDto>("/api/system/stripe"))!;
        Assert.Equal(("STRP", true, true), (stripeAccount.KeyEnding, stripeAccount.Restricted, stripeAccount.Test));

        // Three cards in each of three finished months.
        var now = DateTime.UtcNow;
        string Month(int back) => MailLedger.MonthKey(MailLedger.MonthStart(now).AddMonths(-back));
        async Task MailIn(int back, string name)
        {
            var ids = new List<int>();
            for (var i = 0; i < 3; i++) ids.Add((await Create(owner, Card(rid, $"{name} {i}"))).Id);
            Assert.All(await Mail(owner, ids.ToArray()), o => Assert.True(o.Ok));
            await Db(async db =>
            {
                foreach (var c in db.Comments.Where(c => ids.Contains(c.Id))) c.MailedAt = MailLedger.MonthStart(now).AddMonths(-back).AddDays(2);
                return await db.SaveChangesAsync();
            });
        }
        await MailIn(1, "Last Month");
        await MailIn(2, "Two Back");
        await MailIn(3, "Three Back");

        // Bills go to the group's billing email, which only its administrators may set.
        var contact = new BillingContact("Test Group LLC", "bills@testgroup.example", "5 Ledger Ln", "Joliet", "IL", "60431");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync("/api/billing/contact", contact)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/billing/contact", contact)).StatusCode);

        // A month that is not over cannot be billed; a finished one is billed once.
        Assert.Equal(HttpStatusCode.BadRequest, (await site.PostAsJsonAsync("/api/system/billing/issue", new IssueBillsRequest(Month(0)))).StatusCode);
        Assert.Contains(Month(1), (await Billing(site)).UnbilledMonths);
        var emailsBefore = factory.Emails.Sent.Count;
        foreach (var back in new[] { 1, 2, 3 })
            Assert.Equal(HttpStatusCode.OK, (await site.PostAsJsonAsync("/api/system/billing/issue", new IssueBillsRequest(Month(back)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.PostAsJsonAsync("/api/system/billing/issue", new IssueBillsRequest(Month(1)))).StatusCode);
        var all = (await Billing(site)).Invoices.Where(i => i.GroupId == groupId).ToList();
        Assert.Equal(3, all.Count);
        var bill = all.Single(i => i.Month == Month(1));
        Assert.Equal((3, 270, 18, 288), (bill.Cards, bill.CostCents, bill.FeeCents, bill.TotalCents));
        Assert.Equal("open", bill.Status);
        Assert.Equal("bills@testgroup.example", bill.EmailedTo);
        Assert.Equal(DateOnly.FromDateTime(now).AddDays(15), bill.DueOn);
        var mails = factory.Emails.Sent.Skip(emailsBefore).ToList();
        Assert.Equal(3, mails.Count);
        Assert.Contains(mails, m => m.to == "bills@testgroup.example" && m.subject.Contains(bill.Number) && m.text.Contains("$2.88"));
        Assert.DoesNotContain(mails, m => m.text.Contains("PostGrid"));

        // The group's administrators see the bill (never the cost and fee split); an owner sees only usage.
        var seenRaw = await (await admin.GetAsync("/api/billing")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("costCents", seenRaw);
        var seen = (await admin.GetFromJsonAsync<GroupBillingDto>("/api/billing"))!;
        Assert.True(seen.CanManage && seen.PayAvailable);
        Assert.Equal(3, seen.Invoices.Count);
        Assert.Equal(288 * 3, seen.OwedCents);
        var ownerView = (await owner.GetFromJsonAsync<GroupBillingDto>("/api/billing"))!;
        Assert.Empty(ownerView.Invoices);
        Assert.Contains(ownerView.Months, m => m.Month == Month(1) && m.Cards == 3 && m.TotalCents == 288);

        // Paid by check: the System Administrator marks it, and can put it back.
        Assert.Equal(HttpStatusCode.OK, (await site.PostAsJsonAsync($"/api/system/billing/invoices/{bill.Id}/paid", new MarkPaidRequest("check", "Check 1042", null))).StatusCode);
        var paid = (await Billing(site)).Invoices.Single(i => i.Id == bill.Id);
        Assert.Equal(("paid", "check", "Check 1042"), (paid.Status, paid.PaidHow, paid.PaidNote));
        Assert.Equal(HttpStatusCode.BadRequest, (await site.PostAsJsonAsync($"/api/system/billing/invoices/{bill.Id}/paid", new MarkPaidRequest("check", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.PostAsync($"/api/system/billing/invoices/{bill.Id}/unpaid", null)).StatusCode);
        Assert.Equal("open", (await Billing(site)).Invoices.Single(i => i.Id == bill.Id).Status);
        Assert.Single((await site.GetFromJsonAsync<SystemBillingDto>($"/api/system/billing?groupId={groupId}&month={Month(1)}&status=open"))!.Invoices);

        // Paying on the site: a credit card is saved on the payment service's page, and carries the 3% fee.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsync("/api/billing/payment-method/start", null)).StatusCode);
        var start = (await (await admin.PostAsync("/api/billing/payment-method/start", null)).Content.ReadFromJsonAsync<PaySetupStart>())!;
        Assert.StartsWith("https://pay.test/", start.Url);
        var abandoned = (await (await admin.PostAsJsonAsync("/api/billing/payment-method/finish", new PaySetupFinish("cs_abandoned"))).Content.ReadFromJsonAsync<BillingActionResult>())!;
        Assert.False(abandoned.Ok);
        factory.Payments.NextMethod = new SavedMethod("pm_card1", PayKinds.Card, "credit", "Visa credit card ending 4242", null);
        var savedCard = (await (await admin.PostAsJsonAsync("/api/billing/payment-method/finish", new PaySetupFinish("cs_test_1"))).Content.ReadFromJsonAsync<BillingActionResult>())!;
        Assert.True(savedCard.Ok);
        Assert.Equal("Visa credit card ending 4242", savedCard.Billing.Method!.Label);
        Assert.Equal(9, savedCard.Billing.Invoices.Single(i => i.Id == bill.Id).PayFeeCents);

        var chargesBefore = factory.Payments.Charges.Count;
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/billing/invoices/{bill.Id}/pay", new PayInvoiceRequest(297))).StatusCode);
        // The amount confirmed must be the amount charged.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/billing/invoices/{bill.Id}/pay", new PayInvoiceRequest(288))).StatusCode);
        Assert.Equal(chargesBefore, factory.Payments.Charges.Count);
        // A decline leaves the bill open, with the reason.
        factory.Payments.DeclineNext = true;
        var declined = (await (await admin.PostAsJsonAsync($"/api/billing/invoices/{bill.Id}/pay", new PayInvoiceRequest(297))).Content.ReadFromJsonAsync<BillingActionResult>())!;
        Assert.False(declined.Ok);
        Assert.Equal("open", declined.Billing.Invoices.Single(i => i.Id == bill.Id).Status);
        var payment = (await (await admin.PostAsJsonAsync($"/api/billing/invoices/{bill.Id}/pay", new PayInvoiceRequest(297))).Content.ReadFromJsonAsync<BillingActionResult>())!;
        Assert.True(payment.Ok);
        var charged = factory.Payments.Charges[^1];
        Assert.Equal(297, charged.Request.AmountCents);
        Assert.NotEqual(factory.Payments.Charges[^2].IdempotencyKey, charged.IdempotencyKey);
        var after = payment.Billing.Invoices.Single(i => i.Id == bill.Id);
        Assert.Equal(("paid", "card", 9), (after.Status, after.PaidHow, after.SurchargeCents));
        // Never charged twice, and a bill paid through the payment service cannot be reopened by hand.
        Assert.False((await (await admin.PostAsJsonAsync($"/api/billing/invoices/{bill.Id}/pay", new PayInvoiceRequest(297))).Content.ReadFromJsonAsync<BillingActionResult>())!.Ok);
        Assert.Equal(chargesBefore + 2, factory.Payments.Charges.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await site.PostAsync($"/api/system/billing/invoices/{bill.Id}/unpaid", null)).StatusCode);

        // A bank account carries its own, smaller fee (1% here); the payment is on its way until the bank settles it.
        factory.Payments.NextMethod = new SavedMethod("pm_bank1", PayKinds.Bank, null, "Test Bank account ending 6789", "mandate_1");
        var savedBank = (await (await admin.PostAsJsonAsync("/api/billing/payment-method/finish", new PaySetupFinish("cs_test_1"))).Content.ReadFromJsonAsync<BillingActionResult>())!;
        var second = savedBank.Billing.Invoices.Single(i => i.Month == Month(2));
        Assert.Contains("1% bank account fee", savedBank.Message);
        Assert.Equal(3, second.PayFeeCents);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/billing/invoices/{second.Id}/pay", new PayInvoiceRequest(288))).StatusCode);
        var bank = (await (await admin.PostAsJsonAsync($"/api/billing/invoices/{second.Id}/pay", new PayInvoiceRequest(291))).Content.ReadFromJsonAsync<BillingActionResult>())!;
        Assert.True(bank.Ok);
        Assert.Equal(291, factory.Payments.Charges[^1].Request.AmountCents);
        Assert.Equal(("processing", 3), (bank.Billing.Invoices.Single(i => i.Id == second.Id).Status, bank.Billing.Invoices.Single(i => i.Id == second.Id).SurchargeCents));
        Assert.Equal("mandate_1", factory.Payments.Charges[^1].Request.MandateId);
        Assert.Equal(HttpStatusCode.BadRequest, (await site.PostAsJsonAsync($"/api/system/billing/invoices/{second.Id}/paid", new MarkPaidRequest("check", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync("/api/billing/payment-method")).StatusCode);

        // Stripe's notification settles the bank payment at once, but only when it is genuinely from Stripe. It comes
        // from outside the site (no session id) and nothing in it is believed until its signature has been checked.
        const string hookSecret = "whsec_fake-secret-for-unit-tests-HOOK";
        var stripe = factory.CreateClient();
        stripe.DefaultRequestHeaders.Remove("X-Session-Id");
        var payRef = (await Billing(site)).Invoices.Single(i => i.Id == second.Id).PaymentRef!;
        var notice = "{\"id\":\"evt_1\",\"type\":\"payment_intent.succeeded\",\"data\":{\"object\":{\"id\":\"" + payRef + "\",\"object\":\"payment_intent\",\"status\":\"succeeded\"}}}";
        HttpRequestMessage Notify(string secret, DateTimeOffset at)
        {
            var t = at.ToUnixTimeSeconds();
            using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(secret));
            var signature = Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"{t}.{notice}"))).ToLowerInvariant();
            var message = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe") { Content = new StringContent(notice, System.Text.Encoding.UTF8, "application/json") };
            message.Headers.Add("Stripe-Signature", $"t={t},v1={signature}");
            return message;
        }
        async Task<string> StatusOfSecond() => (await Billing(site)).Invoices.Single(i => i.Id == second.Id).Status;
        // Until the signing secret is entered, every notification is refused.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await stripe.SendAsync(Notify(hookSecret, DateTimeOffset.UtcNow))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync("/api/system/stripe/webhook", new StripeWebhookRequest(hookSecret))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await site.PutAsJsonAsync("/api/system/stripe/webhook", new StripeWebhookRequest("not-a-signing-secret-000000"))).StatusCode);
        var hooked = await site.PutAsJsonAsync("/api/system/stripe/webhook", new StripeWebhookRequest(hookSecret));
        Assert.Equal(HttpStatusCode.OK, hooked.StatusCode);
        Assert.DoesNotContain(hookSecret, await hooked.Content.ReadAsStringAsync());
        var withHook = (await site.GetFromJsonAsync<StripeAccountDto>("/api/system/stripe"))!;
        Assert.Equal((true, "HOOK"), (withHook.WebhookConfigured, withHook.WebhookEnding));
        factory.Payments.Settle(payRef);
        // Signed with the wrong secret, or too long ago: refused, and the bill stays as it was.
        Assert.Equal(HttpStatusCode.BadRequest, (await stripe.SendAsync(Notify("whsec_someone-elses-secret-000000", DateTimeOffset.UtcNow))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await stripe.SendAsync(Notify(hookSecret, DateTimeOffset.UtcNow.AddMinutes(-20)))).StatusCode);
        Assert.Equal("processing", await StatusOfSecond());
        Assert.Null((await site.GetFromJsonAsync<StripeAccountDto>("/api/system/stripe"))!.LastNotificationAt);
        // Genuine: the bill is paid, and a repeat of the same notification changes nothing.
        Assert.Equal(HttpStatusCode.OK, (await stripe.SendAsync(Notify(hookSecret, DateTimeOffset.UtcNow))).StatusCode);
        Assert.Equal("paid", await StatusOfSecond());
        Assert.Equal(HttpStatusCode.OK, (await stripe.SendAsync(Notify(hookSecret, DateTimeOffset.UtcNow))).StatusCode);
        Assert.NotNull((await site.GetFromJsonAsync<StripeAccountDto>("/api/system/stripe"))!.LastNotificationAt);
        Assert.True(await Db(db => Task.FromResult(db.AuditLogs.Count(a => a.Action == "MailBillPaymentArrived" && a.GroupId == groupId) == 1)));
        Assert.False(await Db(db => Task.FromResult(db.AuditLogs.ToList().Any(a => (a.Details ?? "").Contains(hookSecret)))));

        // Automatic payment: the hourly round charges the last open bill once, and sees the bank payment arrive.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/billing/auto-pay", new AutoPayRequest(true))).StatusCode);
        factory.Payments.Settle((await Billing(site)).Invoices.Single(i => i.Id == second.Id).PaymentRef!);
        async Task Round()
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<BillingService>().RunScheduledAsync();
        }
        await Round();
        await Round();
        var settled = (await Billing(site)).Invoices.Where(i => i.GroupId == groupId).ToList();
        Assert.Equal("paid", settled.Single(i => i.Id == second.Id).Status);
        Assert.Equal("processing", settled.Single(i => i.Month == Month(3)).Status);
        Assert.Equal(chargesBefore + 4, factory.Payments.Charges.Count);
        Assert.True(await Db(db => Task.FromResult(db.AuditLogs.Count(a => a.Action == "MailBillCharged" && a.GroupId == groupId) >= 3)));

        // Required before mailing: without a payment method the group prints its own cards.
        factory.Payments.Settle(settled.Single(i => i.Month == Month(3)).PaymentRef!);
        await Round();
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync("/api/billing/payment-method")).StatusCode);
        await SaveSettings(site, s => s.RequirePaymentMethod = true);
        try
        {
            var blocked = (await owner.GetFromJsonAsync<PrintConfig>("/api/comments/print-config"))!;
            Assert.True(blocked.MailAvailable && blocked.MailNeedsPayMethod && !blocked.CanMail);
            var card = await Create(owner, Card(rid, "Needs Method"));
            var refused = await owner.PostAsJsonAsync("/api/comments/mail", new MailRequest([card.Id], 1, await Price(owner)));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("payment method", await refused.Content.ReadAsStringAsync());
        }
        finally
        {
            await SaveSettings(site, s => s.RequirePaymentMethod = false);
        }

        // Printed statements need both addresses; with them, a copy is posted once.
        var bad = (await Billing(site)).Settings;
        bad.PrintBills = true; bad.Street = null;
        Assert.Equal(HttpStatusCode.BadRequest, (await site.PutAsJsonAsync("/api/system/billing/settings", bad)).StatusCode);
        await SaveSettings(site, s => { s.PrintBills = true; s.Street = "1 Biller Way"; s.City = "Joliet"; s.State = "IL"; s.Zip = "60431"; });
        try
        {
            var letters = factory.Mail.Letters.Count;
            Assert.Equal(HttpStatusCode.OK, (await site.PostAsync($"/api/system/billing/invoices/{bill.Id}/send", null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await site.PostAsync($"/api/system/billing/invoices/{bill.Id}/send", null)).StatusCode);
            Assert.Equal(letters + 1, factory.Mail.Letters.Count);
            Assert.Equal("5 Ledger Ln", factory.Mail.Letters[^1].Request.To.AddressLine1);
            Assert.Contains(bill.Number, factory.Mail.Letters[^1].Request.Html);
        }
        finally
        {
            await SaveSettings(site, s => s.PrintBills = false);
        }

        // No key ever reaches the audit log, and the group's audit rows never show the fee split.
        Assert.False(await Db(db => Task.FromResult(db.AuditLogs.ToList().Any(a => (a.Details ?? "").Contains("fake-key-for-unit-tests")))));
    }

    [Fact]
    public async Task A_restaurant_can_have_its_own_card_design_and_otherwise_uses_the_groups()
    {
        var (owner, admin, _, rid, groupId) = await SetUpAsync();
        static MultipartFormDataContent Upload(byte[] bytes, string name)
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "file", name);
            return form;
        }
        // A PNG header saying 1875 x 1275 pixels.
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0x07, 0x53, 0, 0, 0x04, 0xFB, 8, 2, 0, 0, 0, 1, 2, 3, 4];

        // An owner designs their own restaurant's card, but not the group's.
        var scopes = (await owner.GetFromJsonAsync<List<CardDesignScope>>("/api/card-designs"))!;
        Assert.False(scopes.Single(s => s.Scope == "group").CanEdit);
        Assert.True(scopes.Single(s => s.RestaurantId == rid).CanEdit);
        var plain = new CardDesignFields("picture", "centered", null, null, null, null, null);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync("/api/card-designs/group", plain)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsync("/api/card-designs/group/images/picture", Upload(png, "front.png"))).StatusCode);

        // The group's card: finished artwork, served publicly so the mail house can download it, only at its exact address.
        var group = (await (await admin.PutAsync("/api/card-designs/group/images/picture", Upload(png, "front.png"))).Content.ReadFromJsonAsync<CardDesignDto>())!;
        var art = group.Images.Single(i => i.Kind == "picture");
        Assert.Equal((1875, 1275), (art.WidthPx, art.HeightPx));
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/card-designs/group", plain)).StatusCode);
        var anonymous = factory.CreateClient();
        Assert.Equal(png, await (await anonymous.GetAsync(art.Url)).Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/card-art/{groupId}/0000000000000000.png")).StatusCode);

        // With no design of its own, the restaurant's cards use the group's.
        var card = await Create(owner, Card(rid, "Artful"));
        var inherited = (await owner.GetFromJsonAsync<MailPreview>($"/api/comments/{card.Id}/mail/preview"))!;
        Assert.Contains(art.Url, inherited.FrontHtml);
        Assert.False((await owner.GetFromJsonAsync<CardDesignDto>($"/api/card-designs/{rid}"))!.HasOwn);

        // Its own design replaces it: words are escaped, and the note appears on the back.
        var mine = new CardDesignFields("designed", "centered", "Thanks <b>so</b> much", "We are glad you told us.", "From all of us at the restaurant", "#112233", "#ffeecc");
        var draft = (await (await owner.PostAsJsonAsync($"/api/card-designs/{rid}/preview", mine)).Content.ReadFromJsonAsync<MailPreview>())!;
        Assert.Contains("Thanks &lt;b&gt;so&lt;/b&gt; much", draft.FrontHtml);
        Assert.Contains(art.Url, inherited.FrontHtml);
        Assert.False((await owner.GetFromJsonAsync<CardDesignDto>($"/api/card-designs/{rid}"))!.HasOwn);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/card-designs/{rid}", mine)).StatusCode);
        var own = (await owner.GetFromJsonAsync<MailPreview>($"/api/comments/{card.Id}/mail/preview"))!;
        Assert.Contains("Thanks &lt;b&gt;so&lt;/b&gt; much", own.FrontHtml);
        Assert.Contains("#112233", own.FrontHtml);
        Assert.DoesNotContain(art.Url, own.FrontHtml);
        Assert.Contains("From all of us at the restaurant", own.BackHtml);

        // The back is the classic coupon card unless another style is chosen: the restaurant's name with its state, its
        // address, the fill-in lines, the standard small print, all in the usual dark blue, and no web address until one is set.
        Assert.Contains(", IL\n1 Test St\nJoliet, IL 60431</div>", own.BackHtml);
        Assert.Contains("Please enjoy:", own.BackHtml);
        Assert.Contains("Free &lt;Concrete&gt; Mixer", own.BackHtml);
        Assert.Contains("the next time you visit us at ", own.BackHtml);
        Assert.Contains("Date Issued:", own.BackHtml);
        Assert.Contains("from date of issue.", own.BackHtml);
        Assert.Contains("Authorized Signature:", own.BackHtml);
        Assert.Contains("M. Smith", own.BackHtml);
        Assert.Contains("with authorized signature.", own.BackHtml);
        Assert.Contains(CardBacks.DefaultInk, own.BackHtml);
        Assert.DoesNotContain("culvers.com", own.BackHtml);
        // The typefaces travel inside the card itself, so the print matches the preview.
        Assert.Contains("@font-face{font-family:'Montserrat'", own.BackHtml);
        Assert.Contains("src:url(data:font/woff2;base64,", own.BackHtml);
        Assert.DoesNotContain("Good until", own.BackHtml);

        // Its own logo, corner picture, web address, ink and small print.
        var backLogo = (await (await owner.PutAsync($"/api/card-designs/{rid}/images/backlogo", Upload(png, "logo.png"))).Content.ReadFromJsonAsync<CardDesignDto>())!.Images.Single(i => i.Kind == "backlogo");
        byte[] icons = [.. png, 9];
        var footer = (await (await owner.PutAsync($"/api/card-designs/{rid}/images/footer", Upload(icons, "icons.png"))).Content.ReadFromJsonAsync<CardDesignDto>())!.Images.Single(i => i.Kind == "footer");
        var classic = mine with { BackStyle = "classic", InkColor = "#123456", Website = "culvers.com", LegalText = "One per guest. <No> cash value." };
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/card-designs/{rid}", classic)).StatusCode);
        var saved = (await owner.GetFromJsonAsync<CardDesignDto>($"/api/card-designs/{rid}"))!;
        Assert.Equal(("classic", "#123456", "culvers.com", "One per guest. <No> cash value."), (saved.BackStyle, saved.InkColor, saved.Website, saved.LegalText));
        Assert.Contains("with authorized signature.", saved.DefaultLegalText);
        var styled = (await owner.GetFromJsonAsync<MailPreview>($"/api/comments/{card.Id}/mail/preview"))!.BackHtml;
        Assert.Contains(backLogo.Url, styled);
        Assert.Contains(footer.Url, styled);
        Assert.Contains("#123456", styled);
        Assert.DoesNotContain(CardBacks.DefaultInk, styled);
        Assert.Equal(2, styled.Split("culvers.com").Length - 1);
        Assert.Contains("One per guest. &lt;No&gt; cash value.", styled);
        Assert.DoesNotContain("with authorized signature.", styled);

        // The simple style is the plain coupon box, with the date it is good until.
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/card-designs/{rid}", classic with { BackStyle = "simple" })).StatusCode);
        var simple = (await owner.GetFromJsonAsync<MailPreview>($"/api/comments/{card.Id}/mail/preview"))!.BackHtml;
        Assert.Contains("Good until", simple);
        Assert.DoesNotContain("Authorized Signature:", simple);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/card-designs/{rid}", classic with { BackStyle = "fancy" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/card-designs/{rid}", classic with { InkColor = "blue" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/card-designs/{rid}", classic with { LegalText = new string('x', 421) })).StatusCode);

        // What cannot fit or is not a picture is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/card-designs/{rid}", mine with { Headline = new string('x', 61) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/card-designs/{rid}", mine with { BackgroundColor = "red;}" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsync($"/api/card-designs/{rid}/images/logo", Upload("not an image"u8.ToArray(), "notes.txt"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/card-designs/999999")).StatusCode);

        // Removing its own design puts the restaurant back on the group's; removing the group's picture stops serving it.
        Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync($"/api/card-designs/{rid}")).StatusCode);
        Assert.Contains(art.Url, (await owner.GetFromJsonAsync<MailPreview>($"/api/comments/{card.Id}/mail/preview"))!.FrontHtml);
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync("/api/card-designs/group")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(art.Url)).StatusCode);
    }

    [Fact]
    public async Task A_card_can_be_laid_out_item_by_item_in_the_card_designer()
    {
        var (owner, _, _, rid, _) = await SetUpAsync();
        static MultipartFormDataContent Upload(byte[] bytes)
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "file", "art.png");
            return form;
        }
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0x01, 0x00, 0, 0, 0x00, 0x80, 8, 2, 0, 0, 0, 5, 6, 7, 8];
        async Task<CardDesignDto> Get() => (await owner.GetFromJsonAsync<CardDesignDto>($"/api/card-designs/{rid}"))!;
        async Task<HttpStatusCode> Put(CardDesignFields f) => (await owner.PutAsJsonAsync($"/api/card-designs/{rid}", f)).StatusCode;

        // The designer starts from the classic card as a list of items, and knows the fields words can carry.
        var start = await Get();
        Assert.Contains(start.StandardBack, e => e is { Type: "fill", Value: "{offer}" });
        Assert.Contains(start.StandardBack, e => e is { Type: "image", Image: "backlogo" });
        Assert.Contains(start.StandardBack, e => e.Type == "qr");
        Assert.All(start.StandardBack, e => Assert.True(e.X + e.W <= CardElements.AddressFromX, $"{e.Id} reaches into the address area"));
        Assert.NotEmpty(start.StarterFront);
        Assert.Equal("1 Test St", start.MergeFields.Single(f => f.Key == "street").Sample);
        Assert.NotEqual("", start.MergeFields.Single(f => f.Key == "offer").Sample);
        Assert.StartsWith("data:image/svg+xml;base64,", start.SampleQr);

        // A back of its own: words with fields, a fill-in line, a rule, a box, a picture of its own and the QR code.
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsync($"/api/card-designs/{rid}/images/el-abc123", Upload(png))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsync($"/api/card-designs/{rid}/images/el-NOPE!", Upload(png))).StatusCode);
        var art = (await Get()).Images.Single(i => i.Kind == "el-abc123");
        List<CardElement> back =
        [
            new() { Id = "a", Type = "text", X = 1, Y = 0.5, W = 2, Font = "geometric", Size = 12, Bold = true, Text = "<b>{restaurant}</b> & friends\n{website}\n{street}" },
            new() { Id = "b", Type = "fill", X = 0.3, Y = 1.2, W = 3, Text = "Enjoy:", Value = "{offer}", LineUnderLabel = true },
            new() { Id = "c", Type = "line", X = 0.3, Y = 2, W = 3, Thickness = 2, Color = "#aa0000" },
            new() { Id = "d", Type = "box", X = 0.3, Y = 2.2, W = 1, H = 0.5 },
            new() { Id = "e", Type = "image", X = 0.3, Y = 3, W = 1, H = 0.4, Image = "el-abc123" },
            new() { Id = "f", Type = "qr", X = 2.9, Y = 2.2, W = 0.6 },
            // Tidied on the way in: kept on the card, an unknown typeface and colour put back to safe ones, a repeated id replaced.
            new() { Id = "a", Type = "text", X = 99, Y = -4, W = 50, Font = "comic", Size = 900, Color = "red;}</style>", Text = "</div><script>alert(1)</script>" }
        ];
        var custom = new CardDesignFields("plain", "centered", null, null, null, null, null, BackStyle: "custom", InkColor: "#123456", BackLayout: back);
        Assert.Equal(HttpStatusCode.OK, await Put(custom));
        var saved = await Get();
        Assert.Equal("custom", saved.BackStyle);
        Assert.Equal(7, saved.BackLayout!.Count);
        var tidied = saved.BackLayout[6];
        Assert.Equal((5.95, 0d, 6d, "sans", 60d, "ink"), (tidied.X, tidied.Y, tidied.W, tidied.Font, tidied.Size, tidied.Color));
        Assert.NotEqual("a", tidied.Id);
        Assert.Equal(7, saved.BackLayout.Select(e => e.Id).Distinct().Count());

        // The mailed card is drawn from those items: each at its place (an eighth of an inch in, for the bleed), fields
        // filled in for this card, every word escaped, an empty field's line left out, the picture and this card's QR code.
        var card = await Create(owner, Card(rid, "Layout"));
        var html = (await owner.GetFromJsonAsync<MailPreview>($"/api/comments/{card.Id}/mail/preview"))!.BackHtml;
        Assert.Contains("left:1.125in;top:0.625in;width:2in", html);
        Assert.Contains("&lt;b&gt;", html);
        Assert.Contains("&lt;/b&gt; &amp; friends\n1 Test St</div>", html);
        Assert.Contains("Enjoy:", html);
        Assert.Contains("Free &lt;Concrete&gt; Mixer", html);
        Assert.Contains("border-top:2pt solid #aa0000", html);
        Assert.Contains("#123456", html);
        Assert.Contains(art.Url, html);
        Assert.Contains("data:image/svg+xml;base64,", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("red;}", html);
        Assert.DoesNotContain("Authorized Signature:", html);
        Assert.Contains("@font-face{font-family:'Montserrat'", html);

        // The preview draws a layout that has not been saved, and refuses one that could not be.
        var draft = custom with { BackLayout = [new() { Id = "z", Type = "text", X = 0.5, Y = 0.5, W = 2, Text = "Only a draft for {guest}" }] };
        var preview = (await (await owner.PostAsJsonAsync($"/api/card-designs/{rid}/preview", draft)).Content.ReadFromJsonAsync<MailPreview>())!;
        Assert.Contains("Only a draft for Pat Sample", preview.BackHtml);
        Assert.Equal(7, (await Get()).BackLayout!.Count);

        // A front laid out the same way, on its own colours.
        var front = custom with { Front = "custom", BackgroundColor = "#101820", TextColor = "#fedcba", FrontLayout = [new() { Id = "h", Type = "text", X = 0.5, Y = 1.5, W = 5, Size = 28, Align = "center", Text = "Thank you, {guest}!" }] };
        Assert.Equal(HttpStatusCode.OK, await Put(front));
        var printed = (await owner.GetFromJsonAsync<MailPreview>($"/api/comments/{card.Id}/mail/preview"))!.FrontHtml;
        Assert.Contains("Thank you, Grid Layout!", printed);
        Assert.Contains("background:#101820", printed);
        Assert.Contains("color:#fedcba", printed);

        // What cannot be printed is refused: a kind of item the site does not know, too many items, a side with nothing on it, a picture with none chosen.
        Assert.Equal(HttpStatusCode.BadRequest, await Put(custom with { BackLayout = [new() { Id = "x", Type = "video", W = 1 }] }));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(custom with { BackLayout = Enumerable.Range(0, CardElements.Max + 1).Select(i => new CardElement { Id = $"n{i}", Type = "line", W = 1 }).ToList() }));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(custom with { BackLayout = [] }));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(custom with { BackLayout = [new() { Id = "p", Type = "image", W = 1, H = 1, Image = "../secret" }] }));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(custom with { BackLayout = [new() { Id = "t", Type = "text", W = 1, Text = new string('x', 601) }] }));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(custom with { Front = "custom", FrontLayout = null }));

        // A picture no item uses any more is removed, and stops being served.
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(art.Url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await Put(custom with { BackLayout = back.Where(e => e.Type != "image").ToList() }));
        Assert.DoesNotContain((await Get()).Images, i => i.Kind == "el-abc123");
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(art.Url)).StatusCode);

        // The designer in the browser gets the same typeface files, and only those.
        var font = await anonymous.GetAsync("/card-fonts/arimo-latin-400-normal.woff2");
        Assert.Equal(HttpStatusCode.OK, font.StatusCode);
        Assert.Equal("font/woff2", font.Content.Headers.ContentType?.MediaType);
        Assert.Equal("wOF2"u8.ToArray(), (await font.Content.ReadAsByteArrayAsync())[..4]);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/card-fonts/appsettings.json")).StatusCode);

        // Saving is in the audit log, without the words themselves.
        var row = await Db(db => Task.FromResult(db.AuditLogs.Where(a => a.Action == "CardDesignSaved").OrderByDescending(a => a.Id).First()));
        Assert.Contains("backItems", row.Details ?? "");
        Assert.DoesNotContain("alert(1)", row.Details ?? "");

        Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync($"/api/card-designs/{rid}")).StatusCode);
    }

    [Fact]
    public async Task A_card_is_signed_by_the_manager_who_enters_it_or_by_the_name_set_for_the_team_login()
    {
        var (owner, _, _, rid, _) = await SetUpAsync();
        var r = (await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants"))!.Single(x => x.Id == rid);
        var ownerName = (await owner.GetFromJsonAsync<UserDto>("/api/auth/me"))!.DisplayName;
        async Task<string> Back(int id) => (await owner.GetFromJsonAsync<MailPreview>($"/api/comments/{id}/mail/preview"))!.BackHtml;
        CommentRequest Unsigned(string last) { var c = Card(rid, last); c.AuthorizedBy = null; return c; }

        // A manager and the shared team login for the restaurant, and a manager who works somewhere else.
        var n = Interlocked.Increment(ref _n);
        var other = (await (await owner.PostAsJsonAsync("/api/restaurants", new RestaurantRequest { Name = $"Elsewhere {n}" })).Content.ReadFromJsonAsync<RestaurantDto>())!;
        foreach (var u in new[]
                 {
                     new UserRequest { Username = $"sig-mgr-{n}", DisplayName = "Morgan Manager", Password = "Manager!123", Role = "Manager", Permissions = ["ViewComments"], RestaurantIds = [rid] },
                     new UserRequest { Username = $"sig-away-{n}", DisplayName = "Avery Away", Password = "Manager!123", Role = "Manager", RestaurantIds = [other.Id] },
                     new UserRequest { Username = $"sig-team-{n}", DisplayName = "Front Counter", Password = "TeamPass!123", Role = "TeamMember", RestaurantIds = [rid] }
                 })
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/users", u)).StatusCode);
        await Db(async db =>
        {
            foreach (var u in db.Users.Where(u => u.Username.StartsWith("sig-") && u.Username.EndsWith($"-{n}"))) u.MustChangePassword = false;
            return await db.SaveChangesAsync();
        });
        var manager = await factory.LoginAsync($"sig-mgr-{n}", "Manager!123");
        var away = await factory.LoginAsync($"sig-away-{n}", "Manager!123");
        var team = await factory.LoginAsync($"sig-team-{n}", "TeamPass!123");

        // With no name typed: a manager's or owner's card is signed with their own name, and it prints in the signature hand.
        var byManager = await Create(manager, Unsigned("ByManager"));
        Assert.Equal("Morgan Manager", byManager.AuthorizedBy);
        var byOwner = await Create(owner, Unsigned("ByOwner"));
        Assert.Equal(ownerName, byOwner.AuthorizedBy);
        var printed = await Back(byManager.Id);
        Assert.Contains("Morgan Manager", printed);
        Assert.Contains("@font-face{font-family:'Great Vibes'", printed);
        Assert.Contains("font-family:'Great Vibes'", printed[printed.IndexOf("Authorized Signature:", StringComparison.Ordinal)..]);

        // The team login is shared, so its cards are not signed with its name. Until a name is set for the restaurant they carry none.
        var unsigned = await Create(team, Unsigned("NoNameYet"));
        Assert.True(string.IsNullOrEmpty(unsigned.AuthorizedBy));

        // A manager who works at the restaurant sets that name; a team login, or a manager from another restaurant, cannot.
        Assert.Equal(HttpStatusCode.Forbidden, (await team.PutAsJsonAsync($"/api/restaurants/{rid}/signature", new SignatureNameRequest("Team Try"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await away.PutAsJsonAsync($"/api/restaurants/{rid}/signature", new SignatureNameRequest("Away Try"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PutAsJsonAsync($"/api/restaurants/{rid}/signature", new SignatureNameRequest(new string('x', 61)))).StatusCode);
        var set = (await (await manager.PutAsJsonAsync($"/api/restaurants/{rid}/signature", new SignatureNameRequest("  Michael <Smith>  "))).Content.ReadFromJsonAsync<RestaurantDto>())!;
        Assert.Equal("Michael <Smith>", set.SignatureName);
        // Everyone who enters cards there is told the name, so the card form can show it.
        Assert.Equal("Michael <Smith>", (await team.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants"))!.Single(x => x.Id == rid).SignatureName);

        // From then on the team login's cards are signed with it, unless another name is typed on the card.
        var byTeam = await Create(team, Unsigned("ByTeam"));
        Assert.Equal("Michael <Smith>", byTeam.AuthorizedBy);
        Assert.Contains("Michael &lt;Smith&gt;", await Back(byTeam.Id));
        var typed = await Create(team, Card(rid, "Typed"));
        Assert.Equal("M. Smith", typed.AuthorizedBy);
        Assert.Contains("M. Smith", await Back(typed.Id));
        // A manager's own cards still carry the manager's name, and a card that had none prints the restaurant's.
        Assert.Equal("Morgan Manager", (await Create(manager, Unsigned("StillManager"))).AuthorizedBy);
        Assert.Contains("Michael &lt;Smith&gt;", await Back(unsigned.Id));
        // The card designer's sample is signed the same way, and so is the simple back.
        Assert.Equal("Michael <Smith>", (await owner.GetFromJsonAsync<CardDesignDto>($"/api/card-designs/{rid}"))!.MergeFields.Single(f => f.Key == "authorizedBy").Sample);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/card-designs/{rid}", new CardDesignFields("plain", "centered", null, null, null, null, null, BackStyle: "simple"))).StatusCode);
        Assert.Contains("Michael &lt;Smith&gt;", await Back(byTeam.Id));
        Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync($"/api/card-designs/{rid}")).StatusCode);

        // An owner can set it from the restaurant's own form too, and clear it. Both ways are in the audit log.
        RestaurantRequest With(string? signature) => new()
        {
            Name = r.Name, StreetAddress = r.StreetAddress, City = r.City, State = r.State, Zip = r.Zip, Phone = r.Phone, SignatureName = signature
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/restaurants/{rid}", With(new string('x', 61)))).StatusCode);
        Assert.Equal("Pat Owner", (await (await owner.PutAsJsonAsync($"/api/restaurants/{rid}", With("Pat Owner"))).Content.ReadFromJsonAsync<RestaurantDto>())!.SignatureName);
        Assert.Null((await (await owner.PutAsJsonAsync($"/api/restaurants/{rid}", With(" "))).Content.ReadFromJsonAsync<RestaurantDto>())!.SignatureName);
        Assert.True(await Db(db => Task.FromResult(db.AuditLogs.Any(a => a.Action == "RestaurantSignatureChanged" && a.RestaurantId == rid && a.Details!.Contains("Michael")))));
        Assert.True(await Db(db => Task.FromResult(db.AuditLogs.Any(a => a.Action == "RestaurantUpdated" && a.RestaurantId == rid && a.Details!.Contains("Pat Owner")))));
        // Other tests expect the owner to have a single restaurant, so the one made for the manager from elsewhere is taken away again.
        await Db(async db =>
        {
            db.Restaurants.Remove((await db.Restaurants.FindAsync(other.Id))!);
            return await db.SaveChangesAsync();
        });

        // In the designer, what is filled in on a line can have its own typeface and size; anything unknown is put back to the label's.
        List<CardElement> layout =
        [
            new() { Id = "s", Type = "fill", X = 0.3, Y = 2, W = 3, Text = "Signed:", Value = "{authorizedBy}", ValueFont = "script", ValueSize = 18 },
            new() { Id = "t", Type = "fill", X = 0.3, Y = 2.5, W = 3, Text = "Date:", Value = "{dateIssued}", ValueFont = "wingdings", ValueSize = 900 },
            new() { Id = "u", Type = "text", X = 0.3, Y = 3, W = 3, Text = "words", ValueFont = "script", ValueSize = 20 }
        ];
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/card-designs/{rid}", new CardDesignFields("plain", "centered", null, null, null, null, null, BackStyle: "custom", BackLayout: layout))).StatusCode);
        var kept = (await owner.GetFromJsonAsync<CardDesignDto>($"/api/card-designs/{rid}"))!.BackLayout!;
        Assert.Equal(("script", 18d), (kept[0].ValueFont, kept[0].ValueSize));
        Assert.Equal((null, 60d), (kept[1].ValueFont, kept[1].ValueSize));
        Assert.Equal((null, 0d), (kept[2].ValueFont, kept[2].ValueSize));
        Assert.Contains("font-family:'Great Vibes','Snell Roundhand', 'Brush Script MT', cursive;font-size:18pt", await Back(byTeam.Id));
        Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync($"/api/card-designs/{rid}")).StatusCode);
    }

    private static AddressCheck Read(string json, string street = "123 Main St", string city = "Joliet", string zip = "60431") =>
        PostGridAddressVerifier.Read(JsonDocument.Parse(json).RootElement, new AddressInput(street, city, "IL", zip));

    [Fact]
    public void PostGrid_address_answers_are_read_as_verified_corrected_needs_unit_or_not_found()
    {
        Assert.Equal(AddressStatuses.Verified, Read("""{"status":"success","data":{"status":"verified","line1":"123 Main St","city":"Joliet","provinceOrState":"IL","postalOrZip":"60431","zipPlus4":"1234","errors":{}}}""").Status);

        var corrected = Read("""{"status":"success","data":{"status":"corrected","line1":"123 MAIN ST","city":"JOLIET","provinceOrState":"IL","postalOrZip":"60432","zipPlus4":"5678","errors":{"city":["Corrected"]}}}""");
        Assert.Equal(AddressStatuses.Corrected, corrected.Status);
        Assert.Equal("60432-5678", corrected.Suggested!.Zip);

        var unit = Read("""{"status":"success","data":{"status":"failed","line1":"500 Tower Rd","city":"Joliet","provinceOrState":"IL","postalOrZip":"60431","errors":{"line2":["Missing secondary unit number"]}}}""");
        Assert.Equal(AddressStatuses.NeedsUnit, unit.Status);

        var missing = Read("""{"status":"success","data":{"status":"failed","errors":{"line1":["Could not find the street"]}}}""");
        Assert.Equal(AddressStatuses.NotFound, missing.Status);
        Assert.Contains("Could not find the street", missing.Message);

        Assert.Equal(AddressStatuses.Unavailable, Read("""{"status":"error","message":"Invalid request"}""").Status);
    }

    [Fact]
    public void Card_artwork_sizes_are_read_from_PNG_and_JPEG_headers()
    {
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0x07, 0x53, 0, 0, 0x04, 0xFB, 8, 2, 0, 0, 0];
        Assert.Equal(("image/png", 1875, 1275), ImageInfo.Read(png));
        // SOI, an APP0 segment, then a baseline frame header (SOF0) with height 1275 and width 1875.
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x04, 0xFB, 0x07, 0x53, 0x03, 0, 0, 0, 0];
        Assert.Equal(("image/jpeg", 1875, 1275), ImageInfo.Read(jpeg));
        Assert.Null(ImageInfo.Read("GIF89a......"u8.ToArray()));
    }

    [Fact]
    public void Messages_for_groups_never_name_the_mail_house()
    {
        Assert.Equal("The mailing service refused it (422: bad address).", MailStatuses.ForClients("PostGrid refused it (422: bad address)."));
        Assert.Equal("Ask about the mailing service's answer. The mailing service was busy.", MailStatuses.ForClients("Ask about PostGrid's answer. PostGrid was busy."));
    }

    [Fact]
    public void Stripe_answers_are_read_as_saved_methods_and_payment_states()
    {
        static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;
        var card = StripePayments.Read(J("""{"id":"pm_1","type":"card","card":{"brand":"visa","last4":"4242","funding":"credit"}}"""), null)!;
        Assert.Equal((PayKinds.Card, "credit", "Visa credit card ending 4242"), (card.Kind, card.Funding, card.Label));
        var bank = StripePayments.Read(J("""{"id":"pm_2","type":"us_bank_account","us_bank_account":{"bank_name":"STRIPE TEST BANK","last4":"6789"}}"""), "mandate_9")!;
        Assert.Equal((PayKinds.Bank, "STRIPE TEST BANK account ending 6789", "mandate_9"), (bank.Kind, bank.Label, bank.MandateId));
        Assert.Null(StripePayments.Read(J("""{"id":"pm_3","type":"cashapp"}"""), null));

        Assert.Equal(PayStates.Succeeded, StripePayments.State(J("""{"id":"pi_1","status":"succeeded"}""")).Status);
        Assert.Equal(PayStates.Processing, StripePayments.State(J("""{"id":"pi_2","status":"processing"}""")).Status);
        var declined = StripePayments.State(J("""{"error":{"message":"Your card was declined.","payment_intent":{"id":"pi_3","status":"requires_payment_method"}}}"""));
        Assert.Equal((PayStates.Failed, "pi_3", "Your card was declined."), (declined.Status, declined.Id, declined.Error));
        Assert.Equal(PayStates.Failed, StripePayments.State(J("""{"id":"pi_4","status":"requires_action"}""")).Status);

        // A notification is believed only with a matching signature made within five minutes; either shape names its payment.
        const string secret = "whsec_fake-secret-for-unit-tests";
        const string body = "{\"type\":\"payment_intent.succeeded\",\"data\":{\"object\":{\"id\":\"pi_9\",\"object\":\"payment_intent\"}}}";
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        string Sign(string key, long t)
        {
            using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(key));
            return Convert.ToHexString(hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"{t}.{body}"))).ToLowerInvariant();
        }
        var t0 = now.ToUnixTimeSeconds();
        Assert.True(StripeWebhooks.Verify(body, $"t={t0},v1={Sign(secret, t0)}", secret, now));
        Assert.True(StripeWebhooks.Verify(body, $"t={t0},v1={Sign("whsec_an-old-secret", t0)},v1={Sign(secret, t0)}", secret, now));
        Assert.False(StripeWebhooks.Verify(body + " ", $"t={t0},v1={Sign(secret, t0)}", secret, now));
        Assert.False(StripeWebhooks.Verify(body, $"t={t0},v1={Sign("whsec_someone-else", t0)}", secret, now));
        Assert.False(StripeWebhooks.Verify(body, $"t={t0},v1={Sign(secret, t0)}", secret, now.AddMinutes(6)));
        Assert.False(StripeWebhooks.Verify(body, $"t={t0},v0={Sign(secret, t0)}", secret, now));
        Assert.False(StripeWebhooks.Verify(body, "", secret, now));
        Assert.Equal("pi_9", StripeWebhooks.PaymentId(body));
        Assert.Equal("pi_7", StripeWebhooks.PaymentId("{\"type\":\"v1.payment_intent.succeeded\",\"related_object\":{\"id\":\"pi_7\",\"type\":\"payment_intent\"}}"));
        Assert.Null(StripeWebhooks.PaymentId("{\"type\":\"customer.created\",\"data\":{\"object\":{\"id\":\"cus_1\",\"object\":\"customer\"}}}"));
        Assert.Null(StripeWebhooks.PaymentId("not json"));

        // The bank account fee is 0.08% unless the System Administrator changes it, and survives being saved.
        Assert.Equal(0.08m, new BillingSettings().BankFeePercent);
        Assert.Equal(0.08m, new BillingSettings { BankFeePercent = 0.08m }.Normalize().BankFeePercent);
    }
}
