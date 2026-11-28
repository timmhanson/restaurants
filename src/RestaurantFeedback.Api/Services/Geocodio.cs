using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Services;

public class GeocodioOptions
{
    /// <summary>A fallback for the key entered on Connected Systems (App Service setting Geocodio__ApiKey).</summary>
    public string? ApiKey { get; set; }
    /// <summary>Geocodio's API, with its version.</summary>
    public string BaseUrl { get; set; } = "https://api.geocod.io/v2.1";
}

/// <summary>The services that can check a guest's mailing address. The System Administrator chooses the one that does.</summary>
public static class AddressServices
{
    public const string Geocodio = "geocodio", Usps = "usps", PostGrid = "postgrid";
    public static readonly string[] All = [Geocodio, Usps, PostGrid];
    public static string Name(string id) => id switch { Geocodio => "Geocodio", Usps => "USPS", PostGrid => "PostGrid", _ => id };
}

/// <summary>
/// Which service checks guest addresses: the System Administrator's choice on Connected Systems, kept in the site's
/// settings (it is a choice, not a secret). The chosen service does every check; the others are never asked, whether
/// or not their keys are saved. Until a choice has been made, the service that checked before stands: USPS when its
/// account is set up, else PostGrid (PostGrid first when its key was saved that way), else Geocodio.
/// </summary>
public class AddressServiceChoice(IServiceScopeFactory scopes, UspsAccountStore usps, PostGridAddressAccountStore postGrid, GeocodioAccountStore geocodio)
{
    private const string SettingKey = "addresses.first";
    private volatile string? _chosen;

    /// <summary>Its key or account has been entered.</summary>
    public bool IsSetUp(string service) => service switch
    {
        AddressServices.Geocodio => geocodio.Current is not null,
        AddressServices.PostGrid => postGrid.Current is not null,
        _ => usps.Current is not null
    };

    /// <summary>The one service that checks addresses.</summary>
    public string First
    {
        get
        {
            if (_chosen is { } chosen) return chosen;
            string[] before = postGrid.Current?.UseFirst == true
                ? [AddressServices.PostGrid, AddressServices.Usps, AddressServices.Geocodio]
                : [AddressServices.Usps, AddressServices.PostGrid, AddressServices.Geocodio];
            return before.FirstOrDefault(IsSetUp) ?? before[0];
        }
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().SiteSettings.FindAsync([SettingKey], ct);
        _chosen = row is not null && AddressServices.All.Contains(row.Value) ? row.Value : null;
    }

    public async Task SetAsync(string service, string by, CancellationToken ct = default)
    {
        if (!AddressServices.All.Contains(service)) throw new ArgumentException("Not an address service.", nameof(service));
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.SiteSettings.FindAsync([SettingKey], ct);
            if (row is null) db.SiteSettings.Add(row = new SiteSetting { Key = SettingKey });
            row.Value = service;
            row.UpdatedAt = DateTime.UtcNow;
            row.UpdatedBy = by;
            await db.SaveChangesAsync(ct);
        }
        _chosen = service;
    }
}

public record GeocodioAccount(string ApiKey, string Source, DateTime? SavedAt, string? SavedBy);

/// <summary>
/// The site's Geocodio API key, entered by the System Administrator on Connected Systems. Encrypted with Data
/// Protection before it is stored; never returned to a browser or logged. Geocodio__ApiKey is a fallback.
/// </summary>
public class GeocodioAccountStore(IServiceScopeFactory scopes, IDataProtectionProvider protection, GeocodioOptions options, ILogger<GeocodioAccountStore> log)
{
    private const string SettingKey = "geocodio.account";
    private readonly IDataProtector _protector = protection.CreateProtector("RestaurantFeedback.SiteSecret.Geocodio.v1");
    private volatile GeocodioAccount? _current;
    private int _version;

    private record Saved(string ApiKey);

    public GeocodioAccount? Current => _current;
    /// <summary>Goes up every time the key changes, so a pause after a refusal does not outlive the key that caused it.</summary>
    public int Version => _version;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        GeocodioAccount? found = null;
        try
        {
            using var scope = scopes.CreateScope();
            var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().SiteSettings.FindAsync([SettingKey], ct);
            if (row is not null)
            {
                var saved = JsonSerializer.Deserialize<Saved>(_protector.Unprotect(row.Value))!;
                found = new GeocodioAccount(saved.ApiKey, "Saved in the site", DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc), row.UpdatedBy);
            }
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            log.LogError(ex, "The saved Geocodio key can no longer be read and must be entered again");
        }
        if (found is null && !string.IsNullOrWhiteSpace(options.ApiKey)) found = new GeocodioAccount(options.ApiKey.Trim(), "App settings", null, null);
        _current = found;
        Interlocked.Increment(ref _version);
    }

    public async Task SaveAsync(string apiKey, string by, CancellationToken ct = default)
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.SiteSettings.FindAsync([SettingKey], ct);
            if (row is null) db.SiteSettings.Add(row = new SiteSetting { Key = SettingKey });
            row.Value = _protector.Protect(JsonSerializer.Serialize(new Saved(apiKey.Trim())));
            row.UpdatedAt = DateTime.UtcNow;
            row.UpdatedBy = by;
            await db.SaveChangesAsync(ct);
        }
        await LoadAsync(ct);
    }

    public async Task RemoveAsync(CancellationToken ct = default)
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.SiteSettings.FindAsync([SettingKey], ct);
            if (row is not null)
            {
                db.SiteSettings.Remove(row);
                await db.SaveChangesAsync(ct);
            }
        }
        await LoadAsync(ct);
    }
}

/// <summary>
/// Checks a guest's address with Geocodio (GET /geocode with the USPS ZIP+4 data appended, fields=zip4): whether
/// the address is found at that house number, how it is properly written, and, from the ZIP+4 data, whether the
/// post office delivers there and whether a unit number is missing. Each check uses two of the account's lookups
/// (the address and the ZIP+4 data). Answers are remembered for a day, and after a refusal (a bad key, the daily
/// limit) or a busy answer Geocodio is left alone for a while. The key travels in a header, never in the address
/// of the request. What a person entering a card is told never names the company.
/// </summary>
public class GeocodioAddressVerifier(HttpClient http, GeocodioAccountStore account, GeocodioOptions options, IMemoryCache cache, ILogger<GeocodioAddressVerifier> log) : IAddressVerifier
{
    private static DateTime _pausedUntil = DateTime.MinValue;
    private static int _accountVersion = -1;
    private static string? _problem;

    private GeocodioAccount? Account
    {
        get
        {
            if (account.Version != _accountVersion)
            {
                _accountVersion = account.Version;
                _pausedUntil = DateTime.MinValue;
                _problem = null;
            }
            return account.Current;
        }
    }

    public bool Configured => Account is not null;
    public string Description => "Geocodio";

    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct = default)
    {
        if (Account is null) return (false, "No Geocodio API key has been entered.");
        _pausedUntil = DateTime.MinValue;
        _problem = null;
        var r = await CheckAsync(new AddressInput("475 L'Enfant Plaza SW", "Washington", "DC", "20260"), ct, remember: false);
        return AddressStatuses.IsResult(r.Status)
            ? (true, "Geocodio accepted the key and answered an address check. Address checking through Geocodio is working. (The test used two lookups.)")
            : (false, _problem ?? "Geocodio could not be reached. Try again in a minute.");
    }

    public Task<AddressCheck> CheckAsync(AddressInput input, CancellationToken ct = default) => CheckAsync(input, ct, remember: true);

    private async Task<AddressCheck> CheckAsync(AddressInput input, CancellationToken ct, bool remember)
    {
        if (Account is not { } a) return new(AddressStatuses.Unavailable, "Address checking is not set up.");
        var street = Clean(input.Street);
        var city = Clean(input.City);
        var zip5 = Zip5(input.Zip);
        var state = UsStates.Code(input.State);
        if (street.Length == 0) return new(AddressStatuses.Incomplete, "Enter the street address to check it.");
        if (state is null) return new(AddressStatuses.Incomplete, "Enter the state (for example IL) so the address can be checked.");
        if (city.Length == 0 && zip5 is null) return new(AddressStatuses.Incomplete, "Enter the city or the zip so the address can be checked.");

        var cacheKey = $"geocodio:{street}|{city}|{state}|{zip5}".ToUpperInvariant();
        if (remember && cache.TryGetValue(cacheKey, out AddressCheck? known) && known is not null) return known;
        if (DateTime.UtcNow < _pausedUntil) return new(AddressStatuses.Unavailable, "Address checking is paused for a few minutes. You can still save the card.");

        try
        {
            var whole = string.Join(", ", new[] { street, city, $"{state} {zip5}".Trim() }.Where(s => s.Length > 0));
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{options.BaseUrl.TrimEnd('/')}/geocode?q={Uri.EscapeDataString(whole)}&country=US&fields=zip4&limit=1");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", a.ApiKey);
            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                var said = Said(body);
                // Geocodio could not read it as an address at all: that is an answer about the address, not an outage.
                if (res.StatusCode == HttpStatusCode.UnprocessableEntity)
                    return Remember(new(AddressStatuses.NotFound, "This address could not be understood. Check the street number, the spelling and the city."));
                if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    _problem = $"Geocodio did not accept the key, or the account has reached its daily limit ({(int)res.StatusCode}{said}). Check the key on Connected Systems and the usage in Geocodio's dashboard.";
                    _pausedUntil = DateTime.UtcNow.AddMinutes(10);
                }
                else if (res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    _problem = $"Geocodio is busy ({(int)res.StatusCode}).";
                    _pausedUntil = DateTime.UtcNow.AddMinutes(2);
                }
                else _problem = $"Geocodio answered the address check with an error ({(int)res.StatusCode}{said}).";
                log.LogWarning("Geocodio address check answered {Status}{Said}", (int)res.StatusCode, said);
                return new(AddressStatuses.Unavailable, "The address could not be checked just now. You can still save the card.");
            }
            return Remember(Read(JsonDocument.Parse(body).RootElement, input));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning(ex, "Geocodio address check failed");
            _problem = "Geocodio could not be reached. Try again in a minute.";
            return new(AddressStatuses.Unavailable, "The address service could not be reached. You can still save the card.");
        }

        AddressCheck Remember(AddressCheck result)
        {
            if (remember && AddressStatuses.IsResult(result.Status)) cache.Set(cacheKey, result, TimeSpan.FromHours(24));
            return result;
        }
    }

    /// <summary>How an apartment, suite or unit is written at the end of a street line: "Apt 4B", "Ste 200", "#12".</summary>
    private static readonly Regex Unit = new(@"(?:\b(?:apt|apartment|unit|suite|ste|bldg|building|lot|trlr|fl|floor|rm|room|spc|space)\b\.?\s*#?\s*[\w-]+|#\s*[\w-]+)\s*$", RegexOptions.IgnoreCase);

    /// <summary>
    /// Turns Geocodio's answer ({results:[{address_components, accuracy, accuracy_type, fields:{zip4}}]}, best first)
    /// into a status. The address counts as found only when Geocodio placed it at that house number (rooftop, point
    /// or a numbered range) with good accuracy; a street, a town or the nearest other building is "not found". With
    /// ZIP+4 data, an address the post office does not deliver to is "not found", and an inexact match means a unit
    /// number is missing or not known. Both versions of Geocodio's field names are read.
    /// </summary>
    public static AddressCheck Read(JsonElement root, AddressInput typed)
    {
        var notFound = new AddressCheck(AddressStatuses.NotFound, "This address could not be found. Check the street number and spelling.");
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0) return notFound;
        var best = results[0];
        var type = Text(best, "accuracy_type") ?? "";
        var accuracy = best.TryGetProperty("accuracy", out var acc) && acc.ValueKind == JsonValueKind.Number ? acc.GetDouble() : 0;
        var c = best.TryGetProperty("address_components", out var ac) ? ac : default;
        var number = Text(c, "number");
        var name = Text(c, "formatted_street");
        var typedStreet = Clean(typed.Street);
        var typedNumber = Regex.Match(typedStreet, @"^\d+[A-Za-z]?").Value;
        if (type is not ("rooftop" or "point" or "range_interpolation") || accuracy < 0.8 || number is null || name is null) return notFound;
        // Placed at a different house number: not this address.
        if (typedNumber.Length > 0 && !string.Equals(typedNumber, number, StringComparison.OrdinalIgnoreCase)) return notFound;

        var typedUnit = Unit.Match(typedStreet) is { Success: true } u ? u.Value.Trim() : null;
        var unit = string.Join(' ', new[] { Text(c, "unit_type") ?? Text(c, "secondaryunit"), Text(c, "unit_number") ?? Text(c, "secondarynumber") }.Where(s => !string.IsNullOrWhiteSpace(s)));
        // A unit that was typed is never dropped from the address offered back, whether or not Geocodio knows it.
        var line = string.Join(' ', new[] { number, name, unit.Length > 0 ? unit : typedUnit }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var zip = Zip5(Text(c, "postal_code") ?? Text(c, "zip")) ?? Zip5(typed.Zip) ?? "";

        JsonElement zip4 = default;
        var hasZip4 = best.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("zip4", out zip4) && zip4.ValueKind == JsonValueKind.Object;
        if (hasZip4 && zip.Length == 5 && zip4.TryGetProperty("plus4", out var plus) && plus.ValueKind == JsonValueKind.Array && plus.GetArrayLength() == 1
            && plus[0].ValueKind == JsonValueKind.String && plus[0].GetString() is { Length: 4 } four)
            zip += "-" + four;
        var suggested = new AddressSuggestion(line, Text(c, "city") ?? Clean(typed.City), (Text(c, "state_province") ?? Text(c, "state") ?? UsStates.Code(typed.State) ?? "").ToUpperInvariant(), zip);
        var shown = $"{suggested.StreetAddress}, {suggested.City}, {suggested.State} {suggested.Zip}";

        if (hasZip4 && Flag(zip4, "valid_delivery_area") == false)
            return new(AddressStatuses.NotFound, "This address was found, but the post office does not deliver mail there. Check the street number and spelling.");
        if (hasZip4 && Flag(zip4, "exact_match") == false)
            return typedUnit is null
                ? new(AddressStatuses.NeedsUnit, $"This building was found ({shown}) but it needs an apartment, suite or unit number for the post office to deliver.", suggested)
                : new(AddressStatuses.UnitNotConfirmed, $"This building was found ({shown}) but the apartment, suite or unit could not be confirmed. Check it with the guest if you can.", suggested);
        if (SameAsTyped(suggested, typed))
            return new(AddressStatuses.Verified, hasZip4 ? "The post office delivers to this address." : "This address was found.", suggested);
        return new(AddressStatuses.Corrected, $"This address is written as {shown}.", suggested);
    }

    /// <summary>Geocodio's own explanation from an error reply ({"error": "..."}), as ": message", or "".</summary>
    private static string Said(string body)
    {
        try
        {
            var text = Text(JsonDocument.Parse(body).RootElement, "error");
            return string.IsNullOrWhiteSpace(text) ? "" : ": " + (text.Length > 300 ? text[..300] : text);
        }
        catch (JsonException) { return ""; }
    }

    private static bool? Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.True ? true : v.ValueKind == JsonValueKind.False ? false : null : null;

    private static bool SameAsTyped(AddressSuggestion s, AddressInput typed) =>
        Canon(s.StreetAddress) == Canon(typed.Street)
        && (string.IsNullOrWhiteSpace(typed.City) || Canon(s.City) == Canon(typed.City))
        && (Zip5(typed.Zip) is null || Zip5(s.Zip) == Zip5(typed.Zip));

    private static string Canon(string? s) => Regex.Replace(Regex.Replace((s ?? "").ToUpperInvariant(), @"[^A-Z0-9 ]", ""), @"\s+", " ").Trim();
    private static string Clean(string? s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();
    private static string? Zip5(string? zip) => Regex.Match(zip ?? "", @"\d{5}") is { Success: true } m ? m.Value : null;
    private static string? Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
