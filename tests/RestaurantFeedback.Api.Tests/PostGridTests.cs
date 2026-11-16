using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Models;
using RestaurantFeedback.Api.Services;

namespace RestaurantFeedback.Api.Tests;

/// <summary>
/// PostGrid: a group's own account prints and mails cards (confirmed, capped, never twice, cancellable), the group's
/// card-front picture, and reading PostGrid's address verification answers.
/// </summary>
[Collection(ApiCollection.Name)]
public class PostGridTests(ApiFactory factory)
{
    private const string TestKey = "test_sk_0123456789abcdefPGKEYwxyz";
    private static int _n;

    private static CommentRequest Card(int rid, string last) => new()
    {
        RestaurantId = rid, Source = "SMG survey", GuestFirstName = "Grid", GuestLastName = last, Phone = $"815-555-{8000 + Interlocked.Increment(ref _n)}",
        StreetAddress = "9 Mailer Rd", City = "Joliet", State = "IL", Zip = "60431",
        VisitDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1), Reason = "Order mistake", CouponOffer = "Free <Concrete> Mixer", AuthorizedBy = "M. Smith", Force = true
    };

    /// <summary>The owner (who may spend), a System Administrator viewing the owner's group, and the restaurant id.</summary>
    private async Task<(HttpClient Owner, HttpClient Admin, int Rid, int GroupId)> SetUpAsync()
    {
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        var r = (await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants"))!.First();
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/restaurants/{r.Id}", new RestaurantRequest
        {
            Name = r.Name, StreetAddress = "1 Test St", City = "Joliet", State = "IL", Zip = "60431", Phone = "815-555-0000"
        })).StatusCode);
        var groupId = (await owner.GetFromJsonAsync<UserDto>("/api/auth/me"))!.GroupId!.Value;
        var admin = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword, groupId);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/settings/postgrid-account", new PostageAccountRequest(TestKey))).StatusCode);
        return (owner, admin, r.Id, groupId);
    }

    private static async Task<CommentSummary> Create(HttpClient client, CommentRequest card)
    {
        var res = await client.PostAsJsonAsync("/api/comments", card);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<CommentSummary>())!;
    }

    private static async Task<List<MailOutcome>> Mail(HttpClient client, int[] ids, int? confirm = null)
    {
        var res = await client.PostAsJsonAsync("/api/comments/mail", new MailRequest(ids, confirm ?? ids.Length));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<List<MailOutcome>>())!;
    }

    [Fact]
    public async Task The_group_PostGrid_key_is_checked_stored_encrypted_and_never_shown()
    {
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        var groupId = (await owner.GetFromJsonAsync<UserDto>("/api/auth/me"))!.GroupId!.Value;
        var admin = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword, groupId);

        // Only the group's administrators manage it, and PostGrid must accept the key before it is saved.
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync("/api/settings/postgrid-account", new PostageAccountRequest(TestKey))).StatusCode);
        var refused = await admin.PutAsJsonAsync("/api/settings/postgrid-account", new PostageAccountRequest("test_sk_bad_key_0000000000"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("did not accept", await refused.Content.ReadAsStringAsync());

        var saved = await admin.PutAsJsonAsync("/api/settings/postgrid-account", new PostageAccountRequest(TestKey));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.DoesNotContain(TestKey, await saved.Content.ReadAsStringAsync());
        var account = (await admin.GetFromJsonAsync<PostageAccountDto>("/api/settings/postgrid-account"))!;
        Assert.True(account.HasOwnAccount && account.LooksLikeTestKey);
        Assert.Equal("wxyz", account.KeyEnding);
        Assert.True((await admin.PostAsync("/api/settings/postgrid-account/test", null)).IsSuccessStatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RestaurantFeedback.Api.Data.AppDbContext>();
        var stored = db.Groups.Single(g => g.Id == groupId).PostGridKeyEncrypted;
        Assert.False(string.IsNullOrEmpty(stored));
        Assert.DoesNotContain(TestKey, stored);
        Assert.DoesNotContain(db.AuditLogs.ToList(), a => (a.Details ?? "").Contains(TestKey));

        var config = (await owner.GetFromJsonAsync<PrintConfig>("/api/comments/print-config"))!;
        Assert.True(config.MailAvailable && config.CanMail && config.MailTest);
    }

    [Fact]
    public async Task Cards_are_mailed_once_marked_sent_and_can_be_cancelled_before_printing()
    {
        var (owner, _, rid, groupId) = await SetUpAsync();
        var card = await Create(owner, Card(rid, "Mailed"));
        var refused = await Create(owner, Card(rid, "Refused"));
        var before = factory.Mail.Created;

        // The confirmed count must match what is sent.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/comments/mail", new MailRequest([card.Id, refused.Id], 1))).StatusCode);
        Assert.Equal(before, factory.Mail.Created);

        var outcomes = await Mail(owner, [card.Id, refused.Id]);
        var ok = outcomes.Single(o => o.CommentId == card.Id);
        Assert.True(ok.Ok && ok.Test && !ok.AlreadyMailed);
        Assert.Equal(MailStatuses.Ready, ok.Status);
        Assert.Contains("refused", outcomes.Single(o => o.CommentId == refused.Id).Error);
        Assert.Equal(before + 1, factory.Mail.Created);

        // What PostGrid was sent: the group's key, the guest and the restaurant, and the coupon with the text escaped.
        var sent = factory.Mail.Requests.Last(r => r.Request.To.LastName == "Mailed");
        Assert.Equal(TestKey, sent.Key);
        Assert.Equal("9 Mailer Rd", sent.Request.To.AddressLine1);
        Assert.Equal("1 Test St", sent.Request.From.AddressLine1);
        Assert.Contains("Free &lt;Concrete&gt; Mixer", sent.Request.BackHtml);
        Assert.Contains($"card{card.Id}-0", sent.IdempotencyKey);

        // The card is marked as sent, with a follow-up due, and shows PostGrid's status.
        var after = (await owner.GetFromJsonAsync<PagedResult<CommentSummary>>($"/api/comments?ids={card.Id}"))!.Items.Single();
        Assert.True(after.Responded);
        Assert.Equal("PostGrid", after.MailProvider);
        Assert.Equal(MailStatuses.Ready, after.MailStatus);
        Assert.NotNull(after.FollowUpDueOn);

        // Never mailed twice.
        var again = (await Mail(owner, [card.Id])).Single();
        Assert.True(again.Ok && again.AlreadyMailed);
        Assert.Equal(before + 1, factory.Mail.Created);

        // A refused card can be tried again; it gets a new idempotency key so PostGrid does not just repeat its refusal.
        await Mail(owner, [refused.Id]);
        var keys = factory.Mail.Requests.Where(r => r.Request.To.LastName == "Refused").Select(r => r.IdempotencyKey).ToList();
        Assert.Equal(2, keys.Distinct().Count());

        // Cancelled before printing: back to "not sent", and mailing it again creates a new card at PostGrid.
        var cancel = await owner.PostAsync($"/api/comments/{card.Id}/mail/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(MailStatuses.Cancelled, (await cancel.Content.ReadFromJsonAsync<MailStatusDto>())!.Status);
        var reopened = (await owner.GetFromJsonAsync<PagedResult<CommentSummary>>($"/api/comments?ids={card.Id}"))!.Items.Single();
        Assert.False(reopened.Responded);
        Assert.Contains(card.Id, (await owner.GetFromJsonAsync<PagedResult<CommentSummary>>("/api/comments?status=waiting&pageSize=500"))!.Items.Select(c => c.Id));
        var remailed = (await Mail(owner, [card.Id])).Single();
        Assert.True(remailed.Ok && !remailed.AlreadyMailed);
        Assert.Equal(before + 2, factory.Mail.Created);

        // Once PostGrid is printing it can no longer be cancelled.
        var mailId = (await owner.GetFromJsonAsync<PagedResult<CommentSummary>>($"/api/comments?ids={card.Id}"))!.Items.Single();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RestaurantFeedback.Api.Data.AppDbContext>();
            factory.Mail.SetStatus(db.Comments.Single(c => c.Id == card.Id).MailId!, "printing");
        }
        var late = await owner.PostAsync($"/api/comments/{card.Id}/mail/cancel", null);
        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
        var status = (await (await owner.PostAsJsonAsync("/api/comments/mail/status", new CardIdsRequest([card.Id]))).Content.ReadFromJsonAsync<List<MailStatusDto>>())!.Single();
        Assert.Equal("printing", status.Status);
        Assert.StartsWith("https://proofs.test/", status.PreviewUrl);

        var history = (await owner.GetFromJsonAsync<List<ActivityDto>>($"/api/comments/{card.Id}/activity"))!;
        Assert.Equal(2, history.Count(a => a.Kind == "Mailed"));
        Assert.Single(history, a => a.Kind == "MailCancelled");
        Assert.True(mailId.Responded);
        _ = groupId;
    }

    [Fact]
    public async Task Only_people_allowed_to_spend_can_mail_and_a_mailing_is_capped()
    {
        var (owner, _, rid, _) = await SetUpAsync();
        var card = await Create(owner, Card(rid, "Capped"));
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync("/api/users", new UserRequest
        {
            Username = "grid-mgr", DisplayName = "Grid Manager", Password = "MgrPass!1", Role = "Manager", RestaurantIds = [rid], Permissions = ["ViewComments", "MarkResponded"]
        })).StatusCode);
        var manager = await factory.LoginAsync("grid-mgr", "MgrPass!1");
        Assert.False((await manager.GetFromJsonAsync<PrintConfig>("/api/comments/print-config"))!.CanMail);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync("/api/comments/mail", new MailRequest([card.Id], 1))).StatusCode);

        var tooMany = Enumerable.Range(1, 101).Select(i => card.Id + i * 100000).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/comments/mail", new MailRequest(tooMany, tooMany.Length))).StatusCode);
    }

    [Fact]
    public async Task The_card_front_picture_is_uploaded_served_publicly_by_its_hash_and_used_on_the_card()
    {
        var (owner, admin, rid, groupId) = await SetUpAsync();
        // A PNG header saying 1875 x 1275 pixels.
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0x07, 0x53, 0, 0, 0x04, 0xFB, 8, 2, 0, 0, 0, 1, 2, 3, 4];
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "front.png");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsync("/api/settings/card-front", form)).StatusCode);

        using var form2 = new MultipartFormDataContent();
        var file2 = new ByteArrayContent(png);
        file2.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form2.Add(file2, "file", "front.png");
        var res = await admin.PutAsync("/api/settings/card-front", form2);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var art = (await res.Content.ReadFromJsonAsync<CardArtDto>())!;
        Assert.True(art.HasArt);
        Assert.Equal(1875, art.WidthPx);
        Assert.Equal(1275, art.HeightPx);

        // Public, so PostGrid can download it, but only at its exact address.
        var anonymous = factory.CreateClient();
        var served = await anonymous.GetAsync(art.Url);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(png, await served.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/card-art/{groupId}/0000000000000000.png")).StatusCode);

        // The card's front uses it.
        var card = await Create(owner, Card(rid, "Artful"));
        var preview = (await owner.GetFromJsonAsync<MailPreview>($"/api/comments/{card.Id}/mail/preview"))!;
        Assert.Contains(art.Url!, preview.FrontHtml);
        Assert.Contains("Good until", preview.BackHtml);

        // Not a picture: refused.
        using var form3 = new MultipartFormDataContent();
        form3.Add(new ByteArrayContent("not an image"u8.ToArray()), "file", "notes.txt");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/settings/card-front", form3)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync("/api/settings/card-front")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(art.Url)).StatusCode);
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
}
