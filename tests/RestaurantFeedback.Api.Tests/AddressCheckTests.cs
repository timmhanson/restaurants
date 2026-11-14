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
    public async Task The_audit_log_records_whether_the_usps_address_or_the_typed_one_was_used()
    {
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        var rid = (await owner.GetFromJsonAsync<List<RestaurantDto>>("/api/restaurants"))!.First().Id;
        async Task<int> Save(CommentRequest card) =>
            (await (await owner.PostAsJsonAsync("/api/comments", card)).Content.ReadFromJsonAsync<CommentSummary>())!.Id;
        async Task<List<AuditDto>> Log(int id) =>
            (await owner.GetFromJsonAsync<PagedResult<AuditDto>>("/api/audit?pageSize=200"))!.Items.Where(a => a.EntityId == id.ToString() && a.Action.StartsWith("Address")).ToList();

        // Took the USPS version: the card is saved with it, and the log keeps what was first typed.
        var took = Card(rid, "Took", "44 OAK AVE");
        took.AddressChoice = "UsedUsps";
        took.AddressAsTyped = "44 Oak Avenue, Joliet, IL 60431";
        var tookRow = Assert.Single(await Log(await Save(took)));
        Assert.Equal("AddressUspsVersionUsed", tookRow.Action);
        Assert.Contains("44 Oak Avenue", tookRow.Details);

        // Kept their own wording when USPS writes it differently.
        var kept = Card(rid, "Kept", "45 Oak Avenue");
        kept.AddressChoice = "KeptOwn";
        var keptRow = Assert.Single(await Log(await Save(kept)));
        Assert.Equal("AddressKeptAsTyped", keptRow.Action);
        Assert.Contains("Kept the address as typed", keptRow.Details);
        Assert.Contains("45 OAK AVE", keptRow.Details); // the USPS version that was passed over

        // Saved without choosing while USPS had a different version: still recorded.
        var ignored = Assert.Single(await Log(await Save(Card(rid, "Ignored", "46 Oak Avenue"))));
        Assert.Contains("without choosing", ignored.Details);

        // Nothing to choose between when USPS agrees with what was typed.
        Assert.Empty(await Log(await Save(Card(rid, "Agreed", "47 Oak St"))));
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

        // USPS's own reason is pulled out of whichever error shape it sends.
        Assert.Equal("Insufficient scope", UspsAddressVerifier.ErrorMessage("""{"apiVersion":"v3","error":{"code":"403","message":"Insufficient scope","errors":[]}}"""));
        Assert.Equal("Invalid API call as no apiproduct match found", UspsAddressVerifier.ErrorMessage("""{"fault":{"faultstring":"Invalid API call as no apiproduct match found","detail":{}}}"""));
        Assert.Equal("The client is not approved", UspsAddressVerifier.ErrorMessage("""{"error":"invalid_client","error_description":"The client is not approved"}"""));
        Assert.Equal("invalid_client", UspsAddressVerifier.ErrorMessage("""{"error":"invalid_client"}"""));
        Assert.Null(UspsAddressVerifier.ErrorMessage("<html>Forbidden</html>"));

        Assert.Equal("IL", UsStates.Code("Illinois"));
        Assert.Equal("IL", UsStates.Code("il"));
        Assert.Equal("WI", UsStates.Code("Wisc"));
        Assert.Null(UsStates.Code("New")); // ambiguous
        Assert.Null(UsStates.Code("Ontario"));
    }
}
