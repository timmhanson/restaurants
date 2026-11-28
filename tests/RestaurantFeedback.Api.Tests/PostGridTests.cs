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
        // Mailing is off for a group until the System Administrator switches it on, and a group starts in test. Each
        // test here works with one key: the group mails for real with the live key and as a test with the test key.
        var live = !key.StartsWith("test_");
        Assert.Equal(HttpStatusCode.OK, (await site.DeleteAsync($"/api/system/postgrid/mail?mode={(live ? "test" : "live")}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/billing/groups/{groupId}", new GroupBillingTermsRequest(null, null, true))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/groups/{groupId}/settings",
            new GroupSettingsRequest("on", live ? "live" : "test", "each", null, null, null))).StatusCode);
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
        Assert.True(dto.MailTestKey.Configured && dto.MailTestKey.Test);
        Assert.Equal("wxyz", dto.MailTestKey.KeyEnding);
        Assert.True((await site.PostAsync("/api/system/postgrid/mail/test?mode=test", null)).IsSuccessStatusCode);

        var (stored, audited) = await Db(async db => ((await db.SiteSettings.FindAsync("postgrid.mail.test"))!.Value, db.AuditLogs.ToList().Any(a => (a.Details ?? "").Contains(TestKey))));
        Assert.DoesNotContain(TestKey, stored);
        Assert.False(audited);

        // Out of the box a group cannot send anything to the mail house, test orders included, until it has saved a
        // payment method; the System Administrator can switch that requirement off.
        Assert.True(new BillingSettings().RequirePaymentMethod);
        await SaveSettings(site, s => s.RequirePaymentMethod = true);
        // The group is switched on and mails as tests, with only the test key on the site.
        var groupId = (await owner.GetFromJsonAsync<UserDto>("/api/auth/me"))!.GroupId!.Value;
        Assert.Equal(HttpStatusCode.OK, (await site.DeleteAsync("/api/system/postgrid/mail?mode=live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/groups/{groupId}/settings", new GroupSettingsRequest("on", "test", "each", null, null, null))).StatusCode);
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
        // Sent, not replied: only a person records a reply.
        Assert.True(after.SentAt is not null && !after.Responded && after.MailTest);
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
        Assert.Null(reopened.SentAt);
        Assert.Contains(card.Id, (await owner.GetFromJsonAsync<PagedResult<CommentSummary>>("/api/comments?status=not-sent&pageSize=500"))!.Items.Select(c => c.Id));
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

        // Everything Stripe sent is kept, once: the repeat is counted on the same row, and what was refused is not kept at all.
        var kept = await Db(db => Task.FromResult(db.StripeEvents.ToList()));
        var arrived = Assert.Single(kept);
        Assert.Equal(("evt_1", "payment_intent.succeeded", 2, second.Id, groupId, "succeeded"), (arrived.EventId, arrived.Type, arrived.Deliveries, arrived.InvoiceId ?? 0, arrived.GroupId ?? 0, arrived.Status));
        Assert.Contains("brought the bill up to date", arrived.Outcome);
        // Only the System Administrator reads the log. Billing shows each notification in plain words, with its bill.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/system/billing/notifications")).StatusCode);
        var listed = Assert.Single((await site.GetFromJsonAsync<List<PaymentNotificationDto>>($"/api/system/billing/notifications?groupId={groupId}"))!);
        Assert.Equal(("Payment arrived", second.Number, false), (listed.Label, listed.Bill, listed.NeedsALook));
        Assert.Equal("evt_1", Assert.Single((await Billing(site)).Invoices.Single(i => i.Id == second.Id).Notifications).EventId);
        // Every data point in it can be looked up, and looking is audited.
        var detail = (await site.GetFromJsonAsync<PaymentNotificationDetail>($"/api/system/billing/notifications/{listed.Id}"))!;
        Assert.Contains(detail.DataPoints, d => d is { Name: "data.object.status", Value: "succeeded" });
        Assert.Contains(detail.DataPoints, d => d is { Name: "id", Value: "evt_1" });
        Assert.Equal(5, detail.DataPoints.Count);
        Assert.True(await Db(db => Task.FromResult(db.AuditLogs.Any(a => a.Action == "PaymentNotificationRead")) ));
        Assert.True(await Db(db => Task.FromResult(db.AuditLogs.Count(a => a.Action == "PaymentNotificationReceived" && a.GroupId == groupId) == 1)));

        // A refund reported by Stripe is kept against the same bill and marked as needing a look.
        notice = "{\"id\":\"evt_2\",\"type\":\"charge.refunded\",\"created\":1795000000,\"livemode\":false,\"data\":{\"object\":{\"id\":\"ch_1\",\"object\":\"charge\",\"amount\":291,\"amount_refunded\":100,"
            + "\"currency\":\"usd\",\"payment_intent\":\"" + payRef + "\",\"status\":\"succeeded\",\"refunds\":{\"data\":[{\"id\":\"re_1\",\"amount\":100}]}}}}";
        Assert.Equal(HttpStatusCode.OK, (await stripe.SendAsync(Notify(hookSecret, DateTimeOffset.UtcNow))).StatusCode);
        var look = Assert.Single((await site.GetFromJsonAsync<List<PaymentNotificationDto>>("/api/system/billing/notifications?kind=look"))!);
        Assert.Equal(("Payment refunded", 100, "usd", second.Number, true, true), (look.Label, look.AmountCents, look.Currency, look.Bill, look.NeedsALook, look.Test));
        Assert.Contains("refund", (await site.GetFromJsonAsync<SystemOverview>("/api/system/overview"))!.Attention.Select(a => a.Title).FirstOrDefault(x => x.Contains("refund")) ?? "");
        Assert.Contains((await site.GetFromJsonAsync<PaymentNotificationDetail>($"/api/system/billing/notifications/{look.Id}"))!.DataPoints, d => d is { Name: "data.object.refunds.data[0].amount", Value: "100" });
        Assert.Equal("paid", await StatusOfSecond());
        await Db(async db => { db.StripeEvents.RemoveRange(db.StripeEvents); return await db.SaveChangesAsync(); });

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
    public async Task The_System_Administrator_gives_a_group_credit_with_details_and_it_comes_off_its_bills()
    {
        var (owner, admin, site, rid, groupId) = await SetUpAsync(LiveKey);
        await SaveSettings(site, s => { s.CostCents = 90; s.FeeCents = 10; s.EmailBills = false; s.PrintBills = false; s.RequirePaymentMethod = false; });
        Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/billing/groups/{groupId}", new GroupBillingTermsRequest(null, 1_000_000))).StatusCode);
        // Start from a group with nothing waiting and no credit, whatever the tests before left.
        await Db(async db =>
        {
            foreach (var i in db.MailInvoices.Where(i => i.GroupId == groupId && i.Status == InvoiceStatuses.Open)) i.Status = InvoiceStatuses.Void;
            db.GroupCredits.RemoveRange(db.GroupCredits.Where(c => c.GroupId == groupId));
            foreach (var c in db.Comments.Where(c => c.Restaurant.GroupId == groupId && c.PrintFeeCents != null)) { c.PrintFeeCents = null; c.PrintFeeAt = null; }
            return await db.SaveChangesAsync();
        });
        var now = DateTime.UtcNow;
        string Month(int back) => MailLedger.MonthKey(MailLedger.MonthStart(now).AddMonths(-back));
        var mine = new List<int>();
        async Task MailIn(int back, int cards)
        {
            var ids = new List<int>();
            for (var i = 0; i < cards; i++) ids.Add((await Create(owner, Card(rid, $"Credit {back} {i}"))).Id);
            Assert.All(await Mail(owner, ids.ToArray()), o => Assert.True(o.Ok));
            mine.AddRange(ids);
            await Db(async db =>
            {
                foreach (var c in db.Comments.Where(c => ids.Contains(c.Id))) c.MailedAt = MailLedger.MonthStart(now).AddMonths(-back).AddDays(2);
                return await db.SaveChangesAsync();
            });
        }
        async Task<GroupBillingRow> Row() => (await Billing(site)).Groups.Single(g => g.GroupId == groupId);
        async Task<SystemInvoiceDto> Bill(int back) => (await Billing(site)).Invoices.Single(i => i.GroupId == groupId && i.Month == Month(back));
        async Task<List<GroupCreditDto>> Log() => (await site.GetFromJsonAsync<SystemBillingDto>($"/api/system/billing?groupId={groupId}"))!.Credits;
        Task<HttpResponseMessage> Add(HttpClient who, int cents, string? kind, string? details, string? key, int? confirm = null) =>
            who.PostAsJsonAsync($"/api/system/billing/groups/{groupId}/credits", new AddCreditRequest(cents, kind, details, confirm ?? cents, key));
        Task<HttpResponseMessage> TakeBack(int cents, string? details, string? key) =>
            site.PostAsJsonAsync($"/api/system/billing/groups/{groupId}/credits/takeback", new TakeBackCreditRequest(cents, details, key));
        try
        {
            // Only the System Administrator gives credit, and every credit needs an amount within the cap, a reason and real details.
            Assert.Equal(HttpStatusCode.Forbidden, (await Add(owner, 250, "bonus", "Welcome to the site", "k-owner")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Add(site, 0, "bonus", "Welcome to the site", "k-a")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Add(site, RestaurantFeedback.Api.Controllers.SystemBillingController.MaxCreditCents + 1, "bonus", "Welcome to the site", "k-b")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Add(site, 250, "bonus", "Welcome to the site", "k-c", confirm: 2500)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Add(site, 250, "gift", "Welcome to the site", "k-d")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Add(site, 250, "bonus", " ok ", "k-e")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Add(site, 250, "bonus", "Welcome to the site", null)).StatusCode);
            Assert.Empty(await Log());

            // A welcome bonus before any bill: it waits as unused credit. Sending the same request twice records it once.
            var key = $"bonus-{Guid.NewGuid():N}";
            Assert.Equal(HttpStatusCode.OK, (await Add(site, 250, "bonus", "Welcome bonus for the first month <b>", key)).StatusCode);
            var again = await Add(site, 250, "bonus", "Welcome bonus for the first month <b>", key);
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Contains("already recorded", await again.Content.ReadAsStringAsync());
            Assert.Equal(250, (await Row()).CreditCents);
            var first = Assert.Single(await Log());
            Assert.Equal((250, "bonus", "Welcome bonus", "Welcome bonus for the first month <b>"), (first.AmountCents, first.Kind, first.Label, first.Details));
            Assert.False(string.IsNullOrWhiteSpace(first.By));

            // The next bill takes the credit by itself: $3.00 of cards, $2.50 of credit, 50 cents still owed.
            await MailIn(5, 3);
            Assert.Equal(HttpStatusCode.OK, (await site.PostAsJsonAsync("/api/system/billing/issue", new IssueBillsRequest(Month(5)))).StatusCode);
            var bill = await Bill(5);
            Assert.Equal((300, 250, 50, "open"), (bill.TotalCents, bill.CreditCents, bill.DueCents, bill.Status));
            Assert.Equal(0, (await Row()).CreditCents);
            Assert.Equal(50, (await Row()).OwedCents);
            Assert.Contains(await Log(), c => c.Kind == "applied" && c.AmountCents == -250 && c.InvoiceNumber == bill.Number);

            // The group's administrators see the credit and where it went, but never the System Administrator's notes.
            var seen = await admin.GetAsync("/api/billing");
            var theirs = (await seen.Content.ReadFromJsonAsync<GroupBillingDto>())!;
            Assert.Equal(50, theirs.OwedCents);
            Assert.Equal((250, 50), (theirs.Invoices.Single(i => i.Id == bill.Id).CreditCents, theirs.Invoices.Single(i => i.Id == bill.Id).DueCents));
            Assert.Equal(2, theirs.Credits.Count);
            Assert.DoesNotContain("first month", await (await admin.GetAsync("/api/billing")).Content.ReadAsStringAsync());
            Assert.Empty((await owner.GetFromJsonAsync<GroupBillingDto>("/api/billing"))!.Credits);

            // A check arrives for more than is owed: the bill is paid with credit and the rest waits for the next bill.
            Assert.Equal(HttpStatusCode.OK, (await Add(site, 500, "check", "Check 1042 dated Nov 3, deposited Nov 5", $"check-{Guid.NewGuid():N}")).StatusCode);
            bill = await Bill(5);
            Assert.Equal(("paid", "credit", 300, 0), (bill.Status, bill.PaidHow, bill.CreditCents, bill.DueCents));
            Assert.Equal(450, (await Row()).CreditCents);
            // There is no payment to undo on a bill the credit paid.
            Assert.Equal(HttpStatusCode.BadRequest, (await site.PostAsync($"/api/system/billing/invoices/{bill.Id}/unpaid", null)).StatusCode);
            await MailIn(4, 2);
            Assert.Equal(HttpStatusCode.OK, (await site.PostAsJsonAsync("/api/system/billing/issue", new IssueBillsRequest(Month(4)))).StatusCode);
            Assert.Equal(("paid", "credit", 200), ((await Bill(4)).Status, (await Bill(4)).PaidHow, (await Bill(4)).CreditCents));
            Assert.Equal(250, (await Row()).CreditCents);

            // Unused credit can be taken back, with a reason; never more than is unused, and never twice for one request.
            Assert.Equal(HttpStatusCode.BadRequest, (await TakeBack(251, "The check was returned by the bank", "t-a")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await TakeBack(100, "no", "t-b")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/system/billing/groups/{groupId}/credits/takeback", new TakeBackCreditRequest(100, "The check was returned", "t-c"))).StatusCode);
            var back = $"back-{Guid.NewGuid():N}";
            Assert.Equal(HttpStatusCode.OK, (await TakeBack(100, "Part of the check was for another group", back)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await TakeBack(100, "Part of the check was for another group", back)).StatusCode);
            Assert.Equal(150, (await Row()).CreditCents);

            // Cancelling a bill returns the credit used on it; putting the bill back uses the credit again; the rest is paid by hand.
            await MailIn(6, 3);
            Assert.Equal(HttpStatusCode.OK, (await site.PostAsJsonAsync("/api/system/billing/issue", new IssueBillsRequest(Month(6)))).StatusCode);
            var last = await Bill(6);
            Assert.Equal((300, 150, 150, "open"), (last.TotalCents, last.CreditCents, last.DueCents, last.Status));
            Assert.Equal(HttpStatusCode.OK, (await site.PostAsync($"/api/system/billing/invoices/{last.Id}/void", null)).StatusCode);
            Assert.Equal(150, (await Row()).CreditCents);
            Assert.Equal(0, (await Bill(6)).CreditCents);
            Assert.Contains(await Log(), c => c.Kind == "returned" && c.AmountCents == 150 && c.InvoiceNumber == last.Number);
            Assert.Equal(HttpStatusCode.OK, (await site.PostAsync($"/api/system/billing/invoices/{last.Id}/unpaid", null)).StatusCode);
            Assert.Equal((150, 150, "open"), ((await Bill(6)).CreditCents, (await Bill(6)).DueCents, (await Bill(6)).Status));
            Assert.Equal(0, (await Row()).CreditCents);
            Assert.Equal(HttpStatusCode.OK, (await site.PostAsJsonAsync($"/api/system/billing/invoices/{last.Id}/paid", new MarkPaidRequest("check", "Check 1050", null))).StatusCode);
            Assert.Equal("paid", (await Bill(6)).Status);

            // The log keeps every line, in order, and adds up to what the group has left. Each step is in the audit log with its details.
            var log = await Log();
            Assert.Equal(0, log.Sum(c => c.AmountCents));
            Assert.Equal(new[] { "applied", "returned", "applied", "takenback", "applied", "applied", "check", "applied", "bonus" }, log.Select(c => c.Kind).ToArray());
            Assert.Empty((await site.GetFromJsonAsync<SystemBillingDto>("/api/system/billing?groupId=999999"))!.Credits);
            var audited = await Db(db => Task.FromResult(db.AuditLogs.Where(a => a.GroupId == groupId).OrderByDescending(a => a.Id).Take(60).ToList()));
            Assert.Contains(audited, a => a.Action == "GroupCreditAdded" && a.Details!.Contains("Check 1042"));
            Assert.Contains(audited, a => a.Action == "GroupCreditTakenBack" && a.Details!.Contains("another group"));
            Assert.Contains(audited, a => a.Action == "MailBillCreditApplied");
            Assert.Contains(audited, a => a.Action == "MailBillMarkedPaid" && a.Details!.Contains("\"paidCents\":150"));
        }
        finally
        {
            // Other tests count this group's bills and cards, so everything made here is taken away again.
            await Db(async db =>
            {
                var months = new[] { Month(4), Month(5), Month(6) };
                db.MailInvoices.RemoveRange(db.MailInvoices.Where(i => i.GroupId == groupId && months.Contains(i.Month)));
                db.GroupCredits.RemoveRange(db.GroupCredits.Where(c => c.GroupId == groupId));
                db.Comments.RemoveRange(db.Comments.Where(c => mine.Contains(c.Id)));
                return await db.SaveChangesAsync();
            });
        }
    }

    [Fact]
    public async Task Settings_are_made_for_all_restaurants_or_left_to_each_restaurant_and_the_higher_level_wins()
    {
        var (owner, admin, site, rid, groupId) = await SetUpAsync();
        async Task<GroupSettingsDto> Settings() => (await site.GetFromJsonAsync<GroupSettingsDto>($"/api/system/groups/{groupId}/settings"))!;
        Task<HttpResponseMessage> Save(string? mail = null, string? mode = null, string? usual = null, string? signature = null, List<RestaurantSettingsRequest>? restaurants = null) =>
            site.PutAsJsonAsync($"/api/system/groups/{groupId}/settings", new GroupSettingsRequest(mail, mode, usual, signature, null, restaurants));
        async Task<RestaurantDto> Mine() => (await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants"))!.Single(x => x.Id == rid);
        async Task<PrintConfig> Config() => (await owner.GetFromJsonAsync<PrintConfig>("/api/comments/print-config"))!;
        var r = await Mine();
        RestaurantRequest Own(bool? mailOn = null, bool? mailLive = null, bool usual = false, string? signature = null) => new()
        {
            Name = r.Name, StreetAddress = r.StreetAddress, City = r.City, State = r.State, Zip = r.Zip, Phone = r.Phone, MailOn = mailOn, MailLive = mailLive, MailByDefault = usual, SignatureName = signature
        };
        try
        {
            // The site holds a test key and a live key at once, each entered on its own.
            Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync("/api/system/postgrid/mail", new PostGridMailKeyRequest(LiveKey))).StatusCode);
            var keys = (await site.GetFromJsonAsync<PostGridSiteDto>("/api/system/postgrid"))!;
            Assert.True(keys.MailTestKey.Configured && keys.MailTestKey.Test);
            Assert.True(keys.MailLiveKey.Configured && !keys.MailLiveKey.Test);
            Assert.NotEqual(keys.MailTestKey.KeyEnding, keys.MailLiveKey.KeyEnding);
            Assert.True((await site.PostAsync("/api/system/postgrid/mail/test?mode=live", null)).IsSuccessStatusCode);
            var start = await Settings();
            Assert.True(start.HasTestKey && start.HasLiveKey);
            Assert.Equal(("on", "test", "each"), (start.Mail, start.MailMode, start.SiteMailMode));
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/system/groups/{groupId}/settings")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Save(mail: "sometimes")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Save(mode: "maybe")).StatusCode);

            // Test for all restaurants: the restaurant is told so, and its own choice is not looked at.
            var applies = (await Mine()).Applies!;
            Assert.Equal((true, false, "group", "group"), (applies.MailOn, applies.MailLive, applies.MailOnSetBy, applies.MailLiveSetBy));
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/restaurants/{rid}", Own(mailLive: true))).StatusCode);
            Assert.False((await Mine()).MailLive);
            Assert.True((await Config()).MailTest);
            var asTest = await Create(owner, Card(rid, "Policy Test"));
            Assert.True((await Mail(owner, [asTest.Id])).Single() is { Ok: true, Test: true });

            // Live for all restaurants: the same card screen now mails for real, with the live key.
            Assert.Equal(HttpStatusCode.OK, (await Save(mode: "live")).StatusCode);
            Assert.True((await Mine()).Applies!.MailLive);
            Assert.False((await Config()).MailTest);
            var asLive = await Create(owner, Card(rid, "Policy Live"));
            Assert.True((await Mail(owner, [asLive.Id])).Single() is { Ok: true, Test: false });
            Assert.StartsWith("live_", factory.Mail.Requests.Last(x => x.Request.To.LastName == "Policy Live").Key);
            Assert.StartsWith("test_", factory.Mail.Requests.Last(x => x.Request.To.LastName == "Policy Test").Key);

            // Each restaurant decides: now the restaurant's own choice counts, and its owner can make it.
            Assert.Equal(HttpStatusCode.OK, (await Save(mode: "each")).StatusCode);
            Assert.Equal((false, "restaurant"), ((await Mine()).Applies!.MailLive, (await Mine()).Applies!.MailLiveSetBy));
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/restaurants/{rid}", Own(mailLive: true))).StatusCode);
            Assert.True((await Mine()).Applies!.MailLive);
            // The System Administrator can set a restaurant's own choice too.
            Assert.Equal(HttpStatusCode.OK, (await Save(restaurants: [new(rid, false, false, false, null)])).StatusCode);
            Assert.False((await Mine()).Applies!.MailLive);

            // The group's administrators choose real or test themselves, unless the site has fixed it for every group.
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync("/api/billing/mail-mode", new MailModeRequest("live"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/billing/mail-mode", new MailModeRequest("sometimes"))).StatusCode);
            var chosen = (await (await admin.PutAsJsonAsync("/api/billing/mail-mode", new MailModeRequest("live"))).Content.ReadFromJsonAsync<GroupBillingDto>())!;
            Assert.Equal(("live", false), (chosen.MailMode, chosen.MailModeSetBySite));
            Assert.Equal("live", (await Settings()).MailMode);
            await SaveSettings(site, s => s.MailMode = "test");
            Assert.Equal((false, "site"), ((await Mine()).Applies!.MailLive, (await Mine()).Applies!.MailLiveSetBy));
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/billing/mail-mode", new MailModeRequest("each"))).StatusCode);
            Assert.True((await admin.GetFromJsonAsync<GroupBillingDto>("/api/billing"))!.MailModeSetBySite);
            await SaveSettings(site, s => s.MailMode = "each");

            // Whether cards are mailed at all: off for the restaurant that has not been switched on, when each restaurant decides.
            Assert.Equal(HttpStatusCode.OK, (await Save(mail: "each")).StatusCode);
            Assert.Equal((false, "restaurant"), ((await Mine()).Applies!.MailOn, (await Mine()).Applies!.MailOnSetBy));
            Assert.Equal("group", (await Config()).MailBlockedBy);
            var waiting = await Create(owner, Card(rid, "Policy Off"));
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/comments/mail", new MailRequest([waiting.Id], 1, await Price(owner)))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/restaurants/{rid}", Own(mailOn: true))).StatusCode);
            Assert.True((await Mine()).Applies!.MailOn);
            Assert.Null((await Config()).MailBlockedBy);
            Assert.Contains((await Config()).MailRestaurants, x => x.RestaurantId == rid && x is { On: true, Ready: true });

            // How cards usually go out, and the name that signs for the team login: one answer for all restaurants, or each decides.
            Assert.Equal(HttpStatusCode.OK, (await Save(usual: "mail", signature: "  Pat Group  ")).StatusCode);
            var fixedForAll = (await Mine()).Applies!;
            Assert.Equal((true, "group", "Pat Group", "group"), (fixedForAll.MailUsual, fixedForAll.MailUsualSetBy, fixedForAll.Signature, fixedForAll.SignatureSetBy));
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/restaurants/{rid}/signature", new SignatureNameRequest("Someone Else"))).StatusCode);
            Assert.Equal("Pat Group", (await owner.GetFromJsonAsync<CardDesignDto>($"/api/card-designs/{rid}"))!.MergeFields.Single(f => f.Key == "authorizedBy").Sample);
            Assert.Equal(HttpStatusCode.OK, (await Save(usual: "each", signature: null)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/restaurants/{rid}", Own(mailOn: true, usual: true, signature: "Own Name"))).StatusCode);
            var own = (await Mine()).Applies!;
            Assert.Equal((true, "restaurant", "Own Name", "restaurant"), (own.MailUsual, own.MailUsualSetBy, own.Signature, own.SignatureSetBy));

            // Every change is in the audit log.
            var actions = await Db(db => Task.FromResult(db.AuditLogs.Where(a => a.GroupId == groupId).OrderByDescending(a => a.Id).Take(80).Select(a => a.Action).ToList()));
            Assert.Contains("GroupSettingsChanged", actions);
            Assert.Contains("RestaurantSettingsChanged", actions);
            Assert.Contains("GroupMailModeChanged", actions);
        }
        finally
        {
            await SaveSettings(site, s => s.MailMode = "each");
            await site.PutAsJsonAsync($"/api/system/groups/{groupId}/settings", new GroupSettingsRequest("on", "test", "each", null, null, [new(rid, false, false, false, null)]));
            await site.DeleteAsync("/api/system/postgrid/mail?mode=live");
        }
    }

    [Fact]
    public async Task An_investment_group_administrator_can_also_be_an_owner_of_a_restaurant()
    {
        var (owner, admin, _, rid, groupId) = await SetUpAsync();
        var n = Interlocked.Increment(ref _n);
        var username = $"both-roles-{n}";
        try
        {
            // Made an administrator and, in the same step, an owner of one restaurant: two roles for one person.
            var created = await admin.PostAsJsonAsync("/api/users", new UserRequest
            {
                Username = username, DisplayName = "Robin Both", Email = $"{username}@test.local", Password = "BothRoles!123", Role = "GroupAdmin", RestaurantIds = [rid]
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var both = (await created.Content.ReadFromJsonAsync<UserDto>())!;
            Assert.Equal(("GroupAdmin", true), (both.Role, both.AlsoOwner));
            Assert.Equal(new[] { rid }, both.RestaurantIds);
            Assert.Contains(" and ", both.RoleTitle);

            // The restaurant lists them among its owners, and its staffing screen shows them ticked.
            var listed = (await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants?all=true"))!.Single(x => x.Id == rid);
            Assert.Contains(listed.Owners!, o => o.Id == both.Id);
            var staff = (await owner.GetFromJsonAsync<StaffResponse>($"/api/restaurants/{rid}/staff"))!;
            Assert.True(staff.AdminOwners.Single(a => a.Id == both.Id).Assigned);
            Assert.DoesNotContain(staff.Owners, o => o.Id == both.Id);

            // Taking them off the restaurant leaves them an administrator; ticking them again makes them an owner again.
            var others = staff.Owners.Concat(staff.Managers).Concat(staff.TeamLogins).Where(m => m.Assigned).Select(m => m.Id).ToArray();
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/restaurants/{rid}/staff", new StaffRequest(others))).StatusCode);
            var after = (await admin.GetFromJsonAsync<List<UserDto>>("/api/users"))!.Single(u => u.Id == both.Id);
            Assert.Equal(("GroupAdmin", false), (after.Role, after.AlsoOwner));
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/restaurants/{rid}/staff", new StaffRequest([.. others, both.Id]))).StatusCode);
            Assert.True((await admin.GetFromJsonAsync<List<UserDto>>("/api/users"))!.Single(u => u.Id == both.Id).AlsoOwner);
        }
        finally
        {
            // Other tests count on this group having no administrator of its own.
            await Db(async db =>
            {
                db.Users.RemoveRange(db.Users.Where(u => u.Username == username));
                return await db.SaveChangesAsync();
            });
        }
    }

    [Fact]
    public async Task Cards_a_restaurant_prints_itself_for_a_stamp_by_hand_are_charged_the_sites_fee_once()
    {
        var (owner, _, site, rid, groupId) = await SetUpAsync();
        await SaveSettings(site, s => { s.CostCents = 90; s.FeeCents = 10; s.ChargeHandPrintFee = true; s.EmailBills = false; s.PrintBills = false; });
        Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/billing/groups/{groupId}", new GroupBillingTermsRequest(null, 1_000_000))).StatusCode);
        var now = DateTime.UtcNow;
        var month = MailLedger.MonthKey(MailLedger.MonthStart(now).AddMonths(-8));
        var mine = new List<int>();
        async Task<PrintConfig> Config() => (await owner.GetFromJsonAsync<PrintConfig>("/api/comments/print-config"))!;
        async Task<CommentSummary> New(string name) { var c = await Create(owner, Card(rid, name)); mine.Add(c.Id); return c; }
        async Task<List<CommentSummary>> Print(string postage, params int[] ids) =>
            (await (await owner.PostAsJsonAsync("/api/comments/printed", new PrintedRequest(ids, true, postage))).Content.ReadFromJsonAsync<List<CommentSummary>>())!;
        try
        {
            // The print screens are told the fee before anything is printed: the site's fee per card, not the mail house's cost.
            Assert.Equal(10, (await Config()).PrintFeeCents);
            Assert.Equal(10, (await owner.GetFromJsonAsync<GroupBillingDto>("/api/billing"))!.PrintFeeCents);

            // Two cards printed for a stamp by hand: each is charged the fee, once.
            var a = await New("Hand A");
            var b = await New("Hand B");
            Assert.Null(a.PrintFeeCents);
            Assert.All(await Print("stamp", a.Id, b.Id), c => Assert.Equal(10, c.PrintFeeCents));
            // Printing them again (a jam, a smudge) charges nothing more.
            Assert.All(await Print("stamp", a.Id, b.Id), c => Assert.Equal((10, 2), (c.PrintFeeCents, c.PrintCount)));
            Assert.Equal(20, await Db(db => Task.FromResult(db.Comments.Where(c => mine.Contains(c.Id)).Sum(c => c.PrintFeeCents ?? 0))));
            var audited = await Db(db => Task.FromResult(db.AuditLogs.Where(x => x.Action == "CardPrintFeeCharged" && x.RestaurantId == rid).OrderByDescending(x => x.Id).First()));
            Assert.Contains("\"totalCents\":20", audited.Details);

            // A group with its own fee pays that; with the charge switched off, nothing is charged.
            Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/billing/groups/{groupId}", new GroupBillingTermsRequest(25, 1_000_000))).StatusCode);
            Assert.Equal(25, (await Config()).PrintFeeCents);
            var c3 = await New("Hand C");
            Assert.Equal(25, (await Print("stamp", c3.Id)).Single().PrintFeeCents);
            await SaveSettings(site, s => s.ChargeHandPrintFee = false);
            Assert.Equal(0, (await Config()).PrintFeeCents);
            var d = await New("Hand D");
            Assert.Null((await Print("stamp", d.Id)).Single().PrintFeeCents);
            // Switching it back on does not go back and charge a card that was already printed.
            await SaveSettings(site, s => s.ChargeHandPrintFee = true);
            Assert.Null((await Print("stamp", d.Id)).Single().PrintFeeCents);

            // The fees go on the month's bill as their own lines: no mail house cost, only the fee.
            await Db(async db =>
            {
                foreach (var c in db.Comments.Where(c => mine.Contains(c.Id) && c.PrintFeeCents != null)) c.PrintFeeAt = MailLedger.MonthStart(now).AddMonths(-8).AddDays(3);
                return await db.SaveChangesAsync();
            });
            var statement = (await site.GetFromJsonAsync<SiteMailStatement>($"/api/system/billing/statement?month={month}&groupId={groupId}"))!;
            Assert.Equal((3, 3, 0, 45), (statement.Rows.Single().Cards, statement.Rows.Single().PrintedCards, statement.CostCents, statement.FeeCents));
            Assert.Equal(HttpStatusCode.OK, (await site.PostAsJsonAsync("/api/system/billing/issue", new IssueBillsRequest(month))).StatusCode);
            var bill = (await Billing(site)).Invoices.Single(i => i.GroupId == groupId && i.Month == month);
            Assert.Equal((3, 0, 45, 45), (bill.Cards, bill.CostCents, bill.FeeCents, bill.TotalCents));
            Assert.All(bill.Lines, l => Assert.True(l.Printed));
            Assert.Equal(45, bill.Lines.Sum(l => l.Cents));
        }
        finally
        {
            await Db(async db =>
            {
                db.MailInvoices.RemoveRange(db.MailInvoices.Where(i => i.GroupId == groupId && i.Month == month));
                db.Comments.RemoveRange(db.Comments.Where(c => mine.Contains(c.Id)));
                return await db.SaveChangesAsync();
            });
            await SaveSettings(site, s => s.ChargeHandPrintFee = true);
            await site.PutAsJsonAsync($"/api/system/billing/groups/{groupId}", new GroupBillingTermsRequest(null, null));
        }
    }

    [Fact]
    public async Task A_group_with_credit_but_no_payment_method_can_mail_as_far_as_its_credit_covers()
    {
        var (owner, _, site, rid, groupId) = await SetUpAsync(LiveKey);
        await SaveSettings(site, s => { s.CostCents = 90; s.FeeCents = 10; s.EmailBills = false; s.PrintBills = false; s.RequirePaymentMethod = true; });
        Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/billing/groups/{groupId}", new GroupBillingTermsRequest(null, 1_000_000, null, "site"))).StatusCode);
        string? hadMethod = null;
        await Db(async db =>
        {
            var b = db.GroupBilling.Single(x => x.GroupId == groupId);
            hadMethod = b.PayMethodId;
            b.PayMethodId = null;
            foreach (var i in db.MailInvoices.Where(i => i.GroupId == groupId && i.Status == InvoiceStatuses.Open)) i.Status = InvoiceStatuses.Void;
            db.GroupCredits.RemoveRange(db.GroupCredits.Where(c => c.GroupId == groupId));
            foreach (var c in db.Comments.Where(c => c.Restaurant.GroupId == groupId && c.PrintFeeCents != null)) { c.PrintFeeCents = null; c.PrintFeeAt = null; }
            return await db.SaveChangesAsync();
        });
        // Cards mailed by earlier tests this month that are not on a bill yet would count against the credit; they are moved out of the way.
        var now = DateTime.UtcNow;
        var parked = await Db(async db =>
        {
            var ids = db.Comments.Where(c => c.Restaurant.GroupId == groupId && c.MailId != null && !c.MailTest && c.MailedAt != null).Select(c => c.Id).ToList();
            foreach (var c in db.Comments.Where(c => ids.Contains(c.Id))) c.MailTest = true;
            await db.SaveChangesAsync();
            return ids;
        });
        var month = MailLedger.MonthKey(MailLedger.MonthStart(now).AddMonths(-7));
        var mine = new List<int>();
        async Task<PrintConfig> Config() => (await owner.GetFromJsonAsync<PrintConfig>("/api/comments/print-config"))!;
        async Task<int[]> Cards(int n)
        {
            var ids = new List<int>();
            for (var i = 0; i < n; i++) ids.Add((await Create(owner, Card(rid, $"OnCredit {mine.Count + i}"))).Id);
            mine.AddRange(ids);
            return ids.ToArray();
        }
        Task<HttpResponseMessage> Send(int[] ids) => owner.PostAsJsonAsync("/api/comments/mail", new MailRequest(ids, ids.Length, 100));
        try
        {
            // No payment method and no credit: mailing waits for a payment method.
            var none = await Config();
            Assert.Equal(("payment", false, false, 0), (none.MailBlockedBy, none.CanMail, none.MailOnCredit, none.MailCreditCents));
            Assert.Equal(HttpStatusCode.BadRequest, (await Send(await Cards(1))).StatusCode);

            // A credit of $2.50 at $1.00 a card: mailing opens, paid from the credit, for two cards and no more.
            Assert.Equal(HttpStatusCode.OK, (await site.PostAsJsonAsync($"/api/system/billing/groups/{groupId}/credits",
                new AddCreditRequest(250, "bonus", "Welcome bonus so the group can try mailing", 250, $"oncredit-{Guid.NewGuid():N}"))).StatusCode);
            var open = await Config();
            Assert.Equal(((string?)null, true, true, 250, false), (open.MailBlockedBy, open.CanMail, open.MailOnCredit, open.MailCreditCents, open.MailNeedsPayMethod));
            var three = await Send(await Cards(3));
            Assert.Equal(HttpStatusCode.BadRequest, three.StatusCode);
            Assert.Contains("covers 2 cards", await three.Content.ReadAsStringAsync());
            var two = await Send(await Cards(2));
            Assert.Equal(HttpStatusCode.OK, two.StatusCode);
            Assert.All((await two.Content.ReadFromJsonAsync<List<MailOutcome>>())!, o => Assert.True(o.Ok));

            // Those two cards are not on a bill yet, but the credit they will use is already spoken for: 50 cents is left, which does not cover a card.
            var spent = await Config();
            Assert.Equal(("payment", false, 50), (spent.MailBlockedBy, spent.CanMail, spent.MailCreditCents));
            var refused = await Send(await Cards(1));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("does not cover a card", await refused.Content.ReadAsStringAsync());
            Assert.True(await Db(db => Task.FromResult(db.AuditLogs.Any(a => a.Action == "CardMailOverCredit" && a.GroupId == groupId))));

            // When the month's bill is made the credit pays it, and the same 50 cents is what is left.
            await Db(async db =>
            {
                foreach (var c in db.Comments.Where(c => mine.Contains(c.Id) && c.MailId != null)) c.MailedAt = MailLedger.MonthStart(now).AddMonths(-7).AddDays(2);
                return await db.SaveChangesAsync();
            });
            Assert.Equal(HttpStatusCode.OK, (await site.PostAsJsonAsync("/api/system/billing/issue", new IssueBillsRequest(month))).StatusCode);
            var bill = (await Billing(site)).Invoices.Single(i => i.GroupId == groupId && i.Month == month);
            Assert.Equal((200, 200, "paid", "credit"), (bill.TotalCents, bill.CreditCents, bill.Status, bill.PaidHow));
            Assert.Equal(50, (await Config()).MailCreditCents);
            Assert.Equal(50, (await Billing(site)).Groups.Single(g => g.GroupId == groupId).CreditCents);

            // Once a payment method is saved the credit no longer limits how many cards can go: what it does not cover is billed.
            await Db(async db => { db.GroupBilling.Single(x => x.GroupId == groupId).PayMethodId = "pm_saved_for_test"; return await db.SaveChangesAsync(); });
            var saved = await Config();
            Assert.Equal(((string?)null, true, false), (saved.MailBlockedBy, saved.CanMail, saved.MailOnCredit));
            Assert.Equal(HttpStatusCode.OK, (await Send(await Cards(3))).StatusCode);
        }
        finally
        {
            await Db(async db =>
            {
                db.MailInvoices.RemoveRange(db.MailInvoices.Where(i => i.GroupId == groupId && i.Month == month));
                db.GroupCredits.RemoveRange(db.GroupCredits.Where(c => c.GroupId == groupId));
                db.Comments.RemoveRange(db.Comments.Where(c => mine.Contains(c.Id)));
                foreach (var c in db.Comments.Where(c => parked.Contains(c.Id))) c.MailTest = false;
                db.GroupBilling.Single(x => x.GroupId == groupId).PayMethodId = hadMethod;
                return await db.SaveChangesAsync();
            });
            await SaveSettings(site, s => s.RequirePaymentMethod = false);
        }
    }

    [Fact]
    public async Task Whether_a_payment_method_is_needed_before_mailing_can_be_set_for_one_group()
    {
        var (owner, _, site, _, groupId) = await SetUpAsync();
        async Task<PrintConfig> Config() => (await owner.GetFromJsonAsync<PrintConfig>("/api/comments/print-config"))!;
        async Task<GroupBillingRow> Row() => (await Billing(site)).Groups.Single(g => g.GroupId == groupId);
        Task<HttpResponseMessage> Rule(string? rule) => site.PutAsJsonAsync($"/api/system/billing/groups/{groupId}", new GroupBillingTermsRequest(null, null, null, rule));
        await Db(async db =>
        {
            var b = db.GroupBilling.Single(x => x.GroupId == groupId);
            b.PayMethodId = null;
            b.RequirePayMethod = null;
            return await db.SaveChangesAsync();
        });
        try
        {
            // The site's setting applies to a group with no rule of its own: required, unless the System Administrator switches that off.
            await SaveSettings(site, s => s.RequirePaymentMethod = true);
            Assert.Equal((true, (bool?)null), ((await Row()).PayRequired, (await Row()).OwnPayRule));
            Assert.Equal("payment", (await Config()).MailBlockedBy);
            Assert.True((await Config()).MailNeedsPayMethod);

            // One group can be let off: it mails without a saved payment method while everyone else still needs one.
            Assert.Equal(HttpStatusCode.OK, (await Rule("notrequired")).StatusCode);
            Assert.Equal((false, (bool?)false), ((await Row()).PayRequired, (await Row()).OwnPayRule));
            Assert.Null((await Config()).MailBlockedBy);
            Assert.True((await Config()).CanMail);

            // And one group can be held to it when the site as a whole is not.
            await SaveSettings(site, s => s.RequirePaymentMethod = false);
            Assert.Equal(HttpStatusCode.OK, (await Rule("required")).StatusCode);
            Assert.Equal((true, (bool?)true), ((await Row()).PayRequired, (await Row()).OwnPayRule));
            Assert.Equal("payment", (await Config()).MailBlockedBy);

            // Back to following the site; leaving the rule out of another change keeps it; nonsense is refused; only the System Administrator decides.
            Assert.Equal(HttpStatusCode.OK, (await Rule("site")).StatusCode);
            Assert.Null((await Row()).OwnPayRule);
            Assert.Null((await Config()).MailBlockedBy);
            Assert.Equal(HttpStatusCode.OK, (await Rule("required")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Rule(null)).StatusCode);
            Assert.True((await Row()).OwnPayRule);
            Assert.Equal(HttpStatusCode.BadRequest, (await Rule("sometimes")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync($"/api/system/billing/groups/{groupId}", new GroupBillingTermsRequest(null, null, null, "notrequired"))).StatusCode);
            Assert.True(await Db(db => Task.FromResult(db.AuditLogs.Count(a => a.Action == "GroupPaymentRuleChanged" && a.GroupId == groupId) >= 4)));
        }
        finally
        {
            await Rule("site");
            await SaveSettings(site, s => s.RequirePaymentMethod = false);
        }
    }

    [Fact]
    public async Task Mailing_is_off_for_a_group_until_switched_on_and_a_restaurant_can_make_it_its_usual_way()
    {
        var (owner, _, site, rid, groupId) = await SetUpAsync();
        async Task<PrintConfig> Config() => (await owner.GetFromJsonAsync<PrintConfig>("/api/comments/print-config"))!;
        async Task<GroupBillingRow> Row() => (await site.GetFromJsonAsync<SystemBillingDto>("/api/system/billing"))!.Groups.Single(g => g.GroupId == groupId);
        var card = await Create(owner, Card(rid, "Switch"));
        var send = new MailRequest([card.Id], 1, (await Config()).MailCentsPerCard);
        try
        {
            // Switched off: the group is told why it cannot be used, and nothing can be sent.
            Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/billing/groups/{groupId}", new GroupBillingTermsRequest(null, null, false))).StatusCode);
            Assert.False((await Row()).MailEnabled);
            var off = await Config();
            Assert.False(off.MailAvailable);
            Assert.False(off.CanMail);
            Assert.False(off.MailNeedsPayMethod);
            Assert.Equal("group", off.MailBlockedBy);
            var refused = await owner.PostAsJsonAsync("/api/comments/mail", send);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var why = await refused.Content.ReadAsStringAsync();
            Assert.Contains("not switched on for this investment group", why);
            Assert.DoesNotContain("PostGrid", why);
            Assert.False((await owner.GetFromJsonAsync<GroupBillingDto>("/api/billing"))!.MailAvailable);

            // Only the System Administrator switches it. Leaving the switch out of a change to the fee keeps it as it is.
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync($"/api/system/billing/groups/{groupId}", new GroupBillingTermsRequest(null, null, true))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/billing/groups/{groupId}", new GroupBillingTermsRequest(7, null))).StatusCode);
            Assert.False((await Row()).MailEnabled);

            // Switched on: it can be used (the test group has no payment method, so that comes next when one is required).
            Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/billing/groups/{groupId}", new GroupBillingTermsRequest(null, null, true))).StatusCode);
            Assert.True((await Row()).MailEnabled);
            var on = await Config();
            Assert.True(on.MailAvailable);
            Assert.Contains(on.MailBlockedBy, new[] { null, "payment" });
            Assert.True(await Db(db => Task.FromResult(db.AuditLogs.Any(a => a.Action == "GroupMailingSwitchedOff" && a.GroupId == groupId))));
            Assert.True(await Db(db => Task.FromResult(db.AuditLogs.Any(a => a.Action == "GroupMailingSwitchedOn" && a.GroupId == groupId))));

            // A restaurant's owner can make "mailed for you" its usual way; everyone who prints its cards is told.
            var r = (await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants"))!.Single(x => x.Id == rid);
            Assert.False(r.MailByDefault);
            RestaurantRequest With(bool usual) => new() { Name = r.Name, StreetAddress = r.StreetAddress, City = r.City, State = r.State, Zip = r.Zip, Phone = r.Phone, MailByDefault = usual };
            Assert.True((await (await owner.PutAsJsonAsync($"/api/restaurants/{rid}", With(true))).Content.ReadFromJsonAsync<RestaurantDto>())!.MailByDefault);
            Assert.True((await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants"))!.Single(x => x.Id == rid).MailByDefault);
            Assert.False((await (await owner.PutAsJsonAsync($"/api/restaurants/{rid}", With(false))).Content.ReadFromJsonAsync<RestaurantDto>())!.MailByDefault);
        }
        finally
        {
            await site.PutAsJsonAsync($"/api/system/billing/groups/{groupId}", new GroupBillingTermsRequest(null, null, true));
        }
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

    [Fact]
    public async Task Permissions_are_handed_down_from_the_site_to_the_group_to_owners_to_managers()
    {
        var (owner, admin, site, rid, groupId) = await SetUpAsync();
        var n = Interlocked.Increment(ref _n);
        var username = $"perm-manager-{n}";
        static PermissionRulesRequest Rule(string name, string? owners = null, string? managers = null) => new([new PermissionRuleChange(name, owners, managers)]);
        async Task<string[]> Holds(HttpClient who) => (await who.GetFromJsonAsync<UserDto>("/api/auth/me"))!.PermissionNames;
        try
        {
            // An owner hires a manager and ticks two permissions for them.
            var made = await owner.PostAsJsonAsync("/api/users", new UserRequest
            {
                Username = username, DisplayName = "Pat Manager", Password = "Manager!12345", Role = "Manager", RestaurantIds = [rid], Permissions = ["ViewComments", "ExportComments", "ManageStaff"]
            });
            Assert.Equal(HttpStatusCode.Created, made.StatusCode);
            var managerId = (await made.Content.ReadFromJsonAsync<UserDto>())!.Id;
            var manager = await factory.LoginAsync(username, "Manager!12345");
            // A permission that belongs to owners is never given to a manager, whatever was sent.
            Assert.Equal(new[] { "ViewComments", "ExportComments" }, await Holds(manager));
            Assert.Contains("ManageStaff", await Holds(owner));

            // The System Administrator says no owner in this group may export: that takes precedence, the group's own
            // administrators cannot undo it, and a manager cannot hold what the owners do not.
            Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/groups/{groupId}/permissions", Rule("ExportComments", owners: "no"))).StatusCode);
            Assert.DoesNotContain("ExportComments", await Holds(owner));
            Assert.Equal(new[] { "ViewComments" }, await Holds(manager));
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/comments/export")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync("/api/permission-rules", Rule("ExportComments", owners: "no", managers: "yes"))).StatusCode);
            var asGroup = (await admin.GetFromJsonAsync<PermissionRulesDto>("/api/permission-rules"))!.Permissions.Single(x => x.Name == "ExportComments");
            Assert.Equal((false, "site", "none", "owners"), (asGroup.OwnersCan, asGroup.OwnersSetBy, asGroup.Managers, asGroup.ManagersSetBy));
            // Investment Group Administrators can do everything, whatever is said about owners and managers.
            Assert.Contains("ExportComments", await Holds(admin));

            // Left to the group again, its administrators decide: every manager sees the reports, none may edit comments.
            Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/groups/{groupId}/permissions", Rule("ExportComments", owners: "group"))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/permission-rules", new PermissionRulesRequest(
                [new("ViewAnalytics", null, "yes"), new("EditComments", null, "no")]))).StatusCode);
            Assert.Equal(new[] { "ViewComments", "ViewAnalytics", "ExportComments" }, await Holds(manager));
            // An owner ticking a permission no manager may hold changes nothing; one left to each manager is kept.
            UserRequest Edit(params string[] permissions) => new() { Username = username, DisplayName = "Pat Manager", Role = "Manager", RestaurantIds = [rid], Permissions = permissions };
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/users/{managerId}", Edit("ViewComments", "EditComments", "DeleteComments"))).StatusCode);
            Assert.Equal(new[] { "ViewComments", "DeleteComments", "ViewAnalytics" }, await Holds(manager));
            // The System Administrator's answer for managers wins over the group's.
            Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/groups/{groupId}/permissions", Rule("ViewAnalytics", managers: "no"))).StatusCode);
            Assert.Equal(new[] { "ViewComments", "DeleteComments" }, await Holds(manager));

            // The group's administrators decide whether owners choose what managers may do. Without it, an owner's
            // ticks are left out, and the manager keeps what they had.
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/permission-rules", Rule("SetPermissions", owners: "no"))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/users/{managerId}", Edit("MarkResponded"))).StatusCode);
            Assert.Equal(new[] { "ViewComments", "DeleteComments" }, await Holds(manager));
            // An administrator of the group still can.
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/users/{managerId}", Edit("ViewComments", "MarkResponded"))).StatusCode);
            Assert.Equal(new[] { "ViewComments", "MarkResponded" }, await Holds(manager));

            // Owners who may not manage staff or change restaurants are told so, by name of who can.
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/permission-rules", new PermissionRulesRequest(
                [new("ManageStaff", "no", null), new("EditRestaurants", "no", null)]))).StatusCode);
            var refused = await owner.PutAsJsonAsync($"/api/users/{managerId}/restaurants", new UserRestaurantsRequest([]));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("administrators", await refused.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync("/api/users", new UserRequest { Username = username + "-b", DisplayName = "No", Password = "Manager!12345", Role = "Manager", RestaurantIds = [rid] })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync($"/api/restaurants/{rid}", new RestaurantRequest { Name = "Renamed" })).StatusCode);
            var mine = (await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants?all=true"))!.Single(x => x.Id == rid);
            Assert.Equal((true, false), (mine.CanManage, mine.CanEdit));

            // Mapping a person to restaurants in one step: an administrator takes the manager off, and puts them back.
            var off = await admin.PutAsJsonAsync($"/api/users/{managerId}/restaurants", new UserRestaurantsRequest([]));
            Assert.Equal(HttpStatusCode.OK, off.StatusCode);
            Assert.Empty((await off.Content.ReadFromJsonAsync<UserDto>())!.RestaurantIds);
            var on = await admin.PutAsJsonAsync($"/api/users/{managerId}/restaurants", new UserRestaurantsRequest([rid]));
            Assert.Equal(new[] { rid }, (await on.Content.ReadFromJsonAsync<UserDto>())!.RestaurantIds);
            Assert.Equal(2, await Db(db => Task.FromResult(db.AuditLogs.ToList().Count(a => a.Action == "UserRestaurantsChanged" && a.EntityId == managerId.ToString()))));
            Assert.True(await Db(db => Task.FromResult(db.AuditLogs.ToList().Any(a => a.Action == "PermissionRulesSetBySite" && a.GroupId == groupId))));
            Assert.True(await Db(db => Task.FromResult(db.AuditLogs.ToList().Any(a => a.Action == "PermissionRulesChanged" && a.GroupId == groupId))));
            // The System Administrator maps a person from the Investment groups screen, without opening the group.
            var bySite = await site.PutAsJsonAsync($"/api/users/{managerId}/restaurants", new UserRestaurantsRequest([]));
            Assert.Equal(HttpStatusCode.OK, bySite.StatusCode);
            Assert.Empty((await bySite.Content.ReadFromJsonAsync<UserDto>())!.RestaurantIds);
            Assert.Equal(new[] { rid }, (await (await site.PutAsJsonAsync($"/api/users/{managerId}/restaurants", new UserRestaurantsRequest([rid]))).Content.ReadFromJsonAsync<UserDto>())!.RestaurantIds);
        }
        finally
        {
            await Db(async db =>
            {
                var g = db.Groups.First(x => x.Id == groupId);
                g.SiteOwnerYes = g.SiteOwnerNo = g.SiteManagerYes = g.SiteManagerNo = g.OwnerNo = g.ManagerYes = g.ManagerNo = ManagerPermission.None;
                db.Users.RemoveRange(db.Users.Where(u => u.Username.StartsWith(username)));
                return await db.SaveChangesAsync();
            });
        }
    }

    [Fact]
    public async Task The_site_decides_a_setting_for_a_group_or_leaves_it_to_the_groups_administrators()
    {
        var (_, admin, site, _, groupId) = await SetUpAsync();
        try
        {
            // What the System Administrator decides for the group, the group's administrators cannot change.
            Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/groups/{groupId}/settings",
                new GroupSettingsRequest("on", "test", "stamp", null, null, null) { GroupDecides = ["mode"] })).StatusCode);
            var rules = (await admin.GetFromJsonAsync<GroupRulesDto>("/api/group-rules"))!;
            Assert.Equal(("on", "test", "stamp"), (rules.Mail, rules.MailMode, rules.UsualWay));
            Assert.Equal(new[] { "mode" }, rules.GroupDecides);
            var refused = await admin.PutAsJsonAsync("/api/group-rules", new GroupRulesRequest(null, null, "mail", null, null));
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("people who run the site", await refused.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync("/api/group-rules", new GroupRulesRequest("off", null, null, null, null))).StatusCode);
            // What is left to the group, its administrators set for all restaurants or leave to each restaurant.
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/group-rules", new GroupRulesRequest(null, "each", null, null, null))).StatusCode);

            // Handed over, the group decides the rest too, switching mailing on or off included.
            Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/groups/{groupId}/settings",
                new GroupSettingsRequest(null, null, null, null, null, null) { GroupDecides = ["mail", "mode", "usual", "signature"] })).StatusCode);
            var changed = await admin.PutAsJsonAsync("/api/group-rules", new GroupRulesRequest("each", "live", "mail", "all", "Jordan Lee"));
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            var now = (await changed.Content.ReadFromJsonAsync<GroupRulesDto>())!;
            Assert.Equal(("each", "live", "mail", "Jordan Lee"), (now.Mail, now.MailMode, now.UsualWay, now.SignatureForAll));
            var seen = (await site.GetFromJsonAsync<GroupSettingsDto>($"/api/system/groups/{groupId}/settings"))!;
            Assert.Equal(("each", "live", "mail", "Jordan Lee", 4), (seen.Mail, seen.MailMode, seen.UsualWay, seen.SignatureForAll, seen.GroupDecides.Length));
        }
        finally
        {
            await Db(async db =>
            {
                var b = db.GroupBilling.First(x => x.GroupId == groupId);
                b.GroupDecides = GroupChoice.Default;
                b.SignatureForAll = null;
                b.UsualWay = "each";
                return await db.SaveChangesAsync();
            });
        }
    }

    [Fact]
    public async Task A_group_chooses_one_bill_a_bill_for_each_restaurant_or_bills_for_sets_of_restaurants()
    {
        var (owner, admin, site, rid, groupId) = await SetUpAsync();
        await SaveSettings(site, s => { s.CostCents = 90; s.FeeCents = 10; s.ChargeHandPrintFee = true; s.EmailBills = false; s.PrintBills = false; });
        Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/billing/groups/{groupId}", new GroupBillingTermsRequest(null, 1_000_000))).StatusCode);
        var start = MailLedger.MonthStart(DateTime.UtcNow).AddMonths(-10);
        var month = MailLedger.MonthKey(start);
        var mine = new List<int>();
        var second = 0;
        async Task<List<SystemInvoiceDto>> Bills() => (await Billing(site)).Invoices.Where(i => i.GroupId == groupId && i.Month == month).OrderBy(i => i.Number).ToList();
        async Task Issue() => Assert.Equal(HttpStatusCode.OK, (await site.PostAsJsonAsync("/api/system/billing/issue", new IssueBillsRequest(month))).StatusCode);
        Task<int> Forget() => Db(async db => { db.MailInvoices.RemoveRange(db.MailInvoices.Where(i => i.GroupId == groupId && i.Month == month)); return await db.SaveChangesAsync(); });
        try
        {
            // A second restaurant, and cards both restaurants printed themselves in a month that is over.
            var made = await admin.PostAsJsonAsync("/api/restaurants", new RestaurantRequest { Name = "Split Second", StreetAddress = "2 Test St", City = "Joliet", State = "IL", Zip = "60431" });
            Assert.True(made.IsSuccessStatusCode);
            second = (await made.Content.ReadFromJsonAsync<RestaurantDto>())!.Id;
            var firstName = (await admin.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants?all=true"))!.Single(r => r.Id == rid).Name;
            foreach (var (restaurant, name) in new[] { (rid, "Split A"), (rid, "Split B"), (second, "Split C") })
                mine.Add((await Create(admin, Card(restaurant, name))).Id);
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/comments/printed", new PrintedRequest(mine.ToArray(), true, "stamp"))).StatusCode);
            await Db(async db =>
            {
                foreach (var c in db.Comments.Where(c => mine.Contains(c.Id))) c.PrintFeeAt = start.AddDays(3);
                return await db.SaveChangesAsync();
            });

            // Only the group's administrators choose; an owner cannot.
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync("/api/billing/split", new BillSplitRequest("restaurant", null))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/billing/split", new BillSplitRequest("weekly", null))).StatusCode);
            Assert.Equal("one", (await admin.GetFromJsonAsync<GroupBillingDto>("/api/billing"))!.Bills!.Split);

            // A bill for each restaurant: two bills, each named for its restaurant, and never made twice.
            var chosen = await admin.PutAsJsonAsync("/api/billing/split", new BillSplitRequest("restaurant", null));
            Assert.Equal("restaurant", (await chosen.Content.ReadFromJsonAsync<GroupBillingDto>())!.Bills!.Split);
            await Issue();
            await Issue();
            var each = await Bills();
            Assert.Equal(2, each.Count);
            Assert.Equal(new[] { 20, 10 }.Order(), each.Select(b => b.TotalCents).Order());
            Assert.Equal(new[] { firstName, "Split Second" }.Order(), each.Select(b => b.For!).Order());
            Assert.All(each, b => Assert.Matches(@"-\d\d$", b.Number[^3..]));
            Assert.Equal(2, each.Select(b => b.Number).Distinct().Count());
            // The group sees which restaurant each bill is for.
            var seen = (await admin.GetFromJsonAsync<GroupBillingDto>("/api/billing"))!.Invoices.Where(i => i.Month == month).ToList();
            Assert.Equal(2, seen.Count(i => i.For is not null));

            // Bills for sets of restaurants: both in one set share a bill named for the set.
            await Forget();
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/billing/split",
                new BillSplitRequest("sets", [new BillSetRequest(rid, "North"), new BillSetRequest(second, " north ")]))).StatusCode);
            await Issue();
            var set = Assert.Single(await Bills());
            Assert.Equal((30, "North", 3), (set.TotalCents, set.For, set.Cards));

            // One set and the rest: the restaurants in no set share the other bill.
            await Forget();
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/billing/split", new BillSplitRequest(null, [new BillSetRequest(second, "")]))).StatusCode);
            await Issue();
            Assert.Equal(new[] { "North", "Other restaurants" }, (await Bills()).Select(b => b.For!).Order());

            // One bill for everything, as before: no name, and the plain number.
            await Forget();
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/billing/split", new BillSplitRequest("one", null))).StatusCode);
            await Issue();
            var one = Assert.Single(await Bills());
            Assert.Equal((30, null, $"{month.Replace("-", "")}-{groupId:000}"), (one.TotalCents, one.For, one.Number));
            Assert.True(await Db(db => Task.FromResult(db.AuditLogs.ToList().Any(a => a.Action == "BillSplitChanged" && a.GroupId == groupId && (a.Details ?? "").Contains("North")))));
        }
        finally
        {
            await Db(async db =>
            {
                db.MailInvoices.RemoveRange(db.MailInvoices.Where(i => i.GroupId == groupId && i.Month == month));
                db.Comments.RemoveRange(db.Comments.Where(c => mine.Contains(c.Id)));
                db.Restaurants.RemoveRange(db.Restaurants.Where(r => r.Id == second));
                foreach (var r in db.Restaurants.Where(r => r.GroupId == groupId)) r.BillSet = null;
                foreach (var b in db.GroupBilling.Where(b => b.GroupId == groupId)) b.BillSplit = "one";
                return await db.SaveChangesAsync();
            });
        }
    }

    [Fact]
    public async Task Addresses_are_offered_as_the_street_is_typed_for_restaurants_that_have_cards_mailed()
    {
        var (owner, admin, site, rid, groupId) = await SetUpAsync();
        const string typed = "7431 Quietwater";
        async Task<AddressSuggestResponse> Ask(HttpClient who, int restaurant, string street) =>
            (await (await who.PostAsJsonAsync("/api/addresses/suggest", new AddressSuggestRequest(restaurant, street, null, null, null))).Content.ReadFromJsonAsync<AddressSuggestResponse>())!;
        try
        {
            // Mailing is on for the restaurant: addresses are offered, nearest the restaurant first.
            var before = factory.Suggestions.Calls;
            var offered = await Ask(owner, rid, typed);
            Assert.True(offered.Available);
            Assert.Equal(new[] { $"{typed} St", $"{typed} Ave" }, offered.Suggestions.Select(s => s.StreetAddress));
            Assert.Equal(("Joliet", "IL", "60431"), (offered.Suggestions[0].City, offered.Suggestions[0].State, offered.Suggestions[0].Zip));
            Assert.Equal("IL", factory.Suggestions.LastNearState);
            Assert.Equal(before + 1, factory.Suggestions.Calls);

            // Someone else's restaurant, or one that does not exist: nothing is offered and the address service is not asked.
            Assert.False((await Ask(owner, 987654, typed)).Available);
            Assert.Equal(before + 1, factory.Suggestions.Calls);

            // What was typed is not kept: not in the interaction log, and not in the audit log.
            Assert.False(await Db(db => Task.FromResult(db.InteractionLogs.ToList().Any(l => $"{l.Path} {l.Action} {l.Detail}".Contains("Quietwater")))));
            Assert.False(await Db(db => Task.FromResult(db.AuditLogs.ToList().Any(a => (a.Details ?? "").Contains("Quietwater")))));

            // With mailing off for the group, its restaurants are not offered addresses.
            Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/groups/{groupId}/settings", new GroupSettingsRequest("off", null, null, null, null, null))).StatusCode);
            var off = await Ask(owner, rid, typed);
            Assert.False(off.Available);
            Assert.Empty(off.Suggestions);
            Assert.Equal(before + 1, factory.Suggestions.Calls);

            // And with no address service that can offer them, nothing is offered either.
            Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/groups/{groupId}/settings", new GroupSettingsRequest("on", null, null, null, null, null))).StatusCode);
            factory.Suggestions.Configured = false;
            Assert.False((await Ask(owner, rid, typed)).Available);
            // Nobody inside a group is told why, not even a System Administrator using the group's screens: the
            // answer says nothing about how the site is set up, and never names the company.
            foreach (var who in new[] { owner, admin })
            {
                var said = await (await who.PostAsJsonAsync("/api/addresses/suggest", new AddressSuggestRequest(rid, typed, null, null, null))).Content.ReadAsStringAsync();
                Assert.DoesNotContain("PostGrid", said, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("why", said, StringComparison.OrdinalIgnoreCase);
            }
            // The System Administrator is told on the Overview, in their own area.
            Assert.Contains((await site.GetFromJsonAsync<SystemOverview>("/api/system/overview"))!.Attention, a => a.Title == "Addresses are not offered as they are typed" && a.Link == "/connected-systems");
        }
        finally { factory.Suggestions.Configured = true; }

        // PostGrid's previews are read whether or not each one carries its state and zip.
        var previews = PostGridAddressVerifier.ReadPreviews(System.Text.Json.JsonDocument.Parse(
            "{\"status\":\"success\",\"data\":[{\"preview\":{\"address\":\"145 Mulberry St\",\"city\":\"New York\",\"prov\":\"NY\",\"pc\":\"10013-1234\"}},"
            + "{\"preview\":{\"address\":\"145 Mulberry Ave\",\"city\":\"Joliet\",\"pc\":\"60431\"}},{\"preview\":{\"city\":\"Nowhere\"}}]}").RootElement, null);
        Assert.Equal(2, previews.Count);
        Assert.Equal(("145 Mulberry St", "New York", "NY", "10013"), (previews[0].StreetAddress, previews[0].City, previews[0].State, previews[0].Zip));
        Assert.Equal((null, "60431"), (previews[1].State, previews[1].Zip));
        // A state that was asked for is the state of every answer.
        Assert.Equal("IL", PostGridAddressVerifier.ReadPreviews(System.Text.Json.JsonDocument.Parse("{\"data\":[{\"preview\":{\"address\":\"1 Main St\",\"city\":\"Joliet\",\"pc\":\"60431\"}}]}").RootElement, "IL").Single().State);
    }

    [Fact]
    public async Task The_database_is_backed_up_and_only_the_system_administrator_can_get_a_copy()
    {
        var (owner, _, site, _, _) = await SetUpAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/system/backups")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsync("/api/system/backups", null)).StatusCode);

        // Asked for now: a copy is made, checked, and listed. With no storage account set up (as here), it is kept on the site's disk.
        var made = (await (await site.PostAsync("/api/system/backups", null)).Content.ReadFromJsonAsync<BackupsDto>())!;
        Assert.True(made.Last!.Ok, made.Last.Problem);
        Assert.Matches(@"^restaurant-feedback-\d{8}-\d{6}\.db\.gz$", made.Last.Name);
        Assert.True(made.Last.Bytes > 1000);
        // Nothing but the copies themselves is left in the folder.
        using (var scope = factory.Services.CreateScope())
            Assert.All(Directory.GetFiles(scope.ServiceProvider.GetRequiredService<DatabaseBackups>().Folder), f => Assert.EndsWith(".db.gz", f));
        var listed = made.Files.Single(f => f.Name == made.Last.Name);
        Assert.Equal((true, false, made.Last.Bytes), (listed.OnSite, listed.OffSite, listed.Bytes));

        // The copy opens as a database of its own, sound, with the site's people in it.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/system/backups/{made.Last.Name}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await site.GetAsync("/api/system/backups/restaurant-feedback-19990101-000000.db.gz/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await site.GetAsync("/api/system/backups/keys.xml/download")).StatusCode);
        var download = await site.GetAsync($"/api/system/backups/{made.Last.Name}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var restored = Path.Combine(Path.GetTempPath(), $"restored-{Guid.NewGuid():N}.db");
        try
        {
            await using (var zipped = new System.IO.Compression.GZipStream(await download.Content.ReadAsStreamAsync(), System.IO.Compression.CompressionMode.Decompress))
            await using (var file = File.Create(restored))
                await zipped.CopyToAsync(file);
            await using var copy = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = restored, Pooling = false }.ToString());
            await copy.OpenAsync();
            await using var ask = copy.CreateCommand();
            ask.CommandText = "select (select count(*) from Users) || '|' || (select count(*) from Restaurants) || '|' || (select * from pragma_integrity_check limit 1)";
            var parts = ((string)(await ask.ExecuteScalarAsync())!).Split('|');
            Assert.True(int.Parse(parts[0]) >= 2 && int.Parse(parts[1]) >= 1);
            Assert.Equal("ok", parts[2]);
            ask.CommandText = "pragma journal_mode";
            Assert.Equal("delete", (string)(await ask.ExecuteScalarAsync())!);
        }
        finally { if (File.Exists(restored)) File.Delete(restored); }

        // Making a copy and taking one are both audited; after a good copy none is due for an hour.
        Assert.True(await Db(db => Task.FromResult(db.AuditLogs.Any(a => a.Action == "DatabaseBackedUp") && db.AuditLogs.Any(a => a.Action == "DatabaseBackupDownloaded"))));
        using (var scope = factory.Services.CreateScope())
        {
            var backups = scope.ServiceProvider.GetRequiredService<DatabaseBackups>();
            Assert.False(await backups.DueAsync(made.Last.At.AddMinutes(5)));
            Assert.True(await backups.DueAsync(made.Last.At.AddMinutes(61)));
        }
        Assert.DoesNotContain((await site.GetFromJsonAsync<SystemOverview>("/api/system/overview"))!.Attention, a => a.Title.Contains("back", StringComparison.OrdinalIgnoreCase));

        // What is kept: every copy for two days, then the first copy of each day for three months, then the first of each month always.
        string N(string stamp) => $"restaurant-feedback-{stamp}.db.gz";
        var now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        var all = new[]
        {
            N("20261127-110000"), N("20261127-100000"), N("20261126-090000"),   // the last two days: all kept
            N("20261124-080000"), N("20261124-200000"),                         // older: only the day's first
            N("20260915-080000"),                                               // inside three months: the day's first
            N("20260801-080000"), N("20260802-080000"),                         // older than three months: only the month's first
            N("20260720-080000"), "notes.txt"
        };
        Assert.Equal(new[] { N("20260720-080000"), N("20260801-080000"), N("20260915-080000"), N("20261124-080000"), N("20261126-090000"), N("20261127-100000"), N("20261127-110000") },
            DatabaseBackups.Kept(all, now, hourlyHours: 48, dailyDays: 92).Order());
        // The site's own disk keeps the two days and a week of daily copies, and always the newest copy.
        Assert.Equal(new[] { N("20261124-080000"), N("20261126-090000"), N("20261127-100000"), N("20261127-110000") }, DatabaseBackups.Kept(all, now, 48, 7, monthly: false).Order());
        Assert.Equal(new[] { N("20260720-080000") }, DatabaseBackups.Kept([N("20260720-080000")], now, 48, 7, monthly: false));
    }

    [Fact]
    public async Task An_address_the_mailing_service_refuses_is_corrected_from_the_mailing_screen_and_the_card_then_goes()
    {
        var (owner, _, _, rid, _) = await SetUpAsync();
        var request = Card(rid, "Fixable");
        request.StreetAddress = "12 Nowhere Ln";
        var card = await Create(owner, request);
        try
        {
            // The mailing service does not take the address. The answer says so in the group's words, and marks it as
            // something correcting the address will put right.
            var refused = (await Mail(owner, [card.Id])).Single();
            Assert.Equal((false, true), (refused.Ok, refused.AddressProblem));
            Assert.DoesNotContain("PostGrid", refused.Error);
            Assert.Contains("address", refused.Error);

            // Corrected by the person sending it: the whole address is needed, only the address changes, and it is checked again.
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/comments/{card.Id}/address", new CardAddressRequest("9 Mailer Rd", "Joliet", "IL", " "))).StatusCode);
            var fixedCard = await owner.PutAsJsonAsync($"/api/comments/{card.Id}/address", new CardAddressRequest("  9  Mailer Rd ", "Joliet", "IL", "60431"));
            Assert.Equal(HttpStatusCode.OK, fixedCard.StatusCode);
            var now = (await fixedCard.Content.ReadFromJsonAsync<CommentSummary>())!;
            Assert.Equal(("9 Mailer Rd", "Joliet", "IL", "60431", "Verified", "Fixable"), (now.StreetAddress, now.City, now.State, now.Zip, now.AddressStatus, now.GuestLastName));
            var audited = await Db(db => Task.FromResult(db.AuditLogs.Where(a => a.Action == "CardAddressCorrected" && a.EntityId == card.Id.ToString()).OrderByDescending(a => a.Id).First()));
            Assert.Contains("Nowhere", audited.Details);
            Assert.Contains("9 Mailer Rd", audited.Details);

            // Sent again, it goes; and an address is not changed under a card that is already with the mailing service.
            Assert.True((await Mail(owner, [card.Id])).Single().Ok);
            var late = await owner.PutAsJsonAsync($"/api/comments/{card.Id}/address", new CardAddressRequest("1 Other St", "Joliet", "IL", "60431"));
            Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
            Assert.Contains("already with the mailing service", await late.Content.ReadAsStringAsync());
        }
        finally
        {
            await Db(async db => { db.Comments.RemoveRange(db.Comments.Where(c => c.Id == card.Id)); return await db.SaveChangesAsync(); });
        }

        // Which refusals are about the address.
        Assert.True(MailStatuses.IsAboutTheAddress("PostGrid refused the card (422: the recipient's address could not be verified)."));
        Assert.True(MailStatuses.IsAboutTheAddress("Undeliverable"));
        Assert.False(MailStatuses.IsAboutTheAddress("PostGrid refused the card (402: payment required)."));
        Assert.False(MailStatuses.IsAboutTheAddress("PostGrid could not be reached."));
    }

    [Fact]
    public async Task A_card_can_be_sent_again_and_every_attempt_is_kept_with_the_address_it_used()
    {
        var (owner, _, site, rid, groupId) = await SetUpAsync(LiveKey);
        await SaveSettings(site, s => { s.CostCents = 90; s.FeeCents = 10; });
        Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/billing/groups/{groupId}", new GroupBillingTermsRequest(null, 1_000_000, true))).StatusCode);
        var card = await Create(owner, Card(rid, "Again"));
        async Task<List<CardSendDto>> Sends() => (await owner.GetFromJsonAsync<List<CardSendDto>>($"/api/comments/{card.Id}/sends"))!;
        async Task<int> Used() => (await owner.GetFromJsonAsync<PrintConfig>("/api/comments/print-config"))!.MailUsedCents;
        async Task<CommentSummary> Now() => (await owner.GetFromJsonAsync<CommentSummary>($"/api/comments/{card.Id}"))!;
        async Task<MailOutcome> SendAgain() =>
            (await (await owner.PostAsJsonAsync("/api/comments/mail", new MailRequest([card.Id], 1, await Price(owner)) { Resend = true })).Content.ReadFromJsonAsync<List<MailOutcome>>())!.Single();
        try
        {
            var usedBefore = await Used();
            Assert.True((await Mail(owner, [card.Id])).Single().Ok);
            var first = Assert.Single(await Sends());
            Assert.Equal((1, "mail", "sent", false, "9 Mailer Rd", 100, "ready"), (first.Number, first.Method, first.Outcome, first.Resend, first.StreetAddress, first.BilledCents, first.MailStatus));
            var sentFirst = (await Now()).SentAt;
            Assert.True(sentFirst is not null && !(await Now()).Responded && (await Now()).InMailAt is null);

            // Asked for again by mistake (not as a resend), nothing more goes. As a resend, not while the first card can still be cancelled.
            Assert.True((await Mail(owner, [card.Id])).Single().AlreadyMailed);
            var tooSoon = await SendAgain();
            Assert.False(tooSoon.Ok);
            Assert.Contains("has not been printed yet", tooSoon.Error);
            Assert.Single(await Sends());
            Assert.Equal(usedBefore + 100, await Used());

            // The mailing service moves the card along, and the site finds out by itself: nobody presses "Check status".
            var mailId = await Db(async db => (await db.Comments.FindAsync(card.Id))!.MailId!);
            factory.Mail.SetStatus(mailId, "completed");
            var refreshed = await owner.PostAsync("/api/comments/mail/refresh", null);
            Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
            Assert.Equal("completed", (await Now()).MailStatus);
            Assert.Equal("completed", (await Sends()).Single().MailStatus);
            // That is when the card really went into the mail, and the site notes the time, on the card and on the attempt.
            var inMail = (await Now()).InMailAt;
            Assert.True(inMail >= sentFirst && inMail <= DateTime.UtcNow);
            Assert.Equal(inMail, (await Sends()).Single().InMailAt);
            await owner.PostAsync("/api/comments/mail/status", JsonContent.Create(new CardIdsRequest([card.Id])));
            Assert.Equal(inMail, (await Now()).InMailAt); // noted once, never moved

            // It never arrived: the address is corrected, and the card is sent again. Both attempts are kept, each with the address it used.
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/comments/{card.Id}/address", new CardAddressRequest("10 Mailer Rd", "Joliet", "IL", "60431"))).StatusCode);
            Assert.True((await SendAgain()).Ok);
            var two = await Sends();
            Assert.Equal(new[] { (1, "9 Mailer Rd", false, "completed"), (2, "10 Mailer Rd", true, "ready") }, two.Select(s => (s.Number, s.StreetAddress!, s.Resend, s.MailStatus!)));
            var after = await Now();
            // Sent again: the new send is the one the card goes by, and it is not in the mail yet. Still nobody has replied.
            Assert.Equal((2, false), (after.Sends, after.Responded));
            Assert.True(after.SentAt > sentFirst && after.InMailAt is null);
            // Each card that goes is paid for.
            Assert.Equal(usedBefore + 200, await Used());
            Assert.Contains("\"sentAgain\":true", await Db(db => Task.FromResult(db.AuditLogs.Where(a => a.Action == "CardMailed" && a.EntityId == card.Id.ToString()).OrderByDescending(a => a.Id).First().Details!)));

            // The second card is called off before printing: it is not billed, the first still counts, and the card stays "sent".
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/comments/{card.Id}/mail/cancel", null)).StatusCode);
            Assert.Equal(new[] { "sent", "cancelled" }, (await Sends()).Select(s => s.Outcome));
            Assert.Equal(usedBefore + 100, await Used());
            var back = await Now();
            Assert.Equal((sentFirst, inMail, 1), (back.SentAt, back.InMailAt, back.Sends));

            // Sent again by printing it here for a stamp: one more line in the history, with the address used.
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/comments/printed", new PrintedRequest([card.Id], true, "stamp") { Resend = true })).StatusCode);
            var last = (await Sends())[^1];
            Assert.Equal((3, "stamp", "sent", true, "10 Mailer Rd", 0), (last.Number, last.Method, last.Outcome, last.Resend, last.StreetAddress, last.BilledCents));
            // Printing it once more for a jam, not as a resend, adds nothing to the history.
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/comments/printed", new PrintedRequest([card.Id], true, "stamp"))).StatusCode);
            Assert.Equal(3, (await Sends()).Count);
        }
        finally
        {
            await Db(async db => { db.Comments.RemoveRange(db.Comments.Where(c => c.Id == card.Id)); return await db.SaveChangesAsync(); });
        }
    }
}
