using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RestaurantFeedback.Api.Services;

public class PostageOptions
{
    /// <summary>EasyPost API key (App Service setting Postage__EasyPostApiKey). A test key buys sample labels at no cost.</summary>
    public string? EasyPostApiKey { get; set; }
    public string EasyPostBaseUrl { get; set; } = "https://api.easypost.com/v2/";
    /// <summary>Never pay more than this for one card; guards against buying the wrong class of mail.</summary>
    public int MaxCentsPerCard { get; set; } = 150;
    /// <summary>Local development only: use <see cref="SamplePostageProvider"/> instead of a real provider.</summary>
    public bool Sample { get; set; }
}

public record PostageAddress(string Name, string Street, string City, string State, string Zip, string? Phone = null);

/// <param name="Cents">What was charged.</param>
/// <param name="LabelUrl">Image of the mailing label: addresses, barcode and the postage itself.</param>
/// <param name="Test">Bought with a test key: a sample, not valid postage.</param>
public record PostageResult(string Provider, string Id, int Cents, string LabelUrl, string? TrackingCode, bool Test);

/// <summary>A problem buying postage, with a message fit to show the person printing.</summary>
public class PostageException(string message) : Exception(message);

/// <summary>
/// Buys USPS postage for one postcard. Stamps by hand need none of this. Each investment group pays from its own
/// account: its key (kept encrypted, see <see cref="GroupPostageAccounts"/>) is passed as <c>accountKey</c>. The
/// site-wide key, when one is set, is only used for a group that has not added its own.
/// </summary>
public interface IPostageProvider
{
    /// <summary>A site-wide account is set up, so groups without their own account can still buy postage.</summary>
    bool Configured { get; }
    /// <summary>Groups can bring their own account key.</summary>
    bool AcceptsAccountKeys { get; }
    string Description { get; }
    Task<PostageResult> BuyPostcardAsync(PostageAddress from, PostageAddress to, double widthIn, double heightIn, string? accountKey = null, CancellationToken ct = default);
}

public class NoPostageProvider : IPostageProvider
{
    public bool Configured => false;
    public bool AcceptsAccountKeys => false;
    public string Description => "Not set up (stamps go on by hand)";
    public Task<PostageResult> BuyPostcardAsync(PostageAddress from, PostageAddress to, double widthIn, double heightIn, string? accountKey = null, CancellationToken ct = default) =>
        throw new PostageException("Buying postage is not set up on this site. Print the card and add a stamp by hand.");
}

/// <summary>
/// USPS First-Class postage through EasyPost's Shipments API: create a shipment for a card, pick the USPS "First"
/// rate, refuse it if it costs more than <see cref="PostageOptions.MaxCentsPerCard"/>, then buy it. The label
/// EasyPost returns carries both addresses, the barcode and the postage.
/// </summary>
public class EasyPostProvider : IPostageProvider
{
    private readonly HttpClient _http;
    private readonly PostageOptions _options;

    public EasyPostProvider(HttpClient http, PostageOptions options)
    {
        _http = http;
        _options = options;
        _http.BaseAddress = new Uri(options.EasyPostBaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public bool Configured => !string.IsNullOrWhiteSpace(_options.EasyPostApiKey);
    public bool AcceptsAccountKeys => true;
    public string Description => "EasyPost (USPS First-Class)";

    public async Task<PostageResult> BuyPostcardAsync(PostageAddress from, PostageAddress to, double widthIn, double heightIn, string? accountKey = null, CancellationToken ct = default)
    {
        // The investment group's own account pays; the site-wide key is only a fallback for groups without one.
        var key = string.IsNullOrWhiteSpace(accountKey) ? _options.EasyPostApiKey : accountKey;
        if (string.IsNullOrWhiteSpace(key)) throw new PostageException("No EasyPost account is set up for this investment group. Add one in Settings, or add a stamp by hand.");
        var shipment = await SendAsync(key, "shipments", new
        {
            shipment = new
            {
                to_address = Address(to),
                from_address = Address(from),
                // A postcard: EasyPost's predefined "Card", with its real size (inches) and weight (ounces).
                parcel = new { predefined_package = "Card", length = Math.Max(widthIn, heightIn), width = Math.Min(widthIn, heightIn), height = 0.016, weight = 0.2 },
                options = new { label_format = "PNG" }
            }
        }, ct);

        var id = shipment.GetProperty("id").GetString()!;
        JsonElement? chosen = null;
        if (shipment.TryGetProperty("rates", out var rates) && rates.ValueKind == JsonValueKind.Array)
        {
            var usps = rates.EnumerateArray().Where(r => Text(r, "carrier")?.StartsWith("USPS", StringComparison.OrdinalIgnoreCase) == true).ToList();
            chosen = usps.Where(r => Text(r, "service")?.StartsWith("First", StringComparison.OrdinalIgnoreCase) == true).OrderBy(Cents).Cast<JsonElement?>().FirstOrDefault()
                     ?? usps.OrderBy(Cents).Cast<JsonElement?>().FirstOrDefault();
        }
        if (chosen is null) throw new PostageException("USPS did not offer a postcard rate for this address. Check the guest's address, or add a stamp by hand.");
        var cents = Cents(chosen.Value);
        if (cents > _options.MaxCentsPerCard)
            throw new PostageException($"The cheapest USPS rate offered was ${cents / 100m:0.00} ({Text(chosen.Value, "service")}), more than the ${_options.MaxCentsPerCard / 100m:0.00} limit for one card, so nothing was bought. Add a stamp by hand.");

        var bought = await SendAsync(key, $"shipments/{id}/buy", new { rate = new { id = Text(chosen.Value, "id") } }, ct);
        var label = bought.TryGetProperty("postage_label", out var pl) && pl.ValueKind == JsonValueKind.Object ? Text(pl, "label_url") : null;
        if (string.IsNullOrEmpty(label)) throw new PostageException("Postage was bought but EasyPost returned no label. Check the EasyPost dashboard before trying again.");
        var paid = bought.TryGetProperty("selected_rate", out var sr) && sr.ValueKind == JsonValueKind.Object ? Cents(sr) : cents;
        return new PostageResult("EasyPost", id, paid, label, Text(bought, "tracking_code"), Text(bought, "mode") == "test");
    }

    private static object Address(PostageAddress a) => new { name = a.Name, street1 = a.Street, city = a.City, state = a.State, zip = a.Zip, country = "US", phone = a.Phone };

    private async Task<JsonElement> SendAsync(string key, string path, object body, CancellationToken ct)
    {
        HttpResponseMessage res;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            // EasyPost uses HTTP Basic with the API key as the username and no password.
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(key + ":")));
            res = await _http.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new PostageException("Could not reach EasyPost to buy postage. Try again, or add a stamp by hand.");
        }
        var text = await res.Content.ReadAsStringAsync(ct);
        JsonElement json;
        try { json = JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { throw new PostageException($"EasyPost gave an unexpected answer ({(int)res.StatusCode})."); }
        if (res.IsSuccessStatusCode) return json;
        var error = json.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? e : json;
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new PostageException("EasyPost did not accept the account key. Check the key in Settings (it may have been replaced or removed at EasyPost).");
        var message = Text(error, "message") ?? $"request refused ({(int)res.StatusCode})";
        var details = error.TryGetProperty("errors", out var list) && list.ValueKind == JsonValueKind.Array
            ? string.Join("; ", list.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Object ? $"{Text(x, "field")}: {Text(x, "message")}" : x.ToString()).Take(4))
            : "";
        throw new PostageException($"EasyPost: {message}{(details.Length > 0 ? $" ({details})" : "")}");
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>EasyPost sends money as a string such as "0.65".</summary>
    private static int Cents(JsonElement rate) =>
        decimal.TryParse(Text(rate, "rate"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d * 100) : int.MaxValue;
}

/// <summary>
/// For local development and demos only (Postage:Sample = true): pretends to buy postage and returns a sample
/// label marked as a test, so the whole print flow can be tried without a postage account. Never real postage.
/// </summary>
public class SamplePostageProvider : IPostageProvider
{
    private int _count;
    public bool Configured => true;
    public bool AcceptsAccountKeys => true;
    public string Description => "Sample postage (not real)";

    public Task<PostageResult> BuyPostcardAsync(PostageAddress from, PostageAddress to, double widthIn, double heightIn, string? accountKey = null, CancellationToken ct = default)
    {
        var n = Interlocked.Increment(ref _count);
        return Task.FromResult(new PostageResult("Sample", $"sample_{DateTime.UtcNow:yyyyMMddHHmmss}_{n}", 65, "/sample-postage-label.svg", null, true));
    }
}

/// <summary>What is known about a group's own postage account. The key itself never leaves the server.</summary>
public record PostageAccountInfo(bool HasOwnAccount, string? KeyEnding, bool LooksLikeTestKey, DateTime? AddedAt, string? AddedBy);

/// <summary>
/// Each investment group's own EasyPost key, so each group pays for its own postage. The key is encrypted with
/// ASP.NET Core Data Protection before it is stored and is only decrypted at the moment postage is bought. It is
/// never returned to a browser, logged or written to the audit log; people see only its last four characters.
/// </summary>
public class GroupPostageAccounts(RestaurantFeedback.Api.Data.AppDbContext db, Microsoft.AspNetCore.DataProtection.IDataProtectionProvider protection, ILogger<GroupPostageAccounts> log)
{
    private readonly Microsoft.AspNetCore.DataProtection.IDataProtector _protector = protection.CreateProtector("RestaurantFeedback.GroupPostageKey.v1");

    public async Task<PostageAccountInfo> DescribeAsync(int groupId, CancellationToken ct = default)
    {
        var g = await db.Groups.FindAsync([groupId], ct);
        return g?.PostageKeyEncrypted is null
            ? new PostageAccountInfo(false, null, false, null, null)
            : new PostageAccountInfo(true, g.PostageKeyEnding, g.PostageKeyIsTest, g.PostageKeySetAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null, g.PostageKeySetBy);
    }

    /// <summary>The group's key, decrypted for one purchase; null when the group has none (or it can no longer be read).</summary>
    public async Task<string?> KeyAsync(int groupId, CancellationToken ct = default)
    {
        var g = await db.Groups.FindAsync([groupId], ct);
        if (g?.PostageKeyEncrypted is null) return null;
        try { return Microsoft.AspNetCore.DataProtection.DataProtectionCommonExtensions.Unprotect(_protector, g.PostageKeyEncrypted); }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // The site's encryption keys were lost or replaced: the stored key cannot be recovered and must be entered again.
            log.LogError(ex, "The postage account key for group {GroupId} can no longer be decrypted", groupId);
            return null;
        }
    }

    public async Task<PostageAccountInfo> SetAsync(int groupId, string key, string by, CancellationToken ct = default)
    {
        var g = await db.Groups.FindAsync([groupId], ct) ?? throw new InvalidOperationException("No such group.");
        key = key.Trim();
        g.PostageKeyEncrypted = Microsoft.AspNetCore.DataProtection.DataProtectionCommonExtensions.Protect(_protector, key);
        g.PostageKeyEnding = key[^4..];
        g.PostageKeyIsTest = key.StartsWith("EZTK", StringComparison.OrdinalIgnoreCase);
        g.PostageKeySetAt = DateTime.UtcNow;
        g.PostageKeySetBy = by;
        await db.SaveChangesAsync(ct);
        return await DescribeAsync(groupId, ct);
    }

    public async Task RemoveAsync(int groupId, CancellationToken ct = default)
    {
        var g = await db.Groups.FindAsync([groupId], ct);
        if (g is null) return;
        g.PostageKeyEncrypted = null; g.PostageKeyEnding = null; g.PostageKeyIsTest = false; g.PostageKeySetAt = null; g.PostageKeySetBy = null;
        await db.SaveChangesAsync(ct);
    }
}
