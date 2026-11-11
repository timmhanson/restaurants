using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RestaurantFeedback.Api.Auth;
using RestaurantFeedback.Api.Dtos;

namespace RestaurantFeedback.Api.Tests;

/// <summary>
/// Group setup by a super admin, owners choosing the restaurants they own, and staffing restaurants.
/// Runs in its own investment group so it cannot disturb the shared seed data used by ApiTests.
/// </summary>
[Collection(ApiCollection.Name)]
public class OwnershipTests(ApiFactory factory)
{
    private static UserRequest Owner(string username, string name) => new()
    {
        Username = username, DisplayName = name, Email = $"{username}@test.local", Password = "TempOwner!1", Role = "Owner"
    };

    /// <summary>New owners must change their temporary password (with the emailed code) before doing anything.</summary>
    private async Task<HttpClient> SignInNewOwnerAsync(string username)
    {
        var client = factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, "TempOwner!1"))).Content.ReadFromJsonAsync<LoginResponse>();
        Assert.True(login!.MustChangePassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        var changed = await factory.ChangePasswordAsync(client, "TempOwner!1", "RealOwner!1");
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        return await factory.LoginAsync(username, "RealOwner!1");
    }

    [Fact]
    public async Task Super_admin_sets_up_a_group_and_owners_staff_only_what_they_own()
    {
        var admin = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword);

        // One step: group, two owners, two restaurants. Both owners own both restaurants to start.
        var created = await admin.PostAsJsonAsync("/api/groups", new GroupRequest
        {
            Name = "Ownership Group",
            Owners = [Owner("own-alice", "Alice Owner"), Owner("own-bob", "Bob Owner")],
            Restaurants = [new RestaurantRequest { Name = "Culver's of Lockport" }, new RestaurantRequest { Name = "Culver's of Romeoville" }]
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var group = (await created.Content.ReadFromJsonAsync<GroupDto>())!;
        Assert.Equal(2, group.RestaurantCount);
        Assert.Equal(["Alice Owner", "Bob Owner"], group.OwnerNames);

        var scoped = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword, group.Id);
        var list = (await scoped.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants?all=true"))!;
        var lockport = list.Single(r => r.Name == "Culver's of Lockport");
        var romeoville = list.Single(r => r.Name == "Culver's of Romeoville");
        Assert.All(list, r => Assert.Equal(2, r.Owners!.Count));
        Assert.All(list, r => Assert.True(r.CanManage));

        // Super admin leaves Romeoville to Bob only.
        var staff = (await scoped.GetFromJsonAsync<StaffResponse>($"/api/restaurants/{romeoville.Id}/staff"))!;
        var bob = staff.Owners.Single(o => o.Username == "own-bob");
        var alice = staff.Owners.Single(o => o.Username == "own-alice");
        Assert.Equal(HttpStatusCode.OK, (await scoped.PutAsJsonAsync($"/api/restaurants/{romeoville.Id}/staff", new StaffRequest([bob.Id]))).StatusCode);
        // A restaurant cannot be left without an owner.
        Assert.Equal(HttpStatusCode.BadRequest, (await scoped.PutAsJsonAsync($"/api/restaurants/{romeoville.Id}/staff", new StaffRequest([]))).StatusCode);

        // Alice now owns only Lockport.
        var aliceClient = await SignInNewOwnerAsync("own-alice");
        var aliceView = (await aliceClient.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants?all=true"))!;
        Assert.True(aliceView.Single(r => r.Id == lockport.Id).OwnedByMe);
        Assert.False(aliceView.Single(r => r.Id == romeoville.Id).CanManage);

        // She can create a manager at Lockport, but not at Romeoville, and cannot edit or staff Romeoville.
        var mgr = await aliceClient.PostAsJsonAsync("/api/users", new UserRequest
        {
            Username = "own-mgr", DisplayName = "Lockport Manager", Email = "own-mgr@test.local", Password = "Manager!123", Role = "Manager",
            Permissions = ["ViewComments"], RestaurantIds = [lockport.Id]
        });
        Assert.Equal(HttpStatusCode.Created, mgr.StatusCode);
        var mgrDto = (await mgr.Content.ReadFromJsonAsync<UserDto>())!;
        var wrongPlace = await aliceClient.PostAsJsonAsync("/api/users", new UserRequest
        {
            Username = "own-mgr2", DisplayName = "Nope", Password = "Manager!123", Role = "Manager", RestaurantIds = [romeoville.Id]
        });
        Assert.Equal(HttpStatusCode.Forbidden, wrongPlace.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await aliceClient.PutAsJsonAsync($"/api/restaurants/{romeoville.Id}", new RestaurantRequest { Name = "Renamed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await aliceClient.PutAsJsonAsync($"/api/restaurants/{romeoville.Id}/staff", new StaffRequest([alice.Id, bob.Id]))).StatusCode);

        // Bob (who owns Romeoville) adds the Lockport manager there too; Alice editing the manager keeps that assignment.
        var bobClient = await SignInNewOwnerAsync("own-bob");
        var romeoStaff = (await bobClient.GetFromJsonAsync<StaffResponse>($"/api/restaurants/{romeoville.Id}/staff"))!;
        Assert.True(romeoStaff.CanManage);
        Assert.Equal(HttpStatusCode.OK, (await bobClient.PutAsJsonAsync($"/api/restaurants/{romeoville.Id}/staff", new StaffRequest([bob.Id, mgrDto.Id]))).StatusCode);
        var edit = await aliceClient.PutAsJsonAsync($"/api/users/{mgrDto.Id}", new UserRequest
        {
            Username = "own-mgr", DisplayName = "Lockport Manager", Email = "own-mgr@test.local", Role = "Manager", Permissions = ["ViewComments", "ViewAnalytics"], RestaurantIds = [lockport.Id]
        });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(new[] { lockport.Id, romeoville.Id }.OrderBy(x => x), (await edit.Content.ReadFromJsonAsync<UserDto>())!.RestaurantIds);
        // Alice cannot remove someone who also works at a restaurant she does not own.
        Assert.Equal(HttpStatusCode.Forbidden, (await aliceClient.DeleteAsync($"/api/users/{mgrDto.Id}")).StatusCode);

        // Alice claims Romeoville; now she can manage it. Bob cannot drop Lockport... he is not the last owner there, so he can.
        Assert.Equal(HttpStatusCode.NoContent, (await aliceClient.PutAsJsonAsync($"/api/restaurants/{romeoville.Id}/ownership", new OwnershipRequest(true))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await aliceClient.PutAsJsonAsync($"/api/restaurants/{romeoville.Id}", new RestaurantRequest { Name = "Culver's of Romeoville" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await bobClient.PutAsJsonAsync($"/api/restaurants/{lockport.Id}/ownership", new OwnershipRequest(false))).StatusCode);
        // Alice is now Lockport's only owner, so she cannot stop owning it.
        Assert.Equal(HttpStatusCode.BadRequest, (await aliceClient.PutAsJsonAsync($"/api/restaurants/{lockport.Id}/ownership", new OwnershipRequest(false))).StatusCode);

        // Every change is audited.
        var audit = (await scoped.GetFromJsonAsync<PagedResult<AuditDto>>("/api/audit?pageSize=200"))!;
        Assert.Contains(audit.Items, a => a.Action == "RestaurantStaffChanged");
        Assert.Contains(audit.Items, a => a.Action == "RestaurantOwnershipClaimed");
        Assert.Contains(audit.Items, a => a.Action == "RestaurantOwnershipReleased");
    }

    [Fact]
    public async Task Staffing_changes_and_deactivation_apply_without_signing_in_again()
    {
        var admin = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword);
        var group = (await (await admin.PostAsJsonAsync("/api/groups", new GroupRequest
        {
            Name = "Live Access Group",
            Owners = [Owner("live-owner", "Live Owner")],
            Restaurants = [new RestaurantRequest { Name = "Culver's of Mokena" }]
        })).Content.ReadFromJsonAsync<GroupDto>())!;
        var owner = await SignInNewOwnerAsync("live-owner");
        var rid = (await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants"))!.Single().Id;

        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/users", new UserRequest
        {
            Username = "live-team", DisplayName = "Mokena crew", Password = "Crew!12345", Role = "TeamMember", RestaurantIds = [rid]
        })).StatusCode);
        var team = await factory.LoginAsync("live-team", "Crew!12345");
        var card = new CommentRequest
        {
            RestaurantId = rid, GuestFirstName = "Pat", GuestLastName = "Live", Phone = "815-555-7000", VisitDate = DateOnly.Parse("2026-11-02"), Reason = "Wait time"
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

        // Deactivate the login: the old token is rejected.
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/users/{teamId}", new UserRequest
        {
            Username = "live-team", DisplayName = "Mokena crew", Role = "TeamMember", RestaurantIds = [rid], IsActive = false
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await team.GetAsync("/api/restaurants")).StatusCode);
        Assert.NotEqual(0, group.Id);
    }
}
