using System.Net;
using System.Net.Http.Json;
using RestaurantFeedback.Api.Controllers;
using RestaurantFeedback.Api.Dtos;

namespace RestaurantFeedback.Api.Tests;

/// <summary>Every interaction is logged with its group; only System Administrators can read the log and the reports.</summary>
[Collection(ApiCollection.Name)]
public class InteractionLogTests(ApiFactory factory)
{
    private static async Task<InteractionPage> WaitForAsync(HttpClient admin, string query, Func<InteractionPage, bool> done)
    {
        InteractionPage? page = null;
        for (var i = 0; i < 40; i++)
        {
            page = await admin.GetFromJsonAsync<InteractionPage>("/api/system/activity?" + query);
            if (done(page!)) return page!;
            await Task.Delay(150);
        }
        return page!;
    }

    [Fact]
    public async Task Api_calls_and_browser_events_are_logged_by_group_for_the_system_administrator_only()
    {
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        var me = (await owner.GetFromJsonAsync<UserDto>("/api/auth/me"))!;
        var rid = (await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants"))!.First().Id;
        await owner.GetAsync($"/api/comments?restaurantId={rid}&search=logtest");
        var sent = await owner.PostAsJsonAsync("/api/activity", new ActivityBatch("tab-123", [
            new ActivityEvent("Page", "/comments", "Comments", null, DateTime.UtcNow, null),
            new ActivityEvent("Click", "/comments", "Reply queue", "button", DateTime.UtcNow, rid),
            new ActivityEvent("Bogus", "/x", null, null, null, null)
        ]));
        Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);

        // Nobody but the System Administrator can read it.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/system/activity")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/system/reports")).StatusCode);

        var admin = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword);
        var page = await WaitForAsync(admin, $"groupId={me.GroupId}&userId={me.Id}&pageSize=200",
            p => p.Items.Any(i => i.Kind == "Click" && i.Action == "Reply queue") && p.Items.Any(i => i.Path.Contains("search=logtest")));
        var call = page.Items.First(i => i.Path.Contains("search=logtest"));
        Assert.Equal("Api", call.Kind);
        Assert.Equal("GET", call.Method);
        Assert.Equal(200, call.Status);
        Assert.Equal(rid, call.RestaurantId);
        Assert.Equal(me.GroupId, call.GroupId);
        Assert.Equal("Restaurant Owner", call.RoleTitle);
        var click = page.Items.First(i => i.Kind == "Click");
        Assert.Equal("tab-123", click.SessionId);
        Assert.Equal(rid, click.RestaurantId);
        Assert.Contains(page.Items, i => i.Kind == "Page" && i.Path == "/comments");
        Assert.DoesNotContain(page.Items, i => i.Path == "/x");
        // The forbidden attempts are in there too.
        Assert.Contains(page.Items, i => i.Path == "/api/system/activity" && i.Status == 403);

        // Sign-ins carry who it was even though there was no token yet, and failures are kept.
        await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(ApiFactory.OwnerUser, "wrong-password"));
        var logins = await WaitForAsync(admin, $"userId={me.Id}&search=Login&pageSize=200", p => p.Items.Any(i => i.Action == "LoginFailed"));
        Assert.Contains(logins.Items, i => i.Action == "Login" && i.UserName == ApiFactory.OwnerUser);
        Assert.Contains(logins.Items, i => i.Action == "LoginFailed" && i.Status == 401);

        var csv = await admin.GetStringAsync($"/api/system/activity/export?groupId={me.GroupId}");
        Assert.StartsWith("﻿Time (UTC),Kind,User", csv);
    }

    [Fact]
    public async Task System_administrator_reports_roll_up_by_group_and_restaurant()
    {
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        var me = (await owner.GetFromJsonAsync<UserDto>("/api/auth/me"))!;
        var rid = (await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants"))!.First().Id;
        var visit = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-10);
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/comments", new CommentRequest
        {
            RestaurantId = rid, Source = "Phone call", GuestFirstName = "Report", GuestLastName = "Guest", Phone = "815-555-9191", VisitDate = visit, Reason = "Wait time", Force = true
        })).StatusCode);

        var admin = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword);
        var catalog = (await admin.GetFromJsonAsync<SystemCatalog>("/api/system/catalog"))!;
        Assert.Contains(catalog.Restaurants, r => r.Id == rid && r.GroupId == me.GroupId);

        SystemReport? report = null;
        for (var i = 0; i < 40 && (report is null || report.ActiveUsers == 0); i++)
        {
            report = await admin.GetFromJsonAsync<SystemReport>($"/api/system/reports?groupIds={me.GroupId}");
            await Task.Delay(150);
        }
        var group = Assert.Single(report!.ByGroup);
        Assert.Equal(me.GroupId, group.Id);
        Assert.True(group.Stats.Cards >= 1 && group.Stats.Phone >= 1 && group.Stats.WaitingOverAWeek >= 1);
        Assert.True(group.ActiveUsers >= 1 && group.Interactions >= 1);
        Assert.Contains(report.ByRestaurant, r => r.Id == rid && r.Stats.Cards >= 1);
        Assert.Contains(report.TopUsers, u => u.Id == me.Id && u.CardsEntered >= 1);
        Assert.Contains(report.Daily, d => d.GroupId == me.GroupId && d.Cards >= 1);

        var single = (await admin.GetFromJsonAsync<SystemReport>($"/api/system/reports?restaurantIds={rid}"))!;
        Assert.Equal([rid], single.ByRestaurant.Select(r => r.Id));
    }

    [Fact]
    public async Task Pages_and_api_responses_are_never_cached()
    {
        var client = factory.CreateClient();
        var res = await client.GetAsync("/api/health");
        Assert.Contains("no-store", res.Headers.CacheControl!.ToString());
        Assert.True(res.Headers.Contains("X-App-Version"));

        var page = await client.GetAsync("/comments");
        if (page.StatusCode == HttpStatusCode.OK) // only when the front end has been built into wwwroot
            Assert.Contains("no-store", page.Headers.CacheControl!.ToString());
    }
}
