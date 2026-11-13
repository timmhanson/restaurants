using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Services;

namespace RestaurantFeedback.Api.Tests;

/// <summary>Checking guests' mailing addresses with USPS as cards are entered. It advises; it never blocks a card.</summary>
[Collection(ApiCollection.Name)]
public class AddressCheckTests(ApiFactory factory)
{
    private static int _n;
    private static CommentRequest Card(int rid, string last, string street) => new()
    {
        RestaurantId = rid, Source = "SMG survey", GuestFirstName = "Addr", GuestLastName = last, Phone = $"815-555-{8100 + Interlocked.Increment(ref _n)}",
        StreetAddress = street, City = "Joliet", State = "IL", Zip = "60431", VisitDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1), Reason = "Order mistake", Force = true
    };

    [Fact]
    public async Task The_form_gets_a_suggestion_a_warning_or_a_confirmation()
    {
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        async Task<AddressCheckResponse> Check(string street) =>
            (await (await owner.PostAsJsonAsync("/api/addresses/check", new AddressCheckRequest(street, "Joliet", "IL", "60431"))).Content.ReadFromJsonAsync<AddressCheckResponse>())!;

        var corrected = await Check("12 Oak Avenue");
        Assert.True(corrected.Available);
        Assert.Equal(AddressStatuses.Corrected, corrected.Status);
        Assert.Equal("12 OAK AVE", corrected.Suggested!.StreetAddress);
        Assert.Equal("60431-1234", corrected.Suggested.Zip);
        Assert.Equal(AddressStatuses.Verified, (await Check("12 Oak St")).Status);
        Assert.Equal(AddressStatuses.NotFound, (await Check("99 Nowhere Rd")).Status);
        Assert.Equal(AddressStatuses.NeedsUnit, (await Check("5 Tower Plaza")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/addresses/check", new AddressCheckRequest("1 A St", "Joliet", "IL", null))).StatusCode);
    }

    [Fact]
    public async Task A_card_always_saves_and_remembers_what_usps_said()
    {
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        var rid = (await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants"))!.First().Id;
        async Task<CommentSummary> Save(string last, string street)
        {
            var res = await owner.PostAsJsonAsync("/api/comments", Card(rid, last, street));
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);
            return (await res.Content.ReadFromJsonAsync<CommentSummary>())!;
        }

        Assert.Equal(AddressStatuses.Verified, (await Save("Good", "7 Elm St")).AddressStatus);
        var missing = await Save("Missing", "99 Nowhere Rd"); // not found, but the card is still kept
        Assert.Equal(AddressStatuses.NotFound, missing.AddressStatus);
        Assert.Null((await Save("Down", "3 Outage Way")).AddressStatus); // USPS unavailable: no verdict, no error

        // Fixing the address checks it again; leaving it alone does not ask USPS a second time.
        var calls = factory.Addresses.Calls;
        var same = Card(rid, "Missing", "99 Nowhere Rd");
        same.Phone = missing.Phone;
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/comments/{missing.Id}", same)).StatusCode);
        Assert.Equal(calls, factory.Addresses.Calls);
        var corrected = Card(rid, "Missing", "99 Main St");
        corrected.Phone = missing.Phone;
        var updated = await (await owner.PutAsJsonAsync($"/api/comments/{missing.Id}", corrected)).Content.ReadFromJsonAsync<CommentSummary>();
        Assert.Equal(AddressStatuses.Verified, updated!.AddressStatus);

        var admin = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword);
        var status = (await admin.GetFromJsonAsync<SystemStatus>("/api/system/status"))!;
        Assert.True(status.AddressCheckingConfigured && status.AddressesConfirmed >= 2);
    }

    [Fact]
    public void Usps_answers_are_read_into_plain_verdicts()
    {
        static AddressCheck Read(string dpv, string street, string typedStreet) => UspsAddressVerifier.Read(JsonDocument.Parse($$"""
            {"firm":null,"address":{"streetAddress":"{{street}}","secondaryAddress":"","city":"JOLIET","state":"IL","ZIPCode":"60431","ZIPPlus4":"1234"},
             "additionalInfo":{"DPVConfirmation":"{{dpv}}","vacant":"N"},"matches":[{"code":"31","text":"Single Response - exact match"}]}
            """).RootElement, new AddressInput(typedStreet, "Joliet", "IL", "60431"));

        var same = Read("Y", "123 MAIN ST", "123 Main St.");
        Assert.Equal(AddressStatuses.Verified, same.Status);
        Assert.Equal("60431-1234", same.Suggested!.Zip);
        var fixedUp = Read("Y", "123 MAIN ST", "123 Mian Street");
        Assert.Equal(AddressStatuses.Corrected, fixedUp.Status);
        Assert.Contains("123 MAIN ST, JOLIET, IL 60431-1234", fixedUp.Message);
        Assert.Equal(AddressStatuses.NeedsUnit, Read("D", "500 TOWER RD", "500 Tower Rd").Status);
        Assert.Equal(AddressStatuses.UnitNotConfirmed, Read("S", "500 TOWER RD", "500 Tower Rd Apt 99").Status);
        Assert.Equal(AddressStatuses.NotFound, Read("N", "9999 MAIN ST", "9999 Main St").Status);

        Assert.Equal("IL", UsStates.Code("Illinois"));
        Assert.Equal("IL", UsStates.Code("il"));
        Assert.Equal("WI", UsStates.Code("Wisc"));
        Assert.Null(UsStates.Code("New")); // ambiguous
        Assert.Null(UsStates.Code("Ontario"));
    }
}
