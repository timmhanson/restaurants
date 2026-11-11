using System.Net;
using System.Net.Http.Json;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Tests;

/// <summary>How a comment came in (SMG survey, Culvers.com, phone) and passwords set without a forced change.</summary>
[Collection(ApiCollection.Name)]
public class SourceAndPasswordTests(ApiFactory factory)
{
    private static CommentRequest Card(int rid, string? source, string? phone, string last) => new()
    {
        RestaurantId = rid, Source = source, GuestFirstName = "Sam", GuestLastName = last, Phone = phone, StreetAddress = "9 Source Rd",
        Zip = "60435", VisitDate = DateOnly.Parse("2026-11-04"), Reason = "Order mistake"
    };

    [Fact]
    public async Task Every_new_card_records_how_it_came_in_and_phone_calls_need_a_number()
    {
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        var rid = (await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants"))!.First().Id;

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/comments", Card(rid, null, "815-555-8801", "Nosource"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/comments", Card(rid, "Carrier pigeon", "815-555-8802", "Pigeon"))).StatusCode);
        var noPhone = await owner.PostAsJsonAsync("/api/comments", Card(rid, "Phone call", null, "Nophone"));
        Assert.Equal(HttpStatusCode.BadRequest, noPhone.StatusCode);
        Assert.Contains("phone number", await noPhone.Content.ReadAsStringAsync());

        // Case is forgiven and stored in the canonical spelling.
        var call = await owner.PostAsJsonAsync("/api/comments", Card(rid, "phone CALL", "815-555-8803", "Caller"));
        Assert.Equal(HttpStatusCode.Created, call.StatusCode);
        var card = (await call.Content.ReadFromJsonAsync<CommentSummary>())!;
        Assert.Equal(CommentSources.Phone, card.Source);

        // The website form does not need a phone number (the address is enough).
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/comments", Card(rid, "Culvers.com Contact Us", null, "Webform"))).StatusCode);

        // Editing keeps the rule.
        var edit = Card(rid, "Phone call", null, "Caller");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/comments/{card.Id}", edit)).StatusCode);

        var analytics = await owner.GetFromJsonAsync<AnalyticsDto>("/api/analytics");
        Assert.Contains(analytics!.BySource, s => s.Label == CommentSources.Phone && s.Count >= 1);
        var csv = await owner.GetStringAsync("/api/comments/export");
        Assert.Contains("Came In Via", csv);
    }

    [Fact]
    public async Task Administrators_can_set_a_password_that_does_not_have_to_be_changed()
    {
        var system = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword);
        var group = (await (await system.PostAsJsonAsync("/api/groups", new GroupRequest
        {
            Name = "No Expiry Group",
            Admins = [new UserRequest { Username = "noexp-first", DisplayName = "First Admin", Password = "TempPass!1", Role = "GroupAdmin" }],
            Restaurants = [new RestaurantRequest { Name = "Culver's of Plainfield" }]
        })).Content.ReadFromJsonAsync<GroupDto>())!;
        var inGroup = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword, group.Id);

        var created = await inGroup.PostAsJsonAsync("/api/users", new UserRequest
        {
            Username = "noexp-admin", DisplayName = "Kept Password", Email = "noexp@test.local", Password = "KeptPass!1", Role = "GroupAdmin",
            RequirePasswordChange = false
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var login = await (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("noexp-admin", "KeptPass!1")))
            .Content.ReadFromJsonAsync<LoginResponse>();
        Assert.False(login!.MustChangePassword);
        var admin = await factory.LoginAsync("noexp-admin", "KeptPass!1");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/restaurants")).StatusCode);

        // The default still forces a change.
        var userId = (await created.Content.ReadFromJsonAsync<UserDto>())!.Id;
        Assert.Equal(HttpStatusCode.NoContent, (await inGroup.PutAsJsonAsync($"/api/users/{userId}/password", new SetPasswordRequest("ResetPass!1"))).StatusCode);
        login = await (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("noexp-admin", "ResetPass!1")))
            .Content.ReadFromJsonAsync<LoginResponse>();
        Assert.True(login!.MustChangePassword);
        Assert.Equal(HttpStatusCode.NoContent, (await inGroup.PutAsJsonAsync($"/api/users/{userId}/password", new SetPasswordRequest("KeptPass!2", RequirePasswordChange: false))).StatusCode);
        login = await (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("noexp-admin", "KeptPass!2")))
            .Content.ReadFromJsonAsync<LoginResponse>();
        Assert.False(login!.MustChangePassword);

        // Owners cannot waive it.
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        var rid = (await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants"))!.First().Id;
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/users", new UserRequest
        {
            Username = "noexp-mgr", DisplayName = "Owner's Manager", Password = "MgrPass!1", Role = "Manager", RestaurantIds = [rid], RequirePasswordChange = false
        })).StatusCode);
        login = await (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("noexp-mgr", "MgrPass!1")))
            .Content.ReadFromJsonAsync<LoginResponse>();
        Assert.True(login!.MustChangePassword);
    }
}
