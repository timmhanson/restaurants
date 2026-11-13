using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace RestaurantFeedback.Api.Services;

public class UspsOptions
{
    /// <summary>Consumer key and secret of a USPS developer app (App Service settings Usps__ClientId / Usps__ClientSecret).</summary>
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    /// <summary>https://apis.usps.com in production; https://apis-tem.usps.com is USPS's testing environment.</summary>
    public string BaseUrl { get; set; } = "https://apis.usps.com";
    /// <summary>Local development only: use <see cref="SampleAddressVerifier"/> instead of USPS.</summary>
    public bool Sample { get; set; }
}

public record AddressInput(string? Street, string? City, string? State, string? Zip);
public record AddressSuggestion(string StreetAddress, string City, string State, string Zip);

/// <param name="Status">One of <see cref="AddressStatuses"/>.</param>
/// <param name="Message">Plain words for the person entering the card.</param>
/// <param name="Suggested">The address as USPS has it, when it found one.</param>
public record AddressCheck(string Status, string Message, AddressSuggestion? Suggested = null);

public static class AddressStatuses
{
    /// <summary>USPS delivers here and the address was typed the way USPS writes it.</summary>
    public const string Verified = "Verified";
    /// <summary>USPS delivers here but writes the address differently (spelling, abbreviation, ZIP).</summary>
    public const string Corrected = "Corrected";
    /// <summary>The building exists but USPS needs an apartment, suite or unit number.</summary>
    public const string NeedsUnit = "NeedsUnit";
    /// <summary>The building exists but USPS does not know the apartment or unit that was given.</summary>
    public const string UnitNotConfirmed = "UnitNotConfirmed";
    /// <summary>USPS has no such address.</summary>
    public const string NotFound = "NotFound";
    /// <summary>Not enough was entered to look it up yet.</summary>
    public const string Incomplete = "Incomplete";
    /// <summary>The check could not be made (service not set up, busy or down). Never a reason to stop someone.</summary>
    public const string Unavailable = "Unavailable";

    /// <summary>Statuses worth remembering on the card.</summary>
    public static bool IsResult(string status) => status is Verified or Corrected or NeedsUnit or UnitNotConfirmed or NotFound;
}

/// <summary>Checks a US mailing address. Used only to help mail coupon cards; never blocks saving a card.</summary>
public interface IAddressVerifier
{
    bool Configured { get; }
    string Description { get; }
    Task<AddressCheck> CheckAsync(AddressInput input, CancellationToken ct = default);
}

public class NoAddressVerifier : IAddressVerifier
{
    public bool Configured => false;
    public string Description => "Not set up";
    public Task<AddressCheck> CheckAsync(AddressInput input, CancellationToken ct = default) =>
        Task.FromResult(new AddressCheck(AddressStatuses.Unavailable, "Address checking is not set up."));
}

/// <summary>
/// USPS Addresses 3.0 (GET /addresses/v3/address) with an OAuth client-credentials token. Answers are remembered
/// for a day so retyping or saving a card does not use up USPS's request allowance, and after USPS says it is
/// busy (429/503) the service is left alone for a while.
/// </summary>
public class UspsAddressVerifier(HttpClient http, UspsOptions options, IMemoryCache cache, ILogger<UspsAddressVerifier> log) : IAddressVerifier
{
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static string? _token;
    private static DateTime _tokenExpires = DateTime.MinValue;
    private static DateTime _pausedUntil = DateTime.MinValue;

    public bool Configured => true;
    public string Description => options.BaseUrl.Contains("-tem.") ? "USPS Addresses (testing environment)" : "USPS Addresses";

    public async Task<AddressCheck> CheckAsync(AddressInput input, CancellationToken ct = default)
    {
        var street = Clean(input.Street);
        var city = Clean(input.City);
        var zip5 = Zip5(input.Zip);
        var state = UsStates.Code(input.State);
        if (street.Length == 0) return new(AddressStatuses.Incomplete, "Enter the street address to check it.");
        if (state is null) return new(AddressStatuses.Incomplete, "Enter the state (for example IL) so the address can be checked with USPS.");
        if (city.Length == 0 && zip5 is null) return new(AddressStatuses.Incomplete, "Enter the city or the zip so the address can be checked with USPS.");

        var key = $"usps:{street}|{city}|{state}|{zip5}".ToUpperInvariant();
        if (cache.TryGetValue(key, out AddressCheck? known) && known is not null) return known;
        if (DateTime.UtcNow < _pausedUntil) return Busy();

        AddressCheck result;
        try
        {
            result = await AskAsync(street, city, state, zip5, input, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning(ex, "USPS address check failed");
            return new(AddressStatuses.Unavailable, "USPS could not be reached to check this address. You can still save the card.");
        }
        if (AddressStatuses.IsResult(result.Status)) cache.Set(key, result, TimeSpan.FromHours(24));
        return result;
    }

    private async Task<AddressCheck> AskAsync(string street, string city, string state, string? zip5, AddressInput typed, CancellationToken ct)
    {
        var query = new List<string> { $"streetAddress={Uri.EscapeDataString(street)}", $"state={state}" };
        if (city.Length > 0) query.Add($"city={Uri.EscapeDataString(city)}");
        if (zip5 is not null) query.Add($"ZIPCode={zip5}");

        for (var attempt = 0; ; attempt++)
        {
            var token = await TokenAsync(ct);
            if (token is null) return new(AddressStatuses.Unavailable, "Address checking could not sign in to USPS. You can still save the card.");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{options.BaseUrl.TrimEnd('/')}/addresses/v3/address?{string.Join('&', query)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            if (res.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                _token = null; // expired early; fetch a new one and try once more
                continue;
            }
            if (res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5);
                _pausedUntil = DateTime.UtcNow + (wait < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait);
                log.LogWarning("USPS address checks paused until {Until:u} ({Status})", _pausedUntil, (int)res.StatusCode);
                return Busy();
            }
            if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return Refused(res.StatusCode, body);
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("USPS address check answered {Status}", (int)res.StatusCode);
                return new(AddressStatuses.Unavailable, "USPS could not check this address just now. You can still save the card.");
            }
            return Read(JsonDocument.Parse(body).RootElement, typed);
        }
    }

    /// <summary>Turns USPS's answer into a status, comparing what it returned with what was typed.</summary>
    public static AddressCheck Read(JsonElement root, AddressInput typed)
    {
        if (!root.TryGetProperty("address", out var a) || a.ValueKind != JsonValueKind.Object)
            return new(AddressStatuses.NotFound, "USPS does not recognise this address. Check the street number and spelling.");
        var line = string.Join(' ', new[] { Text(a, "streetAddress"), Text(a, "secondaryAddress") }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var zip = Text(a, "ZIPCode") ?? "";
        if (Text(a, "ZIPPlus4") is { Length: 4 } plus4) zip += "-" + plus4;
        var suggested = new AddressSuggestion(line, Text(a, "city") ?? "", Text(a, "state") ?? "", zip);
        var dpv = root.TryGetProperty("additionalInfo", out var info) && info.ValueKind == JsonValueKind.Object ? Text(info, "DPVConfirmation") : null;
        var shown = $"{suggested.StreetAddress}, {suggested.City}, {suggested.State} {suggested.Zip}";

        return dpv switch
        {
            "D" => new(AddressStatuses.NeedsUnit, $"USPS has this building ({shown}) but needs an apartment, suite or unit number to deliver.", suggested),
            "S" => new(AddressStatuses.UnitNotConfirmed, $"USPS has this building ({shown}) but does not recognise the apartment or unit number.", suggested),
            "N" => new(AddressStatuses.NotFound, "USPS found the street but does not deliver to this number. Check the street number.", null),
            _ => SameAsTyped(suggested, typed)
                ? new(AddressStatuses.Verified, "USPS delivers to this address.", suggested)
                : new(AddressStatuses.Corrected, $"USPS writes this address as {shown}.", suggested)
        };
    }

    private static AddressCheck Refused(HttpStatusCode status, string body)
    {
        string? message = null;
        try
        {
            var root = JsonDocument.Parse(body).RootElement;
            if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object) message = Text(e, "message");
        }
        catch (JsonException) { /* fall through to the general wording */ }
        message ??= "";
        if (message.Contains("More than one", StringComparison.OrdinalIgnoreCase))
            return new(AddressStatuses.NotFound, "USPS found more than one address like this. Add the apartment or unit, or check the street name and direction (N, S, E, W).");
        if (message.Contains("insufficient", StringComparison.OrdinalIgnoreCase) || status == HttpStatusCode.BadRequest)
            return new(AddressStatuses.Incomplete, "USPS needs more to find this address. Check the city, state and zip.");
        return new(AddressStatuses.NotFound, "USPS does not recognise this address. Check the street number and spelling.");
    }

    private static AddressCheck Busy() => new(AddressStatuses.Unavailable, "USPS address checking is busy right now. You can still save the card.");

    private async Task<string?> TokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTime.UtcNow < _tokenExpires) return _token;
        await TokenLock.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTime.UtcNow < _tokenExpires) return _token;
            using var res = await http.PostAsJsonAsync($"{options.BaseUrl.TrimEnd('/')}/oauth2/v3/token",
                new { client_id = options.ClientId, client_secret = options.ClientSecret, grant_type = "client_credentials" }, ct);
            if (!res.IsSuccessStatusCode)
            {
                // Wrong key or secret: stop asking for a while instead of failing on every card.
                _pausedUntil = DateTime.UtcNow.AddMinutes(10);
                log.LogError("USPS refused the sign-in for address checking ({Status}). Check Usps__ClientId and Usps__ClientSecret.", (int)res.StatusCode);
                return null;
            }
            var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement;
            _token = Text(root, "access_token");
            // expires_in arrives as a number or a string of seconds; renew a minute early.
            var seconds = root.TryGetProperty("expires_in", out var exp)
                ? exp.ValueKind == JsonValueKind.Number ? exp.GetDouble() : double.TryParse(exp.GetString(), out var d) ? d : 600
                : 600;
            _tokenExpires = DateTime.UtcNow.AddSeconds(Math.Max(60, seconds - 60));
            return _token;
        }
        finally { TokenLock.Release(); }
    }

    private static bool SameAsTyped(AddressSuggestion s, AddressInput typed) =>
        Canon(s.StreetAddress) == Canon(typed.Street)
        && (string.IsNullOrWhiteSpace(typed.City) || Canon(s.City) == Canon(typed.City))
        && (Zip5(typed.Zip) is null || Zip5(s.Zip) == Zip5(typed.Zip));

    /// <summary>Upper case, no punctuation, single spaces: "123 Main St." equals "123 MAIN ST".</summary>
    private static string Canon(string? s) => Regex.Replace(Regex.Replace((s ?? "").ToUpperInvariant(), @"[^A-Z0-9 ]", ""), @"\s+", " ").Trim();
    private static string Clean(string? s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();
    private static string? Zip5(string? zip) => Regex.Match(zip ?? "", @"\d{5}") is { Success: true } m ? m.Value : null;
    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public static class UsStates
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Alabama"] = "AL", ["Alaska"] = "AK", ["Arizona"] = "AZ", ["Arkansas"] = "AR", ["California"] = "CA", ["Colorado"] = "CO", ["Connecticut"] = "CT",
        ["Delaware"] = "DE", ["District of Columbia"] = "DC", ["Florida"] = "FL", ["Georgia"] = "GA", ["Hawaii"] = "HI", ["Idaho"] = "ID", ["Illinois"] = "IL",
        ["Indiana"] = "IN", ["Iowa"] = "IA", ["Kansas"] = "KS", ["Kentucky"] = "KY", ["Louisiana"] = "LA", ["Maine"] = "ME", ["Maryland"] = "MD",
        ["Massachusetts"] = "MA", ["Michigan"] = "MI", ["Minnesota"] = "MN", ["Mississippi"] = "MS", ["Missouri"] = "MO", ["Montana"] = "MT", ["Nebraska"] = "NE",
        ["Nevada"] = "NV", ["New Hampshire"] = "NH", ["New Jersey"] = "NJ", ["New Mexico"] = "NM", ["New York"] = "NY", ["North Carolina"] = "NC",
        ["North Dakota"] = "ND", ["Ohio"] = "OH", ["Oklahoma"] = "OK", ["Oregon"] = "OR", ["Pennsylvania"] = "PA", ["Puerto Rico"] = "PR", ["Rhode Island"] = "RI",
        ["South Carolina"] = "SC", ["South Dakota"] = "SD", ["Tennessee"] = "TN", ["Texas"] = "TX", ["Utah"] = "UT", ["Vermont"] = "VT", ["Virginia"] = "VA",
        ["Washington"] = "WA", ["West Virginia"] = "WV", ["Wisconsin"] = "WI", ["Wyoming"] = "WY"
    };
    private static readonly HashSet<string> Codes = new(Names.Values, StringComparer.OrdinalIgnoreCase);

    /// <summary>"IL", "il", "Illinois" or "Ill." become "IL"; anything unrecognised is null.</summary>
    public static string? Code(string? state)
    {
        var s = (state ?? "").Trim().TrimEnd('.');
        if (s.Length == 2 && Codes.Contains(s)) return s.ToUpperInvariant();
        if (Names.TryGetValue(s, out var code)) return code;
        return s.Length >= 3 && Names.Keys.Where(n => n.StartsWith(s, StringComparison.OrdinalIgnoreCase)).ToList() is [var only] ? Names[only] : null;
    }
}

/// <summary>
/// Local development only (Usps:Sample = true): answers like USPS would without calling it, so the card form can
/// be tried. "Nowhere" streets do not exist, "Tower" buildings need a unit, and "Avenue"/"Street" are abbreviated.
/// </summary>
public class SampleAddressVerifier : IAddressVerifier
{
    public bool Configured => true;
    public string Description => "Sample address checking (not USPS)";

    public Task<AddressCheck> CheckAsync(AddressInput input, CancellationToken ct = default)
    {
        var street = (input.Street ?? "").Trim();
        var state = UsStates.Code(input.State);
        if (street.Length == 0 || state is null) return Task.FromResult(new AddressCheck(AddressStatuses.Incomplete, "Enter the street and state to check the address."));
        if (street.Contains("Nowhere", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new AddressCheck(AddressStatuses.NotFound, "USPS does not recognise this address. Check the street number and spelling."));
        var tidy = Regex.Replace(Regex.Replace(street.ToUpperInvariant(), @"\bAVENUE\b", "AVE"), @"\bSTREET\b", "ST").Replace(".", "");
        var zip = Regex.Match(input.Zip ?? "", @"\d{5}") is { Success: true } m ? m.Value : "60431";
        var suggestion = new AddressSuggestion(tidy, (input.City ?? "Joliet").Trim().ToUpperInvariant(), state, zip + "-1234");
        var shown = $"{suggestion.StreetAddress}, {suggestion.City}, {suggestion.State} {suggestion.Zip}";
        if (street.Contains("Tower", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new AddressCheck(AddressStatuses.NeedsUnit, $"USPS has this building ({shown}) but needs an apartment, suite or unit number to deliver.", suggestion));
        return Task.FromResult(tidy == street.ToUpperInvariant()
            ? new AddressCheck(AddressStatuses.Verified, "USPS delivers to this address.", suggestion)
            : new AddressCheck(AddressStatuses.Corrected, $"USPS writes this address as {shown}.", suggestion));
    }
}
