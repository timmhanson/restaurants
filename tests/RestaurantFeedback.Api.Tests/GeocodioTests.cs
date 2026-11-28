using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Services;

namespace RestaurantFeedback.Api.Tests;

/// <summary>Geocodio as the service that checks guest addresses: reading its answers, its key, and choosing between the services.</summary>
[Collection(ApiCollection.Name)]
public class GeocodioTests(ApiFactory factory)
{
    private static AddressCheck Read(string json, string street, string? city = "Arlington", string? state = "VA", string? zip = "22201") =>
        GeocodioAddressVerifier.Read(JsonDocument.Parse(json).RootElement, new AddressInput(street, city, state, zip));

    /// <summary>An answer in Geocodio's current field names (state_province, postal_code, unit_type, unit_number).</summary>
    private static string Answer(string type = "rooftop", double accuracy = 1, string number = "1109", string? zip4 = """{"plus4":["3516"],"zip9":["22201-3516"],"valid_delivery_area":true,"exact_match":true}""", string unit = "") => $$"""
        {"results":[{"address_components":{"number":"{{number}}","predirectional":"N","street":"Highland","suffix":"St","formatted_street":"N Highland St",{{unit}}
          "city":"Arlington","county":"Arlington County","state_province":"VA","postal_code":"22201","country":"US"},
          "formatted_address":"{{number}} N Highland St, Arlington, VA 22201","location":{"lat":38.886672,"lng":-77.094735},
          "accuracy":{{accuracy.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"accuracy_type":"{{type}}","source":"Arlington"{{(zip4 is null ? "" : $",\"fields\":{{\"zip4\":{zip4}}}")}}}]}
        """;

    [Fact]
    public void An_answer_from_Geocodio_becomes_a_status_the_person_entering_the_card_can_act_on()
    {
        // Found, deliverable, typed the way it is written.
        var ok = Read(Answer(), "1109 N Highland St");
        Assert.Equal(AddressStatuses.Verified, ok.Status);
        Assert.Equal(new AddressSuggestion("1109 N Highland St", "Arlington", "VA", "22201-3516"), ok.Suggested);
        Assert.Equal("The post office delivers to this address.", ok.Message);

        // Found, but written differently: the proper form is offered.
        var corrected = Read(Answer(), "1109 north highland street", zip: null);
        Assert.Equal(AddressStatuses.Corrected, corrected.Status);
        Assert.Contains("1109 N Highland St, Arlington, VA 22201-3516", corrected.Message);

        // The ZIP+4 data says the match is not exact: a unit is missing, or the one typed is not known. A typed unit is never dropped.
        const string inexact = """{"plus4":[],"zip9":[],"valid_delivery_area":true,"exact_match":false}""";
        Assert.Equal(AddressStatuses.NeedsUnit, Read(Answer(zip4: inexact), "1109 N Highland St").Status);
        var unit = Read(Answer(zip4: inexact), "1109 N Highland St Apt 4B");
        Assert.Equal(AddressStatuses.UnitNotConfirmed, unit.Status);
        Assert.Equal("1109 N Highland St Apt 4B", unit.Suggested!.StreetAddress);
        Assert.Equal("22201", unit.Suggested.Zip);
        var known = Read(Answer(unit: "\"unit_type\":\"Apt\",\"unit_number\":\"4B\","), "1109 N Highland St #4B");
        Assert.Equal((AddressStatuses.Corrected, "1109 N Highland St Apt 4B"), (known.Status, known.Suggested!.StreetAddress));

        // Not an address mail goes to: no delivery there, the wrong house number, only the street or the town, or nothing at all.
        Assert.Equal(AddressStatuses.NotFound, Read(Answer(zip4: """{"valid_delivery_area":false,"exact_match":true}"""), "1109 N Highland St").Status);
        Assert.Equal(AddressStatuses.NotFound, Read(Answer(number: "1101"), "1109 N Highland St").Status);
        Assert.Equal(AddressStatuses.NotFound, Read(Answer(type: "nearest_rooftop_match"), "1109 N Highland St").Status);
        Assert.Equal(AddressStatuses.NotFound, Read(Answer(type: "street_center"), "1109 N Highland St").Status);
        Assert.Equal(AddressStatuses.NotFound, Read(Answer(type: "place", accuracy: 0.5), "1109 N Highland St").Status);
        Assert.Equal(AddressStatuses.NotFound, Read(Answer(accuracy: 0.6), "1109 N Highland St").Status);
        Assert.Equal(AddressStatuses.NotFound, Read("""{"results":[]}""", "1 Nowhere Rd").Status);

        // Without ZIP+4 data the address is still found, said without a claim about delivery.
        var plain = Read(Answer(type: "range_interpolation", accuracy: 0.9, zip4: null), "1109 N Highland St");
        Assert.Equal((AddressStatuses.Verified, "This address was found.", "22201"), (plain.Status, plain.Message, plain.Suggested!.Zip));

        // Geocodio's earlier field names (state, zip, secondaryunit, secondarynumber) are read too.
        var older = Read("""
            {"results":[{"address_components":{"number":"42","formatted_street":"Oak Ave","secondaryunit":"Ste","secondarynumber":"200","city":"Joliet","state":"IL","zip":"60431"},
              "accuracy":1,"accuracy_type":"rooftop","fields":{"zip4":{"plus4":["1234"],"valid_delivery_area":true,"exact_match":true}}}]}
            """, "42 Oak Ave Ste 200", "Joliet", "IL", "60431");
        Assert.Equal(AddressStatuses.Verified, older.Status);
        Assert.Equal(new AddressSuggestion("42 Oak Ave Ste 200", "Joliet", "IL", "60431-1234"), older.Suggested);

        // Nothing said to the person entering a card names the company.
        foreach (var said in new[] { ok, corrected, unit, plain, Read("""{"results":[]}""", "1 Nowhere Rd") })
            Assert.DoesNotContain("Geocod", said.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_System_Administrator_enters_the_Geocodio_key_and_switches_between_the_services()
    {
        var site = await factory.LoginAsync(ApiFactory.SuperAdminUser, ApiFactory.SuperAdminPassword);
        var owner = await factory.LoginAsync(ApiFactory.OwnerUser, ApiFactory.OwnerPassword);
        const string key = "geo-test-0123456789abcdef-WXYZ";
        var geocodio = factory.Services.GetRequiredService<GeocodioAccountStore>();
        try
        {
            // Only System Administrators come near it.
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/system/geocodio")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.PutAsJsonAsync("/api/system/address-checking", new AddressCheckingRequest("geocodio"))).StatusCode);

            // A service that is not set up cannot be chosen.
            var refused = await site.PutAsJsonAsync("/api/system/address-checking", new AddressCheckingRequest("geocodio"));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("not set up yet", await refused.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.BadRequest, (await site.PutAsJsonAsync("/api/system/address-checking", new AddressCheckingRequest("somebody"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await site.PutAsJsonAsync("/api/system/geocodio", new GeocodioKeyRequest("short"))).StatusCode);

            // Entering the key saves it encrypted. Geocodio then checks addresses once it is chosen, and it alone.
            var saved = await site.PutAsJsonAsync("/api/system/geocodio", new GeocodioKeyRequest($"  {key} "));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            var raw = await saved.Content.ReadAsStringAsync();
            Assert.DoesNotContain(key, raw);
            Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync("/api/system/address-checking", new AddressCheckingRequest("geocodio"))).StatusCode);
            var dto = (await site.GetFromJsonAsync<GeocodioDto>("/api/system/geocodio"))!;
            Assert.Equal((true, "WXYZ", "Saved in the site", true), (dto.Configured, dto.KeyEnding, dto.Source, dto.InUse));
            Assert.Equal(key, geocodio.Current!.ApiKey);
            var choice = (await site.GetFromJsonAsync<AddressCheckingDto>("/api/system/address-checking"))!;
            Assert.Equal("geocodio", choice.First);
            Assert.True(choice.Services.Single(s => s.Id == "geocodio").Configured);
            Assert.Equal(["Geocodio", "USPS", "PostGrid"], choice.Services.Select(s => s.Name));

            // The key is nowhere it could be read: not as saved, and not in the audit log, which has only its last four characters.
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.DoesNotContain(key, (await db.SiteSettings.FindAsync("geocodio.account"))!.Value);
                var audited = db.AuditLogs.Where(a => a.Action == "GeocodioKeySet" || a.Action == "AddressCheckOrderChanged").Select(a => a.Details!).ToList();
                Assert.Contains(audited, d => d.Contains("WXYZ"));
                Assert.DoesNotContain(audited, d => d.Contains(key));
            }

            // The section on Connected Systems gets each service's key details with the choice, never a key.
            var mine = choice.Services.Single(s => s.Id == "geocodio");
            Assert.Equal(("WXYZ", "Saved in the site"), (mine.KeyEnding, mine.Source));
            Assert.False(choice.OffersAddresses);

            // PostGrid's key saved "key only" (to offer addresses as they are typed) does not change which service checks.
            const string other = "pg-addr-test-0123456789-QRST";
            var postGrid = factory.Services.GetRequiredService<PostGridAddressAccountStore>();
            try
            {
                Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync("/api/system/postgrid", new PostGridAddressRequest(other, false))).StatusCode);
                var both = (await site.GetFromJsonAsync<AddressCheckingDto>("/api/system/address-checking"))!;
                Assert.Equal(("geocodio", true, "QRST"), (both.First, both.OffersAddresses, both.Services.Single(s => s.Id == "postgrid").KeyEnding));
                Assert.DoesNotContain(other, await (await site.GetAsync("/api/system/address-checking")).Content.ReadAsStringAsync());
                // Switching is a choice of its own, and the one chosen is the only one that checks.
                Assert.Equal("postgrid", (await (await site.PutAsJsonAsync("/api/system/address-checking", new AddressCheckingRequest("postgrid"))).Content.ReadFromJsonAsync<AddressCheckingDto>())!.First);
                Assert.Equal("geocodio", (await (await site.PutAsJsonAsync("/api/system/address-checking", new AddressCheckingRequest("geocodio"))).Content.ReadFromJsonAsync<AddressCheckingDto>())!.First);
            }
            finally { await postGrid.RemoveAsync(); }
            // Each switch is in the audit log, with what it was before.
            using (var scope = factory.Services.CreateScope())
                Assert.Contains(scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs.Where(a => a.Action == "AddressCheckOrderChanged").Select(a => a.Details!).ToList(),
                    d => d.Contains("\"first\":\"Geocodio\"") && d.Contains("\"was\":\"PostGrid\""));

            // The key outlives a restart: it is read back from the database.
            await geocodio.LoadAsync();
            Assert.Equal(key, geocodio.Current!.ApiKey);

            // Removed: Geocodio is no longer set up and cannot be chosen.
            Assert.Equal(HttpStatusCode.OK, (await site.DeleteAsync("/api/system/geocodio")).StatusCode);
            Assert.False((await site.GetFromJsonAsync<GeocodioDto>("/api/system/geocodio"))!.Configured);
            // It stays the chosen service, with nothing to check with, until another is saved: no other service steps in.
            var after = (await site.GetFromJsonAsync<AddressCheckingDto>("/api/system/address-checking"))!;
            Assert.Equal(("geocodio", false), (after.First, after.Services.Single(s => s.Id == "geocodio").Configured));
        }
        finally { await geocodio.RemoveAsync(); }
    }
}
