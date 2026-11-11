using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RestaurantFeedback.Api.Dtos;

namespace RestaurantFeedback.Api.Tests;

/// <summary>Managers confirming replies, sharing notes, and roll-up reports across restaurants.</summary>
[Collection(ApiCollection.Name)]
public class FollowUpTests(ApiFactory factory)
{
    private async Task<HttpClient> SignInNewAsync(string username, string temp)
    {
        var client = factory.CreateClient();
        var login = (await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, temp))).Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        if (login.MustChangePassword)
        {
            Assert.Equal(HttpStatusCode.OK, (await factory.ChangePasswordAsync(client, temp, temp + "x")).StatusCode);
            return await factory.LoginAsync(username, temp + "x");
        }
        return client;
    }

    [Fact]
    public async Task Managers_confirm_replies_share_notes_and_see_roll_ups()
    {
        var system = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword);
        var group = (await (await system.PostAsJsonAsync("/api/groups", new GroupRequest
        {
            Name = "Follow-up Group",
            Admins = [new UserRequest { Username = "fu-admin", DisplayName = "FU Admin", Email = "fu-admin@test.local", Password = "Admin!1234", Role = "GroupAdmin" }],
            Restaurants = [new RestaurantRequest { Name = "Culver's of Frankfort" }, new RestaurantRequest { Name = "Culver's of Orland" }]
        })).Content.ReadFromJsonAsync<GroupDto>())!;
        var admin = await SignInNewAsync("fu-admin", "Admin!1234");
        var rs = (await admin.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants?all=true"))!;
        int frankfort = rs.Single(r => r.Name.EndsWith("Frankfort")).Id, orland = rs.Single(r => r.Name.EndsWith("Orland")).Id;

        // Maria manages both restaurants and may mark replies; Lee only views Frankfort.
        await admin.PostAsJsonAsync("/api/users", new UserRequest
        {
            Username = "fu-maria", DisplayName = "Maria", Email = "maria@test.local", Password = "Maria!1234", Role = "Manager",
            Permissions = ["ViewComments", "MarkResponded", "ViewAnalytics"], RestaurantIds = [frankfort, orland]
        });
        await admin.PostAsJsonAsync("/api/users", new UserRequest
        {
            Username = "fu-lee", DisplayName = "Lee", Email = "lee@test.local", Password = "Lee!12345", Role = "Manager",
            Permissions = ["ViewComments"], RestaurantIds = [frankfort]
        });
        var maria = await SignInNewAsync("fu-maria", "Maria!1234");
        var lee = await SignInNewAsync("fu-lee", "Lee!12345");

        // The same guest sends cards to both restaurants.
        CommentRequest Card(int rid, string date, string reason) => new()
        {
            RestaurantId = rid, GuestFirstName = "Dana", GuestLastName = "Guest", Phone = "708-555-0101", VisitDate = DateOnly.Parse(date), Reason = reason, Force = true
        };
        var c1 = (await (await admin.PostAsJsonAsync("/api/comments", Card(frankfort, "2026-10-01", "Wait time"))).Content.ReadFromJsonAsync<CommentSummary>())!;
        var c2 = (await (await admin.PostAsJsonAsync("/api/comments", Card(orland, "2026-10-20", "Order mistake"))).Content.ReadFromJsonAsync<CommentSummary>())!;

        // Lee leaves a note for the other managers; he cannot record a reply.
        var note = await lee.PostAsJsonAsync($"/api/comments/{c1.Id}/followup", new FollowUpRequest { Kind = "Note", Text = "Guest is a regular, ask for Pat." });
        Assert.Equal(HttpStatusCode.OK, note.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await lee.PostAsJsonAsync($"/api/comments/{c1.Id}/followup", new FollowUpRequest { Kind = "Replied", Method = "Phone call", Confirmed = true })).StatusCode);

        // Maria sees the note, tries a call, then confirms the reply.
        var seen = (await maria.GetFromJsonAsync<CommentSummary>($"/api/comments/{c1.Id}"))!;
        Assert.Equal(1, seen.NoteCount);
        Assert.Equal("Lee", seen.LatestNote!.CreatedByName);
        Assert.Equal(HttpStatusCode.OK, (await maria.PostAsJsonAsync($"/api/comments/{c1.Id}/followup",
            new FollowUpRequest { Kind = "Attempt", Method = "Phone call", Text = "Left a voicemail" })).StatusCode);
        var attempted = (await maria.GetFromJsonAsync<PagedResult<CommentSummary>>("/api/comments?status=attempted"))!;
        Assert.Contains(attempted.Items, c => c.Id == c1.Id);

        Assert.Equal(HttpStatusCode.BadRequest, (await maria.PostAsJsonAsync($"/api/comments/{c1.Id}/followup",
            new FollowUpRequest { Kind = "Replied", Method = "Phone call" })).StatusCode); // not confirmed
        Assert.Equal(HttpStatusCode.BadRequest, (await maria.PostAsJsonAsync($"/api/comments/{c1.Id}/followup",
            new FollowUpRequest { Kind = "Replied", Method = "Phone call", Confirmed = true, ContactedOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5) })).StatusCode);
        var replied = await maria.PostAsJsonAsync($"/api/comments/{c1.Id}/followup",
            new FollowUpRequest { Kind = "Replied", Method = "Mailed coupon card", Confirmed = true, Text = "Mailed a Free Value Basket card." });
        Assert.Equal(HttpStatusCode.OK, replied.StatusCode);
        var after = (await replied.Content.ReadFromJsonAsync<FollowUpResponse>())!;
        Assert.True(after.Comment.Responded);
        Assert.Equal("Mailed coupon card", after.Comment.ReplyMethod);
        Assert.Equal(["Note", "Attempt", "Replied"], after.Activity.Select(a => a.Kind));
        Assert.Equal(HttpStatusCode.BadRequest, (await maria.PostAsJsonAsync($"/api/comments/{c1.Id}/followup",
            new FollowUpRequest { Kind = "Replied", Method = "Email", Confirmed = true })).StatusCode); // already replied

        // Reopening needs a reason that the other managers can read.
        Assert.Equal(HttpStatusCode.BadRequest, (await maria.PostAsJsonAsync($"/api/comments/{c2.Id}/followup", new FollowUpRequest { Kind = "Reopened", Text = "x" })).StatusCode);
        await maria.PostAsJsonAsync($"/api/comments/{c2.Id}/followup", new FollowUpRequest { Kind = "Replied", Method = "Email", Confirmed = true });
        Assert.Equal(HttpStatusCode.BadRequest, (await maria.PostAsJsonAsync($"/api/comments/{c2.Id}/followup", new FollowUpRequest { Kind = "Reopened" })).StatusCode);
        var reopened = (await (await maria.PostAsJsonAsync($"/api/comments/{c2.Id}/followup",
            new FollowUpRequest { Kind = "Reopened", Text = "Email bounced." })).Content.ReadFromJsonAsync<FollowUpResponse>())!;
        Assert.False(reopened.Comment.Responded);
        var history = (await lee.GetFromJsonAsync<List<ActivityDto>>($"/api/comments/{c1.Id}/activity"))!;
        Assert.Contains(history, a => a.Kind == "Replied" && a.CreatedByName == "Maria" && a.CreatedByRole == "Manager");

        // Roll-up across Maria's two restaurants, with a row per restaurant.
        var roll = (await maria.GetFromJsonAsync<AnalyticsDto>("/api/analytics"))!;
        Assert.Equal(2, roll.TotalComments);
        Assert.Equal(2, roll.Restaurants.Count);
        Assert.Equal(1, roll.CrossRestaurantGuests);
        Assert.Equal(100, roll.Restaurants.Single(r => r.RestaurantId == frankfort).ResponseRate);
        Assert.Equal(1, roll.Restaurants.Single(r => r.RestaurantId == orland).Waiting);
        Assert.Contains(roll.ByReplyMethod, m => m.Label == "Mailed coupon card");
        var one = (await maria.GetFromJsonAsync<AnalyticsDto>($"/api/analytics?restaurantIds={orland}"))!;
        Assert.Equal(1, one.TotalComments);
        Assert.Equal(HttpStatusCode.Forbidden, (await lee.GetAsync($"/api/analytics?restaurantIds={frankfort},{orland}")).StatusCode);
        Assert.NotEqual(0, group.Id);
    }
}
