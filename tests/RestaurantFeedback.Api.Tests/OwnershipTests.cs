using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Tests;

/// <summary>
/// Group setup by a system administrator, group administrators, owners of specific restaurants, and the people
/// who work across several owners' restaurants. Each test runs in its own investment group.
/// </summary>
[Collection(ApiCollection.Name)]
public class OwnershipTests(ApiFactory factory)
{
    private const string TempPassword = "TempPass!1";
    private const string RealPassword = "RealPass!1";

    private static UserRequest Person(string username, string name, string role) => new()
    {
        Username = username, DisplayName = name, Email = $"{username}@test.local", Password = TempPassword, Role = role
    };

    /// <summary>New owners and administrators must change their temporary password (with the emailed code) first.</summary>
    private async Task<HttpClient> SignInNewAsync(string username, string temp = TempPassword)
    {
        var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, temp))).Content.ReadFromJsonAsync<LoginResponse>();
        Assert.True(login!.MustChangePassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        Assert.Equal(HttpStatusCode.OK, (await factory.ChangePasswordAsync(client, temp, RealPassword)).StatusCode);
        return await factory.LoginAsync(username, RealPassword);
    }

    [Fact]
    public async Task Group_admins_run_the_group_and_owners_run_their_own_restaurants()
    {
        var system = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword);

        // A group needs at least one group administrator.
        Assert.Equal(HttpStatusCode.BadRequest, (await system.PostAsJsonAsync("/api/groups", new GroupRequest { Name = "No Admin Group" })).StatusCode);

        // Lockport is Alice's, Romeoville is Bob's, Joliet has no specific owner and is run by the group administrator.
        var created = await system.PostAsJsonAsync("/api/groups", new GroupRequest
        {
            Name = "Ownership Group",
            Admins = [Person("gina-admin", "Gina Admin", "GroupAdmin")],
            Owners = [Person("own-alice", "Alice Owner", "Owner"), Person("own-bob", "Bob Owner", "Owner")],
            Restaurants =
            [
                new RestaurantRequest { Name = "Culver's of Lockport", OwnerUsernames = ["own-alice"] },
                new RestaurantRequest { Name = "Culver's of Romeoville", OwnerUsernames = ["own-bob"] },
                new RestaurantRequest { Name = "Culver's of Joliet West", OwnerUsernames = [] }
            ]
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var group = (await created.Content.ReadFromJsonAsync<GroupDto>())!;
        Assert.Equal(["Gina Admin"], group.AdminNames);
        Assert.Equal(["Alice Owner", "Bob Owner"], group.OwnerNames);

        var gina = await SignInNewAsync("gina-admin");
        var all = (await gina.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants?all=true"))!;
        var lockport = all.Single(r => r.Name == "Culver's of Lockport");
        var romeoville = all.Single(r => r.Name == "Culver's of Romeoville");
        var joliet = all.Single(r => r.Name == "Culver's of Joliet West");
        Assert.All(all, r => Assert.True(r.CanManage));
        Assert.Equal("Alice Owner", Assert.Single(lockport.Owners!).DisplayName);
        Assert.Empty(joliet.Owners!);
        // With a group administrator, a restaurant can be saved with no specific owner.
        Assert.Equal(HttpStatusCode.OK, (await gina.PutAsJsonAsync($"/api/restaurants/{joliet.Id}/staff", new StaffRequest([]))).StatusCode);

        // Alice manages Lockport only, and cannot claim Joliet: the administrators decide ownership.
        var alice = await SignInNewAsync("own-alice");
        var aliceView = (await alice.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants?all=true"))!;
        Assert.True(aliceView.Single(r => r.Id == lockport.Id).CanManage);
        Assert.False(aliceView.Single(r => r.Id == romeoville.Id).CanManage);
        Assert.False(aliceView.Single(r => r.Id == joliet.Id).CanClaim);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PutAsJsonAsync($"/api/restaurants/{joliet.Id}/ownership", new OwnershipRequest(true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PutAsJsonAsync($"/api/restaurants/{romeoville.Id}", new RestaurantRequest { Name = "Renamed" })).StatusCode);

        // Alice hires a manager at Lockport, but cannot place anyone at Romeoville or create a group administrator.
        var mgrRes = await alice.PostAsJsonAsync("/api/users", new UserRequest
        {
            Username = "shared-mgr", DisplayName = "Sam Shared", Email = "sam@test.local", Password = "Manager!123", Role = "Manager",
            Permissions = ["ViewComments"], RestaurantIds = [lockport.Id]
        });
        Assert.Equal(HttpStatusCode.Created, mgrRes.StatusCode);
        var sam = (await mgrRes.Content.ReadFromJsonAsync<UserDto>())!;
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsJsonAsync("/api/users", new UserRequest
        {
            Username = "nope-mgr", DisplayName = "Nope", Password = "Manager!123", Role = "Manager", RestaurantIds = [romeoville.Id]
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsJsonAsync("/api/users", Person("nope-admin", "Nope Admin", "GroupAdmin"))).StatusCode);

        // Bob also puts Sam to work at Romeoville, so Sam now crosses two owners.
        var bob = await SignInNewAsync("own-bob");
        var bobStaff = (await bob.GetFromJsonAsync<StaffResponse>($"/api/restaurants/{romeoville.Id}/staff"))!;
        var bobId = bobStaff.Owners.Single(o => o.Username == "own-bob").Id;
        Assert.Equal("Gina Admin", Assert.Single(bobStaff.GroupAdmins).DisplayName);
        Assert.Equal(HttpStatusCode.OK, (await bob.PutAsJsonAsync($"/api/restaurants/{romeoville.Id}/staff", new StaffRequest([bobId, sam.Id]))).StatusCode);

        // Alice edits Sam's permissions; his Romeoville job is kept.
        var edit = await alice.PutAsJsonAsync($"/api/users/{sam.Id}", new UserRequest
        {
            Username = "shared-mgr", DisplayName = "Sam Shared", Email = "sam@test.local", Role = "Manager", Permissions = ["ViewComments", "ViewAnalytics"], RestaurantIds = [lockport.Id]
        });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(new[] { lockport.Id, romeoville.Id }.OrderBy(x => x), (await edit.Content.ReadFromJsonAsync<UserDto>())!.RestaurantIds);
        // ...but she cannot promote him; only administrators change roles.
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PutAsJsonAsync($"/api/users/{sam.Id}", new UserRequest
        {
            Username = "shared-mgr", DisplayName = "Sam Shared", Role = "Owner", RestaurantIds = [lockport.Id]
        })).StatusCode);

        // Alice can reset Sam's password and suspend him everywhere, even though he also works for Bob.
        Assert.Equal(HttpStatusCode.NoContent, (await alice.PutAsJsonAsync($"/api/users/{sam.Id}/password", new SetPasswordRequest("Manager!456"))).StatusCode);
        var samClient = factory.CreateClient();
        var samLogin = (await (await samClient.PostAsJsonAsync("/api/auth/login", new LoginRequest("shared-mgr", "Manager!456"))).Content.ReadFromJsonAsync<LoginResponse>())!;
        samClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", samLogin.Token);
        Assert.Equal(HttpStatusCode.OK, (await alice.PutAsJsonAsync($"/api/users/{sam.Id}/status", new UserStatusRequest(false))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await samClient.GetAsync("/api/auth/me")).StatusCode);
        // Removing him outright is not hers to do, because he also works at Romeoville.
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.DeleteAsync($"/api/users/{sam.Id}")).StatusCode);
        // Bob sees the suspension in his audit log because Sam works at his restaurant.
        var bobAudit = (await bob.GetFromJsonAsync<PagedResult<AuditDto>>("/api/audit?pageSize=200"))!;
        Assert.Contains(bobAudit.Items, a => a.Action == "UserSuspended" && a.EntityId == sam.Id.ToString() && a.UserName == "own-alice");
        Assert.Equal(HttpStatusCode.OK, (await bob.PutAsJsonAsync($"/api/users/{sam.Id}/status", new UserStatusRequest(true))).StatusCode);

        // Owners cannot change other owners' accounts or group-wide settings.
        var aliceUsers = (await alice.GetFromJsonAsync<List<UserDto>>("/api/users"))!;
        Assert.DoesNotContain(aliceUsers, u => u.Username == "own-bob");
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PutAsJsonAsync($"/api/users/{bobId}/password", new SetPasswordRequest("Hijack!123"))).StatusCode);
        var settings = (await alice.GetFromJsonAsync<GroupSettings>("/api/settings"))!;
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PutAsJsonAsync("/api/settings", settings)).StatusCode);

        // Gina administers every account: she resets Bob's password, changes the settings and promotes Sam.
        var ginaUsers = (await gina.GetFromJsonAsync<List<UserDto>>("/api/users"))!;
        Assert.Contains(ginaUsers, u => u.Username == "own-bob" && u.RoleTitle == "Restaurant Owner");
        Assert.Equal(HttpStatusCode.NoContent, (await gina.PutAsJsonAsync($"/api/users/{bobId}/password", new SetPasswordRequest("BobTemp!123"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await gina.PutAsJsonAsync("/api/settings", settings)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await gina.PutAsJsonAsync($"/api/users/{sam.Id}", new UserRequest
        {
            Username = "shared-mgr", DisplayName = "Sam Shared", Role = "Owner", RestaurantIds = [lockport.Id, romeoville.Id]
        })).StatusCode);

        // Alice's audit log does not show changes made only at restaurants she does not own.
        Assert.Equal(HttpStatusCode.OK, (await gina.PutAsJsonAsync($"/api/restaurants/{joliet.Id}", new RestaurantRequest { Name = "Culver's of Joliet West" })).StatusCode);
        var aliceAudit = (await alice.GetFromJsonAsync<PagedResult<AuditDto>>("/api/audit?pageSize=200"))!;
        Assert.DoesNotContain(aliceAudit.Items, a => a.RestaurantId == joliet.Id);

        // The group always keeps a group administrator.
        var ginaId = ginaUsers.Single(u => u.Username == "gina-admin").Id;
        var scoped = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword, group.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await scoped.PutAsJsonAsync($"/api/users/{ginaId}/status", new UserStatusRequest(false))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await gina.PostAsJsonAsync("/api/users", Person("gina-two", "Second Admin", "GroupAdmin"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await scoped.PutAsJsonAsync($"/api/users/{ginaId}/status", new UserStatusRequest(false))).StatusCode);
    }

    [Fact]
    public async Task Staffing_changes_and_deactivation_apply_without_signing_in_again()
    {
        var system = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword);
        var group = (await (await system.PostAsJsonAsync("/api/groups", new GroupRequest
        {
            Name = "Live Access Group",
            Admins = [Person("live-admin", "Live Admin", "GroupAdmin")],
            Owners = [Person("live-owner", "Live Owner", "Owner")],
            Restaurants = [new RestaurantRequest { Name = "Culver's of Mokena" }]
        })).Content.ReadFromJsonAsync<GroupDto>())!;
        var owner = await SignInNewAsync("live-owner");
        var rid = (await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants"))!.Single().Id;

        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/users", new UserRequest
        {
            Username = "live-team", DisplayName = "Mokena crew", Password = "Crew!12345", Role = "TeamMember", RestaurantIds = [rid]
        })).StatusCode);
        var team = await factory.LoginAsync("live-team", "Crew!12345");
        var card = new CommentRequest
        {
            RestaurantId = rid, Source = "Culvers.com Contact Us", GuestFirstName = "Pat", GuestLastName = "Live", Phone = "815-555-7000", VisitDate = DateOnly.Parse("2026-11-02"), Reason = "Wait time"
        };

        // Take the crew login off the restaurant: its existing token loses access immediately.
        var staff = (await owner.GetFromJsonAsync<StaffResponse>($"/api/restaurants/{rid}/staff"))!;
        var me = staff.Owners.Single().Id;
        var teamId = staff.TeamLogins.Single().Id;
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/restaurants/{rid}/staff", new StaffRequest([me]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await team.PostAsJsonAsync("/api/comments", card)).StatusCode);

        // Put it back: works again with the same token.
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/restaurants/{rid}/staff", new StaffRequest([me, teamId]))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await team.PostAsJsonAsync("/api/comments", card)).StatusCode);

        // Suspend the login: the old token is rejected.
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/users/{teamId}/status", new UserStatusRequest(false))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await team.GetAsync("/api/restaurants")).StatusCode);
        Assert.NotEqual(0, group.Id);
    }
}
