using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using RestaurantFeedback.Api.Dtos;

namespace RestaurantFeedback.Api.Tests;

/// <summary>The System Administrator's own area: overview, security, system status and the cross-group directories.</summary>
[Collection(ApiCollection.Name)]
public class SystemAdminTests(ApiFactory factory)
{
    private static readonly string[] Pages = ["overview", "security", "status", "restaurants", "people"];

    [Fact]
    public async Task Only_system_administrators_can_open_the_system_area()
    {
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        foreach (var page in Pages)
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/system/{page}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsync("/api/system/test-email", null)).StatusCode);
    }

    [Fact]
    public async Task Overview_security_and_status_describe_the_whole_site()
    {
        // Five wrong passwords for one account is worth the System Administrator's attention.
        var anonymous = factory.CreateClient();
        for (var i = 0; i < 5; i++)
            await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest("sysadmin-burst", "wrong-password"));
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        var me = (await owner.GetFromJsonAsync<UserDto>("/api/auth/me"))!;
        var rid = (await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants"))!.First().Id;
        await owner.GetAsync("/api/system/overview"); // refused, and recorded

        var admin = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword);
        SystemOverview? overview = null;
        for (var i = 0; i < 40 && (overview is null || !overview.Attention.Any(a => a.Title.Contains("sysadmin-burst"))); i++)
        {
            overview = await admin.GetFromJsonAsync<SystemOverview>("/api/system/overview");
            await Task.Delay(150);
        }
        Assert.Contains(overview!.Attention, a => a.Severity == "high" && a.Title.Contains("sysadmin-burst") && a.Link == "/security");
        Assert.True(overview.Groups >= 1 && overview.Restaurants >= 1 && overview.SystemAdmins >= 1 && overview.FailedSignIns24h >= 5);
        Assert.Contains(overview.GroupSnapshots, g => g.Id == me.GroupId && g.Restaurants >= 1);
        Assert.Contains(overview.RecentSignIns, s => !s.Succeeded && s.UserName == "sysadmin-burst");

        var security = (await admin.GetFromJsonAsync<SystemSecurity>("/api/system/security?days=1"))!;
        var burst = Assert.Single(security.Failed, f => f.UserName == "sysadmin-burst");
        Assert.True(burst.Attempts >= 5 && !burst.KnownAccount);
        Assert.Contains(security.RefusedBy, r => r.UserName == ApiFactory.OwnerUser && r.Paths.Any(p => p.Contains("/api/system/overview")));
        Assert.Contains(security.SystemAdministrators, u => u.Username == ApiFactory.SuperAdminUser);

        var status = (await admin.GetFromJsonAsync<SystemStatus>("/api/system/status"))!;
        Assert.False(string.IsNullOrEmpty(status.Version));
        Assert.StartsWith("SQLite", status.Database);
        Assert.True(status.DatabaseBytes > 0 && status.Requests24h > 0);
        Assert.Contains(status.Tables, t => t.Name == "People" && t.Rows >= 2);
        Assert.NotNull(status.LatestMigration);

        var directory = (await admin.GetFromJsonAsync<List<DirectoryRestaurant>>("/api/system/restaurants"))!;
        Assert.Contains(directory, r => r.Id == rid && r.GroupId == me.GroupId && r.Owners.Length >= 1);
        var people = (await admin.GetFromJsonAsync<List<SystemPerson>>("/api/system/people"))!;
        Assert.Contains(people, p => p.User.Id == me.Id && p.Restaurants.Length >= 1 && p.LastSeen != null);
        Assert.Contains(people, p => p.User.Role == "SuperAdmin" && p.User.GroupId == null);
    }

    [Fact]
    public async Task The_system_administrator_enters_the_usps_account_and_it_is_kept_encrypted()
    {
        const string key = "uspsConsumerKEY1234abcd", secret = "uspsConsumerSECRET5678wxyz";
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/system/usps-account")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync("/api/system/usps-account", new UspsAccountRequest(key, secret, false))).StatusCode);

        var admin = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword);
        Assert.False((await admin.GetFromJsonAsync<UspsAccountDto>("/api/system/usps-account"))!.Configured);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/system/usps-account", new UspsAccountRequest(key, "", false))).StatusCode);

        var saved = await admin.PutAsJsonAsync("/api/system/usps-account", new UspsAccountRequest(key, secret, true));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var body = await saved.Content.ReadAsStringAsync();
        Assert.DoesNotContain(key, body);
        Assert.DoesNotContain(secret, body);
        var account = (await admin.GetFromJsonAsync<UspsAccountDto>("/api/system/usps-account"))!;
        Assert.True(account.Configured && account.TestEnvironment);
        Assert.Equal("Saved in the site", account.Source);
        Assert.Equal("abcd", account.KeyEnding);

        using (var scope = factory.Services.CreateScope())
        {
            // Stored encrypted, kept out of the audit log, and picked up straight away by the address service.
            var db = scope.ServiceProvider.GetRequiredService<RestaurantFeedback.Api.Data.AppDbContext>();
            var stored = db.SiteSettings.Single(x => x.Key == "usps.account").Value;
            Assert.DoesNotContain(key, stored);
            Assert.DoesNotContain(secret, stored);
            Assert.DoesNotContain(db.AuditLogs.ToList(), a => (a.Details ?? "").Contains(secret) || (a.Details ?? "").Contains(key));
            var live = scope.ServiceProvider.GetRequiredService<RestaurantFeedback.Api.Services.UspsAccountStore>().Current!;
            Assert.Equal(secret, live.ClientSecret);
            Assert.Equal("https://apis-tem.usps.com", live.BaseUrl);
        }

        var test = (await (await admin.PostAsync("/api/system/usps-account/test", null)).Content.ReadFromJsonAsync<ServiceTestResult>())!;
        Assert.True(test.Ok);

        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync("/api/system/usps-account")).StatusCode);
        Assert.False((await admin.GetFromJsonAsync<UspsAccountDto>("/api/system/usps-account"))!.Configured);
    }
}
