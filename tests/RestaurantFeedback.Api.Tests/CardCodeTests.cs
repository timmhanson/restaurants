using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Models;
using RestaurantFeedback.Api.Services;

namespace RestaurantFeedback.Api.Tests;

/// <summary>
/// The QR code on every coupon card: each card has its own GUID, the printed code is that GUID with a signature only
/// this site can make, and scanning it shows the card's record only to someone signed in who works at its restaurant.
/// </summary>
[Collection(ApiCollection.Name)]
public class CardCodeTests(ApiFactory factory)
{
    private static int _n;

    private static CommentRequest Card(int rid, string last) => new()
    {
        RestaurantId = rid, Source = "SMG survey", GuestFirstName = "Cody", GuestLastName = last, Phone = $"815-555-{7000 + Interlocked.Increment(ref _n)}",
        StreetAddress = "4 Scanner Ct", City = "Joliet", State = "IL", Zip = "60431",
        VisitDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1), Reason = "Order mistake", CouponOffer = "Free Sundae", AuthorizedBy = "M. Smith", Force = true
    };

    private static async Task<CommentSummary> Create(HttpClient client, CommentRequest card)
    {
        var res = await client.PostAsJsonAsync("/api/comments", card);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<CommentSummary>())!;
    }

    private static async Task<CardCheck> Check(HttpClient client, string code) =>
        (await client.GetFromJsonAsync<CardCheck>($"/api/cards/check/{Uri.EscapeDataString(code)}"))!;

    [Fact]
    public async Task Each_card_has_its_own_signed_code_that_only_signed_in_staff_of_its_restaurant_can_open()
    {
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        var rid = (await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants"))!.First().Id;
        var first = await Create(owner, Card(rid, "First"));
        var second = await Create(owner, Card(rid, "Second"));

        // Every card has its own GUID, and its QR code holds this site's check address for that card.
        Assert.NotEqual(Guid.Empty, first.CardId);
        Assert.NotEqual(first.CardId, second.CardId);
        var qrs = (await (await owner.PostAsJsonAsync("/api/comments/qr", new CardIdsRequest([first.Id, second.Id]))).Content.ReadFromJsonAsync<List<CardQr>>())!;
        Assert.Equal(2, qrs.Count);
        var qr = qrs.Single(q => q.CommentId == first.Id);
        Assert.Equal(first.CardId, qr.CardId);
        Assert.Contains("/v/", qr.Url);
        Assert.StartsWith("data:image/svg+xml;base64,", qr.Image);
        var code = qr.Url[(qr.Url.LastIndexOf("/v/", StringComparison.Ordinal) + 3)..];
        var otherCode = qrs.Single(q => q.CommentId == second.Id).Url.Split("/v/")[1];
        Assert.NotEqual(code, otherCode);
        // The code is the GUID and a signature, and says nothing else about the card.
        Assert.Equal(26, WebEncoders.Base64UrlDecode(code).Length);
        Assert.DoesNotContain("Sundae", qr.Url);

        // Nobody gets anything without signing in.
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/cards/check/{code}")).StatusCode);

        // Someone who works at the restaurant sees the card as the site has it.
        var good = await Check(owner, code);
        Assert.True(good.Genuine && good.CanRedeem);
        Assert.Equal((first.Id, first.CardId, "Cody First", "Free Sundae"), (good.Card!.CommentId, good.Card.CardId, good.Card.GuestName, good.Card.Offer));
        Assert.False(good.Card.Expired);
        Assert.Null(good.Card.RedeemedOn);

        // Altered, made up, or a signature moved from one card to another: not genuine, and nothing about any card is shown.
        // One character of the signature changed.
        var altered = code[..25] + (code[25] == 'A' ? 'B' : 'A') + code[26..];
        var raw = WebEncoders.Base64UrlDecode(code);
        var otherRaw = WebEncoders.Base64UrlDecode(otherCode);
        var swapped = WebEncoders.Base64UrlEncode([.. otherRaw[..16], .. raw[16..]]);
        var madeUp = WebEncoders.Base64UrlEncode([.. Guid.NewGuid().ToByteArray(), .. new byte[10]]);
        foreach (var bad in new[] { altered, swapped, madeUp, "not-a-code", WebEncoders.Base64UrlEncode(first.CardId.ToByteArray()) })
        {
            var refused = await Check(owner, bad);
            Assert.False(refused.Genuine);
            Assert.Null(refused.Card);
            Assert.False(refused.CanRedeem);
        }

        // A System Administrator overseeing the site learns it is genuine and whose it is, but not the guest or the offer.
        var site = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword);
        var elsewhere = await Check(site, code);
        Assert.True(elsewhere.Genuine);
        Assert.Null(elsewhere.Card);
        Assert.DoesNotContain("Cody", elsewhere.Message);
        Assert.DoesNotContain("Sundae", elsewhere.Message);

        // Once redeemed, the same card (or a photocopy of it) shows as used and cannot be redeemed again.
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/comments/{first.Id}/redeem", new RedeemRequest(null, null))).StatusCode);
        var used = await Check(owner, code);
        Assert.True(used.Genuine);
        Assert.False(used.CanRedeem);
        Assert.NotNull(used.Card!.RedeemedOn);
        Assert.Contains("already redeemed", used.Message);

        // The mailed card carries the same code, and every check is in the audit log.
        var back = (await owner.GetFromJsonAsync<MailPreview>($"/api/comments/{second.Id}/mail/preview"))!.BackHtml;
        Assert.Contains("data:image/svg+xml;base64,", back);
        Assert.Contains("Staff: scan to check", back);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var firstId = first.Id.ToString();
        Assert.True(db.AuditLogs.Count(a => a.Action == "CardCodeChecked" && a.EntityId == firstId) >= 3);
        Assert.True(db.AuditLogs.Count(a => a.Action == "CardCodeRejected") >= 5);

        // The signing secret is kept encrypted, and a code only this site's secret made is accepted.
        var codes = factory.Services.GetRequiredService<CardCodes>();
        Assert.Equal(first.CardId, codes.Read(codes.CodeFor(first.CardId)));
        Assert.Equal(code, codes.CodeFor(first.CardId));
        Assert.Null(codes.Read(null));
        Assert.Null(codes.Read(new string('A', 200)));
        var stored = (await db.SiteSettings.FindAsync("card.codekey"))!.Value;
        Assert.DoesNotContain(code[22..], stored);

        // Cards printed in the restaurant can carry the code too: it is one of the blanks on a pre-printed card.
        var settings = (await owner.GetFromJsonAsync<GroupSettings>("/api/settings"))!;
        Assert.True(settings.Postcard!.Fields["qr"].Show);
    }
}
