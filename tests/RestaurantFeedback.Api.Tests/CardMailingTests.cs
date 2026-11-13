using System.Net;
using System.Net.Http.Json;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Tests;

/// <summary>Printing coupon cards (stamp by hand or bought postage), marking them sent, redeeming, and following up.</summary>
[Collection(ApiCollection.Name)]
public class CardMailingTests(ApiFactory factory)
{
    private static int _n;
    private static CommentRequest Card(int rid, string last, int daysAgo = 2, string? zip = "60431") => new()
    {
        RestaurantId = rid, Source = "SMG survey", GuestFirstName = "Mail", GuestLastName = last, Phone = $"815-555-{7000 + Interlocked.Increment(ref _n)}",
        StreetAddress = "42 Postcard Ln", City = "Joliet", State = "IL", Zip = zip,
        VisitDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-daysAgo), Reason = "Order mistake", CouponOffer = "Free Value Basket", Force = true
    };

    private async Task<(HttpClient Owner, int Rid)> OwnerAsync()
    {
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        var r = (await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants"))!.First();
        // Postage needs the restaurant's full address as the return address.
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/restaurants/{r.Id}", new RestaurantRequest
        {
            Name = r.Name, StreetAddress = "1 Test St", City = "Joliet", State = "IL", Zip = "60431", Phone = "815-555-0000"
        })).StatusCode);
        return (owner, r.Id);
    }

    private static async Task<CommentSummary> Create(HttpClient client, CommentRequest card)
    {
        var res = await client.PostAsJsonAsync("/api/comments", card);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<CommentSummary>())!;
    }

    [Fact]
    public async Task Printing_a_card_marks_it_sent_once_and_keeps_a_history()
    {
        var (owner, rid) = await OwnerAsync();
        var card = await Create(owner, Card(rid, "Printed"));
        Assert.Contains("mailed", (await owner.GetFromJsonAsync<PrintConfig>("/api/comments/print-config"))!.MailedMethod, StringComparison.OrdinalIgnoreCase);

        var printed = (await (await owner.PostAsJsonAsync("/api/comments/printed", new PrintedRequest([card.Id]))).Content.ReadFromJsonAsync<List<CommentSummary>>())!.Single();
        Assert.True(printed.Responded);
        Assert.Equal("Mailed coupon card", printed.ReplyMethod);
        Assert.Equal(1, printed.PrintCount);
        Assert.NotNull(printed.PrintedAt);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5), printed.FollowUpDueOn); // default: follow up after 5 days

        // A reprint is counted but does not record a second reply.
        printed = (await (await owner.PostAsJsonAsync("/api/comments/printed", new PrintedRequest([card.Id]))).Content.ReadFromJsonAsync<List<CommentSummary>>())!.Single();
        Assert.Equal(2, printed.PrintCount);
        var history = (await owner.GetFromJsonAsync<List<ActivityDto>>($"/api/comments/{card.Id}/activity"))!;
        Assert.Equal(2, history.Count(a => a.Kind == "Printed"));
        Assert.Single(history, a => a.Kind == "Replied");
        Assert.Contains("stamp added by hand", history.First(a => a.Kind == "Printed").Text);

        // The list can pull exactly the cards chosen for printing.
        var chosen = await owner.GetFromJsonAsync<PagedResult<CommentSummary>>($"/api/comments?ids={card.Id}");
        Assert.Equal([card.Id], chosen!.Items.Select(c => c.Id));
    }

    [Fact]
    public async Task Postage_is_bought_once_per_card_only_by_people_allowed_to_spend()
    {
        var (owner, rid) = await OwnerAsync();
        var good = await Create(owner, Card(rid, "Stamped"));
        var noZip = await Create(owner, Card(rid, "Nozip", zip: null));
        var bad = await Create(owner, Card(rid, "Undeliverable"));
        var before = factory.Postage.Bought.Count;

        var outcomes = (await (await owner.PostAsJsonAsync("/api/comments/postage", new CardIdsRequest([good.Id, noZip.Id, bad.Id]))).Content.ReadFromJsonAsync<List<PostageOutcome>>())!;
        var ok = outcomes.Single(o => o.CommentId == good.Id);
        Assert.True(ok.Ok && ok.Cents == 65 && ok.Test && !ok.AlreadyBought);
        Assert.StartsWith("https://labels.test/", ok.LabelUrl);
        Assert.Contains("zip", outcomes.Single(o => o.CommentId == noZip.Id).Error);
        Assert.Contains("Address not found", outcomes.Single(o => o.CommentId == bad.Id).Error);
        Assert.Equal(before + 1, factory.Postage.Bought.Count);
        Assert.Equal("1 Test St", factory.Postage.Bought.Last().From.Street);

        // Asking again never charges twice.
        var again = (await (await owner.PostAsJsonAsync("/api/comments/postage", new CardIdsRequest([good.Id]))).Content.ReadFromJsonAsync<List<PostageOutcome>>())!.Single();
        Assert.True(again.Ok && again.AlreadyBought);
        Assert.Equal(before + 1, factory.Postage.Bought.Count);
        var printed = (await (await owner.PostAsJsonAsync("/api/comments/printed", new PrintedRequest([good.Id], Postage: "bought"))).Content.ReadFromJsonAsync<List<CommentSummary>>())!.Single();
        Assert.Equal(65, printed.PostageCents);
        Assert.True(printed.PostageTest);

        // A manager needs the BuyPostage permission to spend money, though they can still print.
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/users", new UserRequest
        {
            Username = "mail-mgr", DisplayName = "Mail Manager", Password = "MgrPass!1", Role = "Manager", RestaurantIds = [rid], Permissions = ["ViewComments", "MarkResponded"]
        })).StatusCode);
        var manager = await factory.LoginAsync("mail-mgr", "MgrPass!1");
        Assert.False((await manager.GetFromJsonAsync<PrintConfig>("/api/comments/print-config"))!.CanBuyPostage);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync("/api/comments/postage", new CardIdsRequest([noZip.Id]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.PostAsJsonAsync("/api/comments/printed", new PrintedRequest([noZip.Id]))).StatusCode);
    }

    [Fact]
    public async Task A_coupon_can_be_redeemed_once_and_taken_back_with_a_reason()
    {
        var (owner, rid) = await OwnerAsync();
        var card = await Create(owner, Card(rid, "Redeemer"));
        await owner.PostAsJsonAsync("/api/comments/printed", new PrintedRequest([card.Id]));

        var redeemed = (await (await owner.PostAsJsonAsync($"/api/comments/{card.Id}/redeem", new RedeemRequest(null, "Used at the drive-thru"))).Content.ReadFromJsonAsync<FollowUpResponse>())!;
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), redeemed.Comment.RedeemedOn);
        Assert.Null(redeemed.Comment.FollowUpDueOn); // it clearly arrived
        Assert.Contains(redeemed.Activity, a => a.Kind == "Redeemed" && a.Text == "Used at the drive-thru");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/comments/{card.Id}/redeem", new RedeemRequest(null, null))).StatusCode);

        Assert.Contains((await owner.GetFromJsonAsync<PagedResult<CommentSummary>>("/api/comments?status=redeemed&pageSize=200"))!.Items, c => c.Id == card.Id);
        Assert.True((await owner.GetFromJsonAsync<AnalyticsDto>("/api/analytics"))!.Redeemed >= 1);
        Assert.Contains("Redeemed On", await owner.GetStringAsync("/api/comments/export"));

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/comments/{card.Id}/unredeem", new UndoRequest(null))).StatusCode);
        var undone = (await (await owner.PostAsJsonAsync($"/api/comments/{card.Id}/unredeem", new UndoRequest("Wrong guest"))).Content.ReadFromJsonAsync<FollowUpResponse>())!;
        Assert.Null(undone.Comment.RedeemedOn);
    }

    [Fact]
    public async Task Guests_are_due_a_follow_up_after_the_restaurants_days_and_the_call_is_noted()
    {
        var (owner, rid) = await OwnerAsync();
        var card = await Create(owner, Card(rid, "Followup", daysAgo: 12));
        var sentOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-8);
        // Mailed eight days ago: past the default five days.
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/comments/{card.Id}/followup",
            new FollowUpRequest { Kind = "Replied", Method = "Mailed coupon card", ContactedOn = sentOn, Confirmed = true })).StatusCode);

        var summary = (await owner.GetFromJsonAsync<FollowUpSummary>("/api/follow-ups/summary"))!;
        Assert.True(summary.Due >= 1);
        Assert.Equal(5, summary.DefaultDays);
        Assert.Contains(summary.Restaurants, r => r.Id == rid && r.EffectiveDays == 5 && r.CanSet);
        var due = (await owner.GetFromJsonAsync<PagedResult<CommentSummary>>("/api/comments?status=follow-up-due&pageSize=200"))!;
        Assert.Equal(sentOn.AddDays(5), due.Items.Single(c => c.Id == card.Id).FollowUpDueOn);

        // No answer: noted, and still due.
        var attempt = (await (await owner.PostAsJsonAsync($"/api/comments/{card.Id}/followed-up", new FollowedUpRequest(false, "Phone call", null, "Left a voicemail"))).Content.ReadFromJsonAsync<FollowUpResponse>())!;
        Assert.NotNull(attempt.Comment.FollowUpDueOn);
        Assert.Contains(attempt.Activity, a => a.Kind == "FollowUpAttempt" && a.Text == "Left a voicemail");
        // Reaching the guest needs notes about the conversation.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/comments/{card.Id}/followed-up", new FollowedUpRequest(true, "Phone call", null, null))).StatusCode);
        var done = (await (await owner.PostAsJsonAsync($"/api/comments/{card.Id}/followed-up",
            new FollowedUpRequest(true, "Phone call", null, "Card arrived Tuesday. She plans to come in this weekend."))).Content.ReadFromJsonAsync<FollowUpResponse>())!;
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), done.Comment.FollowedUpOn);
        Assert.Null(done.Comment.FollowUpDueOn);
        Assert.Contains(done.Activity, a => a.Kind == "FollowedUp" && a.Method == "Phone call" && a.Text!.Contains("arrived Tuesday"));
        Assert.DoesNotContain((await owner.GetFromJsonAsync<PagedResult<CommentSummary>>("/api/comments?status=follow-up-due&pageSize=200"))!.Items, c => c.Id == card.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/comments/{card.Id}/followed-up", new FollowedUpRequest(true, null, null, "Again"))).StatusCode);

        // Timing: owners set it; managers only with the permission. Longer than eight days means a second card is not due yet.
        var second = await Create(owner, Card(rid, "Later", daysAgo: 12));
        await owner.PostAsJsonAsync($"/api/comments/{second.Id}/followup", new FollowUpRequest { Kind = "Replied", Method = "Mailed coupon card", ContactedOn = sentOn, Confirmed = true });
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/users", new UserRequest
        {
            Username = "days-mgr", DisplayName = "Days Manager", Password = "MgrPass!1", Role = "Manager", RestaurantIds = [rid], Permissions = ["ViewComments", "MarkResponded"]
        })).StatusCode);
        var manager = await factory.LoginAsync("days-mgr", "MgrPass!1");
        Assert.True((await manager.GetFromJsonAsync<FollowUpSummary>("/api/follow-ups/summary"))!.Due >= 1);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutAsJsonAsync($"/api/follow-ups/restaurants/{rid}", new FollowUpDaysRequest(10))).StatusCode);
        var set = await (await owner.PutAsJsonAsync($"/api/follow-ups/restaurants/{rid}", new FollowUpDaysRequest(10))).Content.ReadFromJsonAsync<FollowUpRestaurant>();
        Assert.Equal(10, set!.EffectiveDays);
        Assert.DoesNotContain((await owner.GetFromJsonAsync<PagedResult<CommentSummary>>("/api/comments?status=follow-up-due&pageSize=200"))!.Items, c => c.Id == second.Id);

        var managerId = (await owner.GetFromJsonAsync<List<UserDto>>("/api/users"))!.Single(u => u.Username == "days-mgr").Id;
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/users/{managerId}", new UserRequest
        {
            Username = "days-mgr", DisplayName = "Days Manager", Role = "Manager", RestaurantIds = [rid], Permissions = ["ViewComments", "MarkResponded", "SetFollowUpDays"]
        })).StatusCode);
        // Back to the group's default (5) so other tests see the usual timing.
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync($"/api/follow-ups/restaurants/{rid}", new FollowUpDaysRequest(null))).StatusCode);
        Assert.Contains((await owner.GetFromJsonAsync<PagedResult<CommentSummary>>("/api/comments?status=follow-up-due&pageSize=200"))!.Items, c => c.Id == second.Id);
    }
}
