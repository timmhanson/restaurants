using System.Net;
using System.Net.Http.Json;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Tests;

[Collection(ApiCollection.Name)]
public class ApiTests(ApiFactory factory)
{
    private static CommentRequest Card(int restaurantId, string first = "Jane", string last = "Doe", string? phone = "815-555-1234",
        string visit = "2026-11-03", string reason = "Order mistake", bool force = false) => new()
    {
        RestaurantId = restaurantId, Source = "SMG survey", GuestFirstName = first, GuestLastName = last, Phone = phone, StreetAddress = "123 Main St",
        City = "Joliet", State = "IL", Zip = "60431", VisitDate = DateOnly.Parse(visit), DateIssued = DateOnly.Parse(visit),
        Reason = reason, ReasonDetails = "Missing fries", CouponOffer = "Free Value Basket", AuthorizedBy = "Mgr", Force = force
    };

    [Fact]
    public async Task Login_rejects_bad_password()
    {
        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(ApiFactory.OwnerUser, "wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Owner_can_enter_card_and_duplicate_is_detected()
    {
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        var restaurants = await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants");
        var rid = restaurants!.Single().Id;

        var first = await owner.PostAsJsonAsync("/api/comments", Card(rid, phone: "815-555-0001"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var created = await first.Content.ReadFromJsonAsync<CommentSummary>();
        Assert.Equal(DateOnly.Parse("2027-02-03"), created!.ExpiresOn); // 3 months after issue
        Assert.Equal("Tuesday", created.DayOfWeek);

        // Same guest, same visit, same reason -> exact duplicate -> 409 unless forced.
        var dup = await owner.PostAsJsonAsync("/api/comments", Card(rid, phone: "(815) 555-0001"));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        var check = await dup.Content.ReadFromJsonAsync<DuplicateCheckResult>();
        Assert.True(check!.ExactDuplicate);
        Assert.Equal(1, check.PriorCount);

        // Same guest a few days later for a different reason -> likely duplicate + repeat guest.
        var later = await owner.PostAsJsonAsync("/api/comments/check", Card(rid, phone: "815-555-0001", visit: "2026-11-06", reason: "Wait time"));
        var laterCheck = await later.Content.ReadFromJsonAsync<DuplicateCheckResult>();
        Assert.False(laterCheck!.ExactDuplicate);
        Assert.True(laterCheck.LikelyDuplicate);
        Assert.True(laterCheck.RepeatGuest);

        var forced = await owner.PostAsJsonAsync("/api/comments", Card(rid, phone: "815-555-0001", visit: "2026-11-06", reason: "Wait time", force: true));
        Assert.Equal(HttpStatusCode.Created, forced.StatusCode);
        var second = await forced.Content.ReadFromJsonAsync<CommentSummary>();
        Assert.Equal(2, second!.GuestCommentCount);

        var list = await owner.GetFromJsonAsync<PagedResult<CommentSummary>>($"/api/comments?restaurantId={rid}&repeatOnly=true");
        Assert.Equal(2, list!.Total);

        var analytics = await owner.GetFromJsonAsync<AnalyticsDto>($"/api/analytics?restaurantId={rid}");
        Assert.Equal(1, analytics!.RepeatGuests);
        Assert.Contains(analytics.ByDayOfWeek, d => d.Label == "Tuesday" && d.Count >= 1); // other tests may add Tuesday visits

        var csv = await owner.GetAsync($"/api/comments/export?restaurantId={rid}");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Jane", await csv.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Password_change_requires_an_email_on_file_and_a_valid_code()
    {
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        var rid = (await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants"))!.Single().Id;
        var create = await owner.PostAsJsonAsync("/api/users", new UserRequest
        {
            Username = "team-noemail", DisplayName = "No Email Team", Password = "TeamPass!123", Role = "TeamMember", RestaurantIds = [rid]
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var team = await factory.LoginAsync("team-noemail", "TeamPass!123");
        var req = await team.PostAsync("/api/auth/change-password/request", null);
        Assert.Equal(HttpStatusCode.BadRequest, req.StatusCode);

        // A user with an email on file: resend cooldown applies, and the code is single-use.
        var mk = await owner.PostAsJsonAsync("/api/users", new UserRequest
        {
            Username = "pwtest", DisplayName = "Password Tester", Email = "pwtest@test.local", Password = "PwTest!123", Role = "Manager", RestaurantIds = [rid]
        });
        Assert.Equal(HttpStatusCode.Created, mk.StatusCode);
        var user = await factory.LoginAsync("pwtest", "PwTest!123");
        var first = await user.PostAsync("/api/auth/change-password/request", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var sent = await first.Content.ReadFromJsonAsync<CodeSentResponse>();
        Assert.Equal("p***t@test.local", sent!.EmailMasked);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsync("/api/auth/change-password/request", null)).StatusCode);
        var code = factory.Emails.LastCode!;
        var ok = await user.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest("PwTest!123", "PwTest!456", code));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var fresh = await ok.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.False(fresh!.MustChangePassword);
        var reuse = await user.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest("PwTest!456", "PwTest!789", code));
        Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("pwtest", "PwTest!456"))).StatusCode);
    }

    [Fact]
    public async Task Team_member_can_only_enter_cards()
    {
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        var rid = (await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants"))!.Single().Id;
        var create = await owner.PostAsJsonAsync("/api/users", new UserRequest
        {
            Username = "team-joliet", DisplayName = "Joliet Team", Password = "TeamPass!123", Role = "TeamMember", RestaurantIds = [rid]
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var team = await factory.LoginAsync("team-joliet", "TeamPass!123");
        var post = await team.PostAsJsonAsync("/api/comments", Card(rid, first: "Team", last: "Entry", phone: "815-555-0002"));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await team.GetAsync($"/api/comments?restaurantId={rid}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await team.GetAsync("/api/analytics")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await team.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await team.GetAsync("/api/audit")).StatusCode);
    }

    [Fact]
    public async Task Manager_permissions_are_enforced_and_password_reset_forces_change()
    {
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        var rid = (await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants"))!.Single().Id;
        var create = await owner.PostAsJsonAsync("/api/users", new UserRequest
        {
            Username = "mgr1", DisplayName = "Manager One", Email = "mgr1@test.local", Password = "MgrPass!123", Role = "Manager",
            Permissions = ["ViewComments"], RestaurantIds = [rid]
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var mgrDto = await create.Content.ReadFromJsonAsync<UserDto>();
        Assert.True(mgrDto!.MustChangePassword);

        // New manager must change password before doing anything else.
        var mgrClient = factory.CreateClient();
        var login = await (await mgrClient.PostAsJsonAsync("/api/auth/login", new LoginRequest("mgr1", "MgrPass!123"))).Content.ReadFromJsonAsync<LoginResponse>();
        Assert.True(login!.MustChangePassword);
        mgrClient.DefaultRequestHeaders.Authorization = new("Bearer", login.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await mgrClient.GetAsync($"/api/comments?restaurantId={rid}")).StatusCode);
        // A wrong code is rejected and counted; the emailed code works.
        var wrong = await mgrClient.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest("MgrPass!123", "MgrPass!456", "000000"));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var changed = await factory.ChangePasswordAsync(mgrClient, "MgrPass!123", "MgrPass!456");
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal("mgr1@test.local", factory.Emails.Sent[^1].to);

        var mgr = await factory.LoginAsync("mgr1", "MgrPass!456");
        Assert.Equal(HttpStatusCode.OK, (await mgr.GetAsync($"/api/comments?restaurantId={rid}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mgr.GetAsync("/api/analytics")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mgr.GetAsync("/api/comments/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mgr.PostAsJsonAsync("/api/users", new UserRequest { Username = "newmgr", DisplayName = "New Manager", Password = "Password!1", Role = "Manager" })).StatusCode);

        // Owner grants analytics; new token picks it up.
        var upd = await owner.PutAsJsonAsync($"/api/users/{mgrDto.Id}", new UserRequest
        {
            Username = "mgr1", DisplayName = "Manager One", Role = "Manager", Permissions = ["ViewComments", "ViewAnalytics"], RestaurantIds = [rid]
        });
        Assert.Equal(HttpStatusCode.OK, upd.StatusCode);
        mgr = await factory.LoginAsync("mgr1", "MgrPass!456");
        Assert.Equal(HttpStatusCode.OK, (await mgr.GetAsync("/api/analytics")).StatusCode);

        // Audit trail recorded the grant.
        var audit = await owner.GetFromJsonAsync<PagedResult<AuditDto>>("/api/audit?action=UserUpdated");
        Assert.Contains(audit!.Items, a => a.EntityId == mgrDto.Id.ToString());
    }

    [Fact]
    public async Task Super_admin_manages_groups_and_settings_live_per_group()
    {
        var admin = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword);
        var groups = await admin.GetFromJsonAsync<List<GroupDto>>("/api/groups");
        Assert.Contains(groups!, g => g.Name == "Test Group");

        var created = await admin.PostAsJsonAsync("/api/groups", new GroupRequest
        {
            Name = "Second Group",
            Admins = [new UserRequest { Username = "admin2", DisplayName = "Admin Two", Password = "Admin2Pass!", Role = "GroupAdmin" }],
            FirstOwner = new UserRequest { Username = "owner2", DisplayName = "Owner Two", Password = "Owner2Pass!", Role = "Owner" }
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var g2 = await created.Content.ReadFromJsonAsync<GroupDto>();

        // Scoped to group 2, the super admin can add a restaurant and change settings for that group only.
        var scoped = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword, g2!.Id);
        var r = await scoped.PostAsJsonAsync("/api/restaurants", new RestaurantRequest { Name = "Culver's of Plainfield" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var settings = await scoped.GetFromJsonAsync<GroupSettings>("/api/settings");
        settings!.CouponValidityMonths = 6;
        settings.ReasonCategories = ["Cold food", "Other"];
        Assert.Equal(HttpStatusCode.OK, (await scoped.PutAsJsonAsync("/api/settings", settings)).StatusCode);

        var owner1 = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        var s1 = await owner1.GetFromJsonAsync<GroupSettings>("/api/settings");
        Assert.Equal(3, s1!.CouponValidityMonths);
        Assert.Contains("Order mistake", s1.ReasonCategories);

        // Owner of group 1 cannot see group 2's users or touch groups.
        var users1 = await owner1.GetFromJsonAsync<List<UserDto>>("/api/users");
        Assert.DoesNotContain(users1!, u => u.Username == "owner2");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner1.GetAsync("/api/groups")).StatusCode);
    }
}
