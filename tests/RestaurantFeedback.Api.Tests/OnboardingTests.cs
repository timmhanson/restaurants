using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Dtos;

namespace RestaurantFeedback.Api.Tests;

/// <summary>Getting a new investment group started: the welcome, the steps to confirm, and leaving it for later.</summary>
[Collection(ApiCollection.Name)]
public class OnboardingTests(ApiFactory factory)
{
    [Fact]
    public async Task A_new_groups_administrator_is_welcomed_and_can_set_up_now_or_later()
    {
        var site = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword);
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        const string name = "Onboarding Test Partners";
        var created = await site.PostAsJsonAsync("/api/groups", new GroupRequest
        {
            Name = name,
            Admins = [new UserRequest { Username = "onboard-admin", DisplayName = "Nora New", Email = "nora@test.local", Password = "Onboard!12345", Role = "GroupAdmin", RequirePasswordChange = false }]
        });
        Assert.True(created.IsSuccessStatusCode);
        var groupId = (await created.Content.ReadFromJsonAsync<GroupDto>())!.Id;
        try
        {
            // (A new administrator first chooses their own password; that is not what is being tested here.)
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                foreach (var u in db.Users.Where(u => u.GroupId == groupId)) u.MustChangePassword = false;
                await db.SaveChangesAsync();
            }
            var admin = await factory.LoginAsync("onboard-admin", "Onboard!12345");
            async Task<OnboardingDto> Now() => (await admin.GetFromJsonAsync<OnboardingDto>("/api/onboarding"))!;
            async Task<List<string>> Attention() => (await site.GetFromJsonAsync<SystemOverview>("/api/system/overview"))!.Attention.Select(a => a.Title).ToList();

            // Somebody who does not run a group has nothing to do, and cannot move its set-up on.
            var notMine = (await owner.GetFromJsonAsync<OnboardingDto>("/api/onboarding"))!;
            Assert.Equal(("none", false), (notMine.Status, notMine.CanRun));
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsync("/api/onboarding/finish", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/system/groups/{groupId}/onboarding")).StatusCode);

            // An owner who has not been welcomed yet gets the tour of what they can do, once; the System Administrator never does.
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                (await db.Users.FirstAsync(u => u.Username == ApiFactory.OwnerUser)).WelcomedAt = null;
                await db.SaveChangesAsync();
            }
            Assert.True((await owner.GetFromJsonAsync<OnboardingDto>("/api/onboarding"))!.Tour);
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/onboarding/welcomed", null)).StatusCode);
            Assert.False((await owner.GetFromJsonAsync<OnboardingDto>("/api/onboarding"))!.Tour);
            Assert.False((await site.GetFromJsonAsync<OnboardingDto>("/api/onboarding"))!.Tour);
            // A new group's administrator gets Getting Started instead of the tour, and using it counts as their welcome.
            Assert.False((await Now()).Tour);

            // The System Administrator leaves a welcome note; the new administrator finds it, with what is already there.
            Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"/api/system/groups/{groupId}/onboarding", new SystemOnboardingRequest("Good talking with you. Check the addresses."))).StatusCode);
            var first = await Now();
            Assert.Equal(("new", true, name, false), (first.Status, first.CanRun, first.GroupName, first.MinimumMet));
            Assert.Equal("Good talking with you. Check the addresses.", first.WelcomeNote);
            Assert.NotNull(first.WelcomeNoteBy);
            Assert.Equal(["group", "restaurants", "offers", "sending", "people"], first.Steps.Select(s => s.Id));
            Assert.Equal(["restaurants"], first.Steps.Where(s => s.Required).Select(s => s.Id));
            Assert.NotEmpty(first.Offers);
            Assert.Contains($"{name} has not finished setting up", await Attention());

            // The one thing the site cannot work without is a restaurant: no confirming that step, and no finishing, without one.
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/onboarding/steps/restaurants/confirm", null)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/onboarding/finish", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/onboarding/steps/nonsense/confirm", null)).StatusCode);

            // Looking around first is allowed: nothing is locked, and set-up waits.
            Assert.Equal("open", (await (await admin.PostAsync("/api/onboarding/later", null)).Content.ReadFromJsonAsync<OnboardingDto>())!.Status);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/comments")).StatusCode);

            // Back to it: a step that needs nothing is confirmed, a restaurant is added, and set-up can finish with the rest left for later.
            var group = (await (await admin.PostAsync("/api/onboarding/steps/group/confirm", null)).Content.ReadFromJsonAsync<OnboardingDto>())!;
            Assert.Equal("Nora New", group.Steps.Single(s => s.Id == "group").ConfirmedBy);
            Assert.True((await admin.PostAsJsonAsync("/api/restaurants", new RestaurantRequest { Name = "Onboarding Diner", StreetAddress = "1 First St", City = "Joliet", State = "IL", Zip = "60431" })).IsSuccessStatusCode);
            var withPlace = await Now();
            Assert.True(withPlace.MinimumMet);
            Assert.Equal("Onboarding Diner", Assert.Single(withPlace.Restaurants).Name);
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/onboarding/steps/restaurants/confirm", null)).StatusCode);
            var done = (await (await admin.PostAsync("/api/onboarding/finish", null)).Content.ReadFromJsonAsync<OnboardingDto>())!;
            Assert.Equal("done", done.Status);
            Assert.NotNull(done.CompletedAt);
            Assert.False((await Now()).Tour);
            Assert.Equal([true, true, false, false, false], done.Steps.Select(s => s.Confirmed));
            Assert.DoesNotContain($"{name} has not finished setting up", await Attention());

            // The System Administrator sees where it stands, and can have the group welcomed again without undoing anything.
            var seen = (await site.GetFromJsonAsync<SystemOnboardingDto>($"/api/system/groups/{groupId}/onboarding"))!;
            Assert.Equal(("done", "Nora New"), (seen.Status, seen.CompletedBy));
            Assert.Equal("new", (await (await site.PutAsJsonAsync($"/api/system/groups/{groupId}/onboarding", new SystemOnboardingRequest(null, Restart: true))).Content.ReadFromJsonAsync<SystemOnboardingDto>())!.Status);
            var again = await Now();
            Assert.Equal(("new", true), (again.Status, again.MinimumMet));
            Assert.Equal("Good talking with you. Check the addresses.", again.WelcomeNote);
            Assert.Equal("done", (await (await site.PutAsJsonAsync($"/api/system/groups/{groupId}/onboarding", new SystemOnboardingRequest(null, MarkDone: true))).Content.ReadFromJsonAsync<SystemOnboardingDto>())!.Status);
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.GroupOnboardings.RemoveRange(db.GroupOnboardings.Where(o => o.GroupId == groupId));
            db.Restaurants.RemoveRange(db.Restaurants.IgnoreQueryFilters().Where(r => r.GroupId == groupId));
            db.Users.RemoveRange(db.Users.Where(u => u.GroupId == groupId));
            db.Groups.RemoveRange(db.Groups.Where(g => g.Id == groupId));
            await db.SaveChangesAsync();
        }
    }
}
