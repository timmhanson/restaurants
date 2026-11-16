using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Services;

/// <summary>
/// PostGrid (postgrid.com) prints and mails coupon cards on the site's own PostGrid account, and can check guest
/// addresses for the whole site. Investment groups never see PostGrid: they are billed by the site, per card, at
/// PostGrid's cost plus the site's fee. Settings section "PostGrid".
/// </summary>
public class PostGridOptions
{
    public string BaseUrl { get; set; } = "https://api.postgrid.com/print-mail/v1/";
    public string AddressBaseUrl { get; set; } = "https://api.postgrid.com";
    /// <summary>Optional site-wide address verification key (App Service setting PostGrid__AddressApiKey), used only when none is saved on Connected Systems.</summary>
    public string? AddressApiKey { get; set; }
    /// <summary>Optional Print &amp; Mail key (App Service setting PostGrid__MailApiKey), used only when none is saved on Connected Systems.</summary>
    public string? MailApiKey { get; set; }
    /// <summary>
    /// What PostGrid is expected to charge the site for one 6x4 First-Class card, until the System Administrator
    /// sets it on Connected Systems. PostGrid's published price on its free Starter plan is $0.902 (printing and
    /// postage included); paid plans are cheaper. PostGrid does not report the price of each card.
    /// </summary>
    public int EstimatedCentsPerCard { get; set; } = 91;
    /// <summary>The site's own fee per card, until the System Administrator sets it on Connected Systems.</summary>
    public int FeeCentsPerCard { get; set; } = 5;
    /// <summary>The most one group may be billed in a calendar month, until the System Administrator sets it on Connected Systems.</summary>
    public int MonthlyLimitCents { get; set; } = 5000;
    /// <summary>The most cards one mailing may send; a guard against sending far more than intended.</summary>
    public int MaxCardsPerMailing { get; set; } = 100;
    /// <summary>The site's public address, used for the card picture links PostGrid downloads. Defaults to the address of the request.</summary>
    public string? PublicBaseUrl { get; set; }
    /// <summary>Local development only: use <see cref="SamplePostGridMailer"/> instead of PostGrid.</summary>
    public bool Sample { get; set; }
    /// <summary>Run the hourly billing round (create, send and collect monthly bills). Off in automated tests.</summary>
    public bool BillingSchedule { get; set; } = true;
}

public record MailContact(string? FirstName, string? LastName, string? CompanyName, string AddressLine1, string? AddressLine2, string City, string State, string Zip);
public record MailPostcardRequest(MailContact To, MailContact From, string FrontHtml, string BackHtml, string Description, Dictionary<string, string> Metadata);
/// <param name="Status">ready, printing, processed_for_delivery, completed or cancelled.</param>
/// <param name="Live">False for a test-key order: nothing is printed, mailed or charged.</param>
/// <param name="PreviewUrl">A short-lived link to PostGrid's PDF of the printed card.</param>
public record MailedPostcard(string Id, string Status, bool Live, string? PreviewUrl, string? SendDate);
/// <summary>A letter (a printed bill): PostGrid adds a first page with the addresses, so the HTML needs no address block.</summary>
public record MailLetterRequest(MailContact To, MailContact From, string Html, string Description, Dictionary<string, string> Metadata);

/// <summary>A problem with PostGrid, in words fit to show the person mailing.</summary>
/// <param name="Answered">PostGrid gave a definite answer (so trying again needs a new idempotency key); false when it could not be reached.</param>
public class PostGridException(string message, bool answered) : Exception(message)
{
    public bool Answered { get; } = answered;
}

/// <summary>Sends postcards through PostGrid's Print &amp; Mail API with the site's key.</summary>
public interface IPostGridMailer
{
    string Description { get; }
    Task<(bool Ok, string Message)> TestKeyAsync(string key, CancellationToken ct = default);
    /// <param name="idempotencyKey">The same key within 24 hours returns the first answer instead of mailing again.</param>
    Task<MailedPostcard> CreatePostcardAsync(string key, MailPostcardRequest request, string idempotencyKey, CancellationToken ct = default);
    Task<MailedPostcard> GetPostcardAsync(string key, string id, CancellationToken ct = default);
    /// <summary>Cancels a card that has not started printing (status ready).</summary>
    Task<MailedPostcard> CancelPostcardAsync(string key, string id, CancellationToken ct = default);
    /// <summary>Prints and posts a letter (a monthly bill), black and white, First-Class.</summary>
    Task<MailedPostcard> CreateLetterAsync(string key, MailLetterRequest request, string idempotencyKey, CancellationToken ct = default);
}

public static class MailStatuses
{
    public const string Ready = "ready";
    public const string Cancelled = "cancelled";
    public static bool IsKeyForTest(string key) => key.StartsWith("test_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The same message without the mail house's name, for investment groups: they deal with this site, and which
    /// company prints the cards is the site's business.
    /// </summary>
    public static string ForClients(string message)
    {
        var s = Regex.Replace(message, @"PostGrid(?:'s|\u2019s)", "the mailing service's");
        s = s.Replace("PostGrid", "the mailing service");
        s = Regex.Replace(s, @"(^|[.!?]\s+)the mailing service", m => m.Groups[1].Value + "The mailing service");
        return s;
    }
}

public class PostGridMailer : IPostGridMailer
{
    private readonly HttpClient _http;

    public PostGridMailer(HttpClient http, PostGridOptions options)
    {
        _http = http;
        _http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public string Description => "PostGrid Print & Mail";

    public async Task<(bool Ok, string Message)> TestKeyAsync(string key, CancellationToken ct = default)
    {
        try
        {
            using var req = Request(HttpMethod.Get, "postcards?limit=1", key);
            using var res = await _http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode)
                return (true, MailStatuses.IsKeyForTest(key)
                    ? "PostGrid accepted the test key. Cards sent with it are not printed, mailed or charged; PostGrid shows a preview of each one."
                    : "PostGrid accepted the live key. Cards sent with it are printed, mailed and charged to this PostGrid account.");
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (false, $"PostGrid did not accept this key ({(int)res.StatusCode}{Said(body)}). Copy the Print & Mail API key again from PostGrid (Settings, API Keys).");
            return (false, $"PostGrid answered with an error ({(int)res.StatusCode}{Said(body)}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, "PostGrid could not be reached. Try again in a minute.");
        }
    }

    public async Task<MailedPostcard> CreatePostcardAsync(string key, MailPostcardRequest r, string idempotencyKey, CancellationToken ct = default)
    {
        var body = new
        {
            to = Contact(r.To),
            from = Contact(r.From),
            frontHTML = r.FrontHtml,
            backHTML = r.BackHtml,
            size = "6x4",
            mailingClass = "first_class",
            description = r.Description,
            metadata = r.Metadata
        };
        using var req = Request(HttpMethod.Post, "postcards", key);
        req.Content = JsonContent.Create(body);
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        return Read(await SendAsync(req, ct));
    }

    public async Task<MailedPostcard> GetPostcardAsync(string key, string id, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Get, $"postcards/{Uri.EscapeDataString(id)}", key);
        return Read(await SendAsync(req, ct));
    }

    public async Task<MailedPostcard> CancelPostcardAsync(string key, string id, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Delete, $"postcards/{Uri.EscapeDataString(id)}", key);
        return Read(await SendAsync(req, ct));
    }

    public async Task<MailedPostcard> CreateLetterAsync(string key, MailLetterRequest r, string idempotencyKey, CancellationToken ct = default)
    {
        var body = new
        {
            to = Contact(r.To),
            from = Contact(r.From),
            html = r.Html,
            addressPlacement = "insert_blank_page",
            color = false,
            doubleSided = false,
            mailingClass = "first_class",
            description = r.Description,
            metadata = r.Metadata
        };
        using var req = Request(HttpMethod.Post, "letters", key);
        req.Content = JsonContent.Create(body);
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        return Read(await SendAsync(req, ct));
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string key)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add("x-api-key", key);
        return req;
    }

    private static object Contact(MailContact c) => new
    {
        firstName = c.FirstName,
        lastName = c.LastName,
        companyName = c.CompanyName,
        addressLine1 = c.AddressLine1,
        addressLine2 = c.AddressLine2,
        city = c.City,
        provinceOrState = c.State,
        postalOrZip = c.Zip,
        countryCode = "US"
    };

    private async Task<JsonElement> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage res;
        try { res = await _http.SendAsync(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new PostGridException("PostGrid could not be reached. Nothing was charged that this site knows of; try again in a minute.", answered: false);
        }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            if (res.IsSuccessStatusCode)
            {
                try { return JsonDocument.Parse(text).RootElement.Clone(); }
                catch (JsonException) { throw new PostGridException($"PostGrid gave an unexpected answer ({(int)res.StatusCode}).", answered: true); }
            }
            if (res.StatusCode is HttpStatusCode.Unauthorized)
                throw new PostGridException("PostGrid did not accept the site's mailing key. The System Administrator can check it on Connected Systems.", answered: true);
            if ((int)res.StatusCode >= 500)
                throw new PostGridException($"PostGrid had a problem ({(int)res.StatusCode}{Said(text)}). Try again in a few minutes.", answered: false);
            throw new PostGridException($"PostGrid refused it ({(int)res.StatusCode}{Said(text)}).", answered: true);
        }
    }

    /// <summary>PostGrid's own explanation from an error reply ({"error":{"type","message"}}), as ": message", or "".</summary>
    public static string Said(string body)
    {
        try
        {
            var root = JsonDocument.Parse(body).RootElement;
            string? text = null;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var e))
                    text = e.ValueKind == JsonValueKind.Object ? Text(e, "message") ?? Text(e, "type") : e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                text ??= Text(root, "message");
            }
            else if (root.ValueKind == JsonValueKind.String) text = root.GetString();
            return string.IsNullOrWhiteSpace(text) ? "" : ": " + (text.Length > 300 ? text[..300] : text);
        }
        catch (JsonException)
        {
            var plain = body.Trim();
            return plain.Length is > 0 and < 300 && !plain.StartsWith('<') ? ": " + plain : "";
        }
    }

    public static MailedPostcard Read(JsonElement e) => new(
        Text(e, "id") ?? throw new PostGridException("PostGrid did not say which card it created. Check the PostGrid dashboard before trying again.", answered: true),
        Text(e, "status") ?? MailStatuses.Ready,
        e.TryGetProperty("live", out var live) && live.ValueKind == JsonValueKind.True,
        Text(e, "url"),
        Text(e, "sendDate"));

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Local development only (PostGrid:Sample = true): pretends to mail cards, as a test-key order would, so the whole
/// flow can be tried without a PostGrid account. Nothing is sent anywhere.
/// </summary>
public class SamplePostGridMailer : IPostGridMailer
{
    private int _count;
    private readonly Dictionary<string, string> _status = [];
    private readonly Dictionary<string, string> _byKey = [];
    public string Description => "Sample mailing (not PostGrid)";
    public Task<(bool Ok, string Message)> TestKeyAsync(string key, CancellationToken ct = default) =>
        Task.FromResult((true, "Sample mailing accepted the key (nothing is sent anywhere on this computer)."));

    public Task<MailedPostcard> CreatePostcardAsync(string key, MailPostcardRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        lock (_status)
        {
            if (!_byKey.TryGetValue(idempotencyKey, out var id))
            {
                id = _byKey[idempotencyKey] = $"postcard_sample{Interlocked.Increment(ref _count)}";
                _status[id] = MailStatuses.Ready;
            }
            return Task.FromResult(new MailedPostcard(id, _status[id], !MailStatuses.IsKeyForTest(key), null, DateTime.UtcNow.ToString("yyyy-MM-dd")));
        }
    }

    public Task<MailedPostcard> GetPostcardAsync(string key, string id, CancellationToken ct = default)
    {
        lock (_status) return Task.FromResult(new MailedPostcard(id, _status.GetValueOrDefault(id, MailStatuses.Ready), false, null, null));
    }

    public Task<MailedPostcard> CancelPostcardAsync(string key, string id, CancellationToken ct = default)
    {
        lock (_status) _status[id] = MailStatuses.Cancelled;
        return Task.FromResult(new MailedPostcard(id, MailStatuses.Cancelled, false, null, null));
    }

    public Task<MailedPostcard> CreateLetterAsync(string key, MailLetterRequest request, string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(new MailedPostcard($"letter_sample{Interlocked.Increment(ref _count)}", MailStatuses.Ready, false, null, null));
}

/// <param name="Source">"Saved in the site" or "App settings".</param>
public record MailAccount(string ApiKey, string Source, DateTime? SavedAt, string? SavedBy)
{
    public bool Test => MailStatuses.IsKeyForTest(ApiKey);
}

/// <summary>
/// The site's own PostGrid Print &amp; Mail key, entered by the System Administrator on Connected Systems. Encrypted
/// with Data Protection before it is stored and never returned to a browser, logged or audited beyond its last four
/// characters. PostGrid__MailApiKey is a fallback. Also holds the site's billing settings (see BillingSettings).
/// </summary>
public class SiteMailing(IServiceScopeFactory scopes, IDataProtectionProvider protection, PostGridOptions options, ILogger<SiteMailing> log)
{
    private const string KeySetting = "postgrid.mail";
    private const string BillingSetting = "mail.billing";
    private readonly IDataProtector _protector = protection.CreateProtector("RestaurantFeedback.SiteSecret.PostGridMail.v1");
    private volatile MailAccount? _account;
    private volatile BillingSettings? _billing;

    private record SavedKey(string ApiKey);

    public MailAccount? Account => _account;
    public BillingSettings Billing => _billing ?? Defaults;
    private BillingSettings Defaults => new() { CostCents = options.EstimatedCentsPerCard, FeeCents = options.FeeCentsPerCard, MonthlyLimitCents = options.MonthlyLimitCents };

    public async Task LoadAsync(CancellationToken ct = default)
    {
        MailAccount? account = null;
        BillingSettings? billing = null;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            try
            {
                var row = await db.SiteSettings.FindAsync([KeySetting], ct);
                if (row is not null)
                {
                    var saved = JsonSerializer.Deserialize<SavedKey>(_protector.Unprotect(row.Value))!;
                    account = new MailAccount(saved.ApiKey, "Saved in the site", DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc), row.UpdatedBy);
                }
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
            {
                log.LogError(ex, "The saved PostGrid mailing key can no longer be read and must be entered again");
            }
            try
            {
                var row = await db.SiteSettings.FindAsync([BillingSetting], ct);
                if (row is not null) billing = JsonSerializer.Deserialize<BillingSettings>(row.Value)?.Normalize();
            }
            catch (JsonException ex)
            {
                log.LogError(ex, "The saved billing settings could not be read; the defaults are used");
            }
        }
        if (account is null && !string.IsNullOrWhiteSpace(options.MailApiKey))
            account = new MailAccount(options.MailApiKey.Trim(), "App settings", null, null);
        _account = account;
        _billing = billing;
    }

    public async Task SaveKeyAsync(string apiKey, string by, CancellationToken ct = default)
    {
        await SiteSettingRows.SetAsync(scopes, KeySetting, _protector.Protect(JsonSerializer.Serialize(new SavedKey(apiKey.Trim()))), by, ct);
        await LoadAsync(ct);
    }

    public async Task RemoveKeyAsync(CancellationToken ct = default)
    {
        await SiteSettingRows.RemoveAsync(scopes, KeySetting, ct);
        await LoadAsync(ct);
    }

    public async Task SaveBillingAsync(BillingSettings billing, string by, CancellationToken ct = default)
    {
        await SiteSettingRows.SetAsync(scopes, BillingSetting, JsonSerializer.Serialize(billing.Normalize()), by, ct);
        await LoadAsync(ct);
    }
}

/// <summary>Reads and writes rows of the SiteSettings table from singletons.</summary>
public static class SiteSettingRows
{
    public static async Task SetAsync(IServiceScopeFactory scopes, string key, string value, string by, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.SiteSettings.FindAsync([key], ct);
        if (row is null) db.SiteSettings.Add(row = new SiteSetting { Key = key });
        row.Value = value;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = by;
        await db.SaveChangesAsync(ct);
    }

    public static async Task RemoveAsync(IServiceScopeFactory scopes, string key, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.SiteSettings.FindAsync([key], ct);
        if (row is null) return;
        db.SiteSettings.Remove(row);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>How one card looks, with the pictures already turned into the public addresses the mail house downloads.</summary>
public record CardLook(string Front, string Layout, string? Headline, string? Message, string? BackMessage, string Background, string Text,
    string? PictureUrl, string? LogoUrl, string? PhotoUrl)
{
    public static CardLook Plain(string? accent) =>
        new(CardFronts.Plain, CardLayouts.Centered, null, null, null, PostGridCardDesign.SafeColor(accent), "#ffffff", null, null, null);
}

/// <summary>
/// The two sides of a coupon card as PostGrid prints it: 6x4 inches trimmed, sent as 6.25 x 4.25 inches with bleed.
/// On the back, the right 2.4 inches and a 0.625-inch strip along the bottom are left blank for PostGrid's address
/// block, postage and barcode (PostGrid's 6x4 postcard guideline), so the coupon sits in the top-left area.
/// </summary>
public static class PostGridCardDesign
{
    private const string Page = "@page{size:6.25in 4.25in;margin:0}html,body{margin:0;padding:0;width:6.25in;height:4.25in;overflow:hidden;position:relative;font-family:Arial,Helvetica,sans-serif;color:#1d1d1f}";
    public const string DefaultHeadline = "Thank you!";

    public static string SafeColor(string? color, string fallback = "#005daa") => Regex.IsMatch(color ?? "", "^#[0-9a-fA-F]{6}$") ? color! : fallback;

    private static string Doc(string css, string body) =>
        $"<!doctype html><html><head><meta charset=\"utf-8\"><style>{Page}{css}</style></head><body>{body}</body></html>";

    /// <summary>
    /// The picture side: finished artwork edge to edge, a designed layout (words, logo and photo), or a plain card in
    /// the group's colour. Words stay 3/8 inch inside the page, clear of the 1/8 inch that is trimmed off.
    /// </summary>
    public static string Front(CardLook look, string restaurantName, string? offer)
    {
        if (look.Front == CardFronts.Picture && !string.IsNullOrWhiteSpace(look.PictureUrl))
            return Doc("img{position:absolute;left:0;top:0;width:6.25in;height:4.25in;object-fit:cover}", $"<img src=\"{H(look.PictureUrl)}\" alt=\"\">");

        var bg = SafeColor(look.Background);
        var fg = SafeColor(look.Text, "#ffffff");
        if (look.Front != CardFronts.Designed)
        {
            var enjoy = string.IsNullOrWhiteSpace(offer) ? "" : $"<p class=\"offer\">Please enjoy <strong>{H(offer)}</strong> on your next visit.</p>";
            return Doc($"body{{background:{bg};color:#fff}}"
                       + ".box{position:absolute;left:.45in;top:.45in;right:.45in;bottom:.45in;display:flex;flex-direction:column;justify-content:center}"
                       + "h1{font-size:26pt;margin:0 0 .12in}.thanks{font-size:15pt;margin:0 0 .25in}.offer{font-size:13pt;margin:0}",
                $"<div class=\"box\"><h1>{H(restaurantName)}</h1><p class=\"thanks\">Thank you for letting us know about your visit.</p>{enjoy}</div>");
        }

        var headline = string.IsNullOrWhiteSpace(look.Headline) ? DefaultHeadline : look.Headline.Trim();
        var words = $"<h1>{H(headline)}</h1>" + (string.IsNullOrWhiteSpace(look.Message) ? "" : $"<p class=\"msg\">{H(look.Message.Trim())}</p>");
        var logo = string.IsNullOrWhiteSpace(look.LogoUrl) ? "" : $"<img class=\"logo\" src=\"{H(look.LogoUrl)}\" alt=\"\">";
        var name = $"<p class=\"name\">{H(restaurantName)}</p>";
        var common = $"body{{background:{bg};color:{fg}}}h1{{margin:0;font-size:27pt;line-height:1.1}}.msg{{margin:.12in 0 0;font-size:12.5pt;line-height:1.3}}"
                     + ".name{margin:.16in 0 0;font-size:10pt;letter-spacing:.04em;text-transform:uppercase;opacity:.85}.logo{object-fit:contain}"
                     + ".photo{position:absolute;object-fit:cover}";
        var hasPhoto = !string.IsNullOrWhiteSpace(look.PhotoUrl);

        if (look.Layout == CardLayouts.Photo && hasPhoto)
            return Doc(common
                       + ".photo{left:0;top:0;width:6.25in;height:4.25in}"
                       + $".band{{position:absolute;left:0;right:0;bottom:0;height:1.75in;background:{bg};opacity:.92}}"
                       + ".in{position:absolute;left:.4in;right:.4in;bottom:.38in;height:1.2in;display:flex;align-items:center;gap:.25in}"
                       + ".logo{max-height:1in;max-width:1.4in;flex:none}h1{font-size:22pt}.msg{margin-top:.06in;font-size:11pt}.name{margin-top:.06in;font-size:8.5pt}",
                $"<img class=\"photo\" src=\"{H(look.PhotoUrl!)}\" alt=\"\"><div class=\"band\"></div><div class=\"in\">{logo}<div>{words}{name}</div></div>");

        if (look.Layout == CardLayouts.Split && hasPhoto)
            return Doc(common
                       + ".photo{left:0;top:0;width:2.9in;height:4.25in}"
                       + ".side{position:absolute;left:3.15in;top:.4in;right:.4in;bottom:.4in;display:flex;flex-direction:column;justify-content:center}"
                       + ".logo{max-height:.9in;max-width:1.8in;align-self:flex-start;margin:0 0 .18in}h1{font-size:22pt}.msg{font-size:11.5pt}",
                $"<img class=\"photo\" src=\"{H(look.PhotoUrl!)}\" alt=\"\"><div class=\"side\">{logo}{words}{name}</div>");

        return Doc(common
                   + ".mid{position:absolute;left:.5in;top:.4in;right:.5in;bottom:.4in;display:flex;flex-direction:column;justify-content:center;align-items:center;text-align:center}"
                   + ".logo{max-height:1.15in;max-width:2.8in;margin:0 0 .2in}",
            $"<div class=\"mid\">{logo}{words}{name}</div>");
    }

    /// <summary>The coupon side. PostGrid adds the guest's address, the return address and the postage on the right.</summary>
    public static string Back(GuestComment c, Restaurant r, int validityMonths, string? accent, string? backMessage = null)
    {
        var color = SafeColor(accent);
        string Day(DateOnly d) => d.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        var months = $"{validityMonths} month{(validityMonths == 1 ? "" : "s")}";
        var sb = new StringBuilder();
        sb.Append($"<!doctype html><html><head><meta charset=\"utf-8\"><style>{Page}");
        sb.Append(".coupon{position:absolute;left:.3in;top:.3in;width:3.4in;height:3.15in;font-size:9.5pt;line-height:1.3;overflow:hidden}");
        sb.Append($"h2{{font-size:13pt;margin:0 0 .06in;color:{color}}}.enjoy{{margin:0 0 .1in;font-size:10.5pt}}.note{{margin:0 0 .08in;font-size:9pt;font-style:italic;color:#333}}");
        sb.Append(".line{display:flex;gap:.08in;margin:.03in 0}.line span:first-child{width:1in;color:#555}.line span:last-child{flex:1;border-bottom:1px solid #999;font-weight:bold}");
        sb.Append(".legal{font-size:6.5pt;line-height:1.25;color:#444;margin:.1in 0 0}</style></head><body><div class=\"coupon\">");
        sb.Append($"<h2>{H(r.Name)}</h2>");
        if (!string.IsNullOrWhiteSpace(backMessage)) sb.Append($"<p class=\"note\">{H(backMessage.Trim())}</p>");
        sb.Append($"<p class=\"enjoy\">Please enjoy <strong>{H(string.IsNullOrWhiteSpace(c.CouponOffer) ? "a treat on us" : c.CouponOffer)}</strong> the next time you visit us.</p>");
        sb.Append($"<div class=\"line\"><span>Date issued</span><span>{Day(c.DateIssued)}</span></div>");
        sb.Append($"<div class=\"line\"><span>Good until</span><span>{Day(c.ExpiresOn)}</span></div>");
        sb.Append($"<div class=\"line\"><span>Authorized by</span><span>{H(c.AuthorizedBy ?? "")}</span></div>");
        sb.Append($"<p class=\"legal\">This coupon is good for {months} from the date of issue. Please present this card prior to ordering. One order per card. ");
        sb.Append("Cannot be exchanged for gift certificates or cash in whole or part and has no cash value. Not valid with other promotion or discounts. Valid only with authorized signature.</p>");
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static string H(string s) => WebUtility.HtmlEncode(s);
}

/// <summary>
/// Card designs: the investment group's, and each restaurant's own where it has one. A restaurant without its own
/// uses the group's; a group without one gets the plain card in its accent colour.
/// </summary>
public class CardDesigns(AppDbContext db, PostGridOptions options, IHttpContextAccessor http)
{
    public record Loaded(CardDesign Design, List<ImageRef> Images);
    public record ImageRef(string Kind, string Hash, string ContentType, int? WidthPx, int? HeightPx, long Bytes, DateTime UploadedAt, string UploadedBy);

    /// <summary>Every design in a group with its pictures' details (never the picture data).</summary>
    public async Task<List<Loaded>> ForGroupAsync(int groupId, CancellationToken ct = default)
    {
        var designs = await db.CardDesigns.AsNoTracking().Where(d => d.GroupId == groupId).ToListAsync(ct);
        var images = await db.CardImages.AsNoTracking().Where(i => i.GroupId == groupId)
            .Select(i => new { i.DesignId, Ref = new ImageRef(i.Kind, i.Hash, i.ContentType, i.WidthPx, i.HeightPx, (long)i.Data.Length, i.UploadedAt, i.UploadedBy) }).ToListAsync(ct);
        return designs.Select(d => new Loaded(d, images.Where(i => i.DesignId == d.Id).Select(i => i.Ref).ToList())).ToList();
    }

    /// <summary>
    /// The design a restaurant's cards use: its own, else the group's, else none. A restaurant's own design borrows
    /// any picture it has not uploaded itself (typically the logo) from the group's.
    /// </summary>
    public static Loaded? For(List<Loaded> all, int restaurantId)
    {
        var group = all.FirstOrDefault(l => l.Design.RestaurantId == null);
        var own = all.FirstOrDefault(l => l.Design.RestaurantId == restaurantId);
        return own is null ? group : own with { Images = WithGroupPictures(own.Images, group) };
    }

    public static List<ImageRef> WithGroupPictures(List<ImageRef> own, Loaded? group) =>
        group is null ? own : [.. own, .. group.Images.Where(g => own.All(o => o.Kind != g.Kind))];

    public CardLook Look(Loaded? l, int groupId, string? accent)
    {
        if (l is null) return CardLook.Plain(accent);
        var d = l.Design;
        string? Url(string kind) => l.Images.FirstOrDefault(i => i.Kind == kind) is { } i ? ImageUrl(groupId, i.Hash, i.ContentType, absolute: true) : null;
        return new CardLook(d.Front, d.Layout, d.Headline, d.Message, d.BackMessage,
            PostGridCardDesign.SafeColor(d.BackgroundColor, PostGridCardDesign.SafeColor(accent)), PostGridCardDesign.SafeColor(d.TextColor, "#ffffff"),
            Url(CardImageKinds.Picture), Url(CardImageKinds.Logo), Url(CardImageKinds.Photo));
    }

    /// <summary>A picture's address: on this site for the browser, or in full for the mail house, which downloads it when it prints.</summary>
    public string ImageUrl(int groupId, string hash, string contentType, bool absolute)
    {
        var path = $"/card-art/{groupId}/{hash}{(contentType == "image/png" ? ".png" : ".jpg")}";
        if (!absolute) return path;
        if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl)) return options.PublicBaseUrl.TrimEnd('/') + path;
        var req = http.HttpContext?.Request;
        if (req is null) return path;
        var local = req.Host.Host is "localhost" or "127.0.0.1" or "[::1]";
        return $"{(local ? req.Scheme : "https")}://{req.Host}{path}";
    }
}

/// <summary>Reads the size and type of an uploaded PNG or JPEG without an imaging library.</summary>
public static class ImageInfo
{
    public static (string ContentType, int Width, int Height)? Read(byte[] d)
    {
        if (d.Length > 24 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47)
            return ("image/png", BigEndian(d, 16), BigEndian(d, 20));
        if (d.Length > 4 && d[0] == 0xFF && d[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < d.Length)
            {
                if (d[i] != 0xFF) { i++; continue; }
                var marker = d[i + 1];
                if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
                var length = (d[i + 2] << 8) | d[i + 3];
                // Start-of-frame markers carry the height then the width.
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    return ("image/jpeg", (d[i + 7] << 8) | d[i + 8], (d[i + 5] << 8) | d[i + 6]);
                i += 2 + length;
            }
            return ("image/jpeg", 0, 0);
        }
        return null;
    }

    private static int BigEndian(byte[] d, int at) => (d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3];
}

// ---- Address checking through PostGrid ----------------------------------------------------------------------------

/// <param name="UseFirst">Ask PostGrid before USPS; otherwise PostGrid is only asked when USPS cannot answer.</param>
public record PostGridAddressAccount(string ApiKey, bool UseFirst, string Source, DateTime? SavedAt, string? SavedBy);

/// <summary>
/// The site's PostGrid address verification key, entered by the System Administrator on Connected Systems. Encrypted
/// with Data Protection before it is stored; never returned to a browser or logged. PostGrid__AddressApiKey is a fallback.
/// </summary>
public class PostGridAddressAccountStore(IServiceScopeFactory scopes, IDataProtectionProvider protection, PostGridOptions options, ILogger<PostGridAddressAccountStore> log)
{
    private const string SettingKey = "postgrid.addresses";
    private readonly IDataProtector _protector = protection.CreateProtector("RestaurantFeedback.SiteSecret.PostGridAddresses.v1");
    private volatile PostGridAddressAccount? _current;
    private int _version;

    private record Saved(string ApiKey, bool UseFirst);

    public PostGridAddressAccount? Current => _current;
    public int Version => _version;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        PostGridAddressAccount? found = null;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.SiteSettings.FindAsync([SettingKey], ct);
            if (row is not null)
            {
                var saved = JsonSerializer.Deserialize<Saved>(_protector.Unprotect(row.Value))!;
                found = new PostGridAddressAccount(saved.ApiKey, saved.UseFirst, "Saved in the site", DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc), row.UpdatedBy);
            }
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            log.LogError(ex, "The saved PostGrid address key can no longer be read and must be entered again");
        }
        if (found is null && !string.IsNullOrWhiteSpace(options.AddressApiKey))
            found = new PostGridAddressAccount(options.AddressApiKey, false, "App settings", null, null);
        _current = found;
        Interlocked.Increment(ref _version);
    }

    public async Task SaveAsync(string apiKey, bool useFirst, string by, CancellationToken ct = default)
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.SiteSettings.FindAsync([SettingKey], ct);
            if (row is null) db.SiteSettings.Add(row = new SiteSetting { Key = SettingKey });
            row.Value = _protector.Protect(JsonSerializer.Serialize(new Saved(apiKey.Trim(), useFirst)));
            row.UpdatedAt = DateTime.UtcNow;
            row.UpdatedBy = by;
            await db.SaveChangesAsync(ct);
        }
        await LoadAsync(ct);
    }

    /// <summary>Changes only which service is asked first, keeping the saved key.</summary>
    public async Task<bool> SetUseFirstAsync(bool useFirst, string by, CancellationToken ct = default)
    {
        if (_current is not { Source: "Saved in the site" } a) return false;
        await SaveAsync(a.ApiKey, useFirst, by, ct);
        return true;
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
/// PostGrid's US address verification (POST /v1/addver/verifications), which checks against USPS data. Answers are
/// remembered for a day, and after a refusal (bad key, out of lookups, busy) PostGrid is left alone for a while.
/// </summary>
public class PostGridAddressVerifier(HttpClient http, PostGridAddressAccountStore account, PostGridOptions options, IMemoryCache cache, ILogger<PostGridAddressVerifier> log) : IAddressVerifier
{
    private static DateTime _pausedUntil = DateTime.MinValue;
    private static int _accountVersion = -1;
    private static string? _problem;

    private PostGridAddressAccount? Account
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

    private string BaseUrl => options.AddressBaseUrl.TrimEnd('/');
    public bool Configured => Account is not null;
    public string Description => "PostGrid address verification";

    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct = default)
    {
        if (Account is not { } a) return (false, "No PostGrid address verification key has been entered.");
        _pausedUntil = DateTime.MinValue;
        _problem = null;
        string? allowance = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/addver/");
            req.Headers.Add("x-api-key", a.ApiKey);
            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && !body.Contains("more lookups", StringComparison.OrdinalIgnoreCase))
                return (false, $"PostGrid did not accept this key ({(int)res.StatusCode}{PostGridMailer.Said(body)}). Use the key from PostGrid's Address Verification dashboard (Developers), not the Print & Mail key.");
            if (res.IsSuccessStatusCode) allowance = Allowance(body);

            var r = await CheckAsync(new AddressInput("475 L'Enfant Plaza SW", "Washington", "DC", "20260"), ct, remember: false);
            if (AddressStatuses.IsResult(r.Status))
                return (true, $"PostGrid accepted the key and answered an address check. Address checking through PostGrid is working.{(allowance is null ? "" : " " + allowance)}");
            return (false, _problem ?? r.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (false, "PostGrid could not be reached. Try again in a minute.");
        }
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

        var cacheKey = $"postgrid:{street}|{city}|{state}|{zip5}".ToUpperInvariant();
        if (remember && cache.TryGetValue(cacheKey, out AddressCheck? known) && known is not null) return known;
        if (DateTime.UtcNow < _pausedUntil) return new(AddressStatuses.Unavailable, "Address checking is paused for a few minutes. You can still save the card.");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/addver/verifications?includeDetails=true&properCase=true")
            {
                Content = JsonContent.Create(new { address = new { line1 = street, city, provinceOrState = state, postalOrZip = zip5 ?? "", country = "us" } })
            };
            req.Headers.Add("x-api-key", a.ApiKey);
            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                var said = PostGridMailer.Said(body);
                if (body.Contains("more lookups", StringComparison.OrdinalIgnoreCase))
                {
                    _problem = $"PostGrid says this account has no address lookups left ({(int)res.StatusCode}{said}). Add lookups or a plan in PostGrid's Address Verification dashboard.";
                    _pausedUntil = DateTime.UtcNow.AddMinutes(30);
                }
                else if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    _problem = $"PostGrid did not accept the address verification key ({(int)res.StatusCode}{said}). Check the key on Connected Systems.";
                    _pausedUntil = DateTime.UtcNow.AddMinutes(10);
                }
                else if (res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                {
                    _problem = $"PostGrid is busy ({(int)res.StatusCode}).";
                    _pausedUntil = DateTime.UtcNow.AddMinutes(2);
                }
                else _problem = $"PostGrid answered the address check with an error ({(int)res.StatusCode}{said}).";
                log.LogWarning("PostGrid address check answered {Status}{Said}", (int)res.StatusCode, said);
                return new(AddressStatuses.Unavailable, "The address could not be checked just now. You can still save the card.");
            }
            var result = Read(JsonDocument.Parse(body).RootElement, input);
            if (remember && AddressStatuses.IsResult(result.Status)) cache.Set(cacheKey, result, TimeSpan.FromHours(24));
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning(ex, "PostGrid address check failed");
            return new(AddressStatuses.Unavailable, "The address service could not be reached. You can still save the card.");
        }
    }

    /// <summary>Turns PostGrid's answer ({status, message, data:{status: verified|corrected|failed, errors, ...}}) into a status.</summary>
    public static AddressCheck Read(JsonElement root, AddressInput typed)
    {
        var data = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : root;
        var status = Text(data, "status")?.ToLowerInvariant();
        var errors = Errors(data);
        var line = string.Join(' ', new[] { Text(data, "line1"), Text(data, "line2") }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var zip = Text(data, "postalOrZip") ?? "";
        if (zip.Length == 5 && Text(data, "zipPlus4") is { Length: 4 } plus4) zip += "-" + plus4;
        var suggested = line.Length == 0 ? null : new AddressSuggestion(line, Text(data, "city") ?? "", (Text(data, "provinceOrState") ?? "").ToUpperInvariant(), zip);
        var shown = suggested is null ? "" : $"{suggested.StreetAddress}, {suggested.City}, {suggested.State} {suggested.Zip}";
        var needsUnit = errors.Any(e => Regex.IsMatch(e, @"\b(unit|suite|apartment|apt|secondary)\b", RegexOptions.IgnoreCase));

        return status switch
        {
            "verified" when suggested is not null && SameAsTyped(suggested, typed) => new(AddressStatuses.Verified, "The post office delivers to this address.", suggested),
            "verified" or "corrected" when suggested is not null => needsUnit
                ? new(AddressStatuses.NeedsUnit, $"The post office has this building ({shown}) but needs an apartment, suite or unit number to deliver.", suggested)
                : new(AddressStatuses.Corrected, $"The post office writes this address as {shown}.", suggested),
            "failed" when needsUnit => new(AddressStatuses.NeedsUnit, $"The post office needs an apartment, suite or unit number for this address{(shown.Length > 0 ? $" ({shown})" : "")}.", suggested),
            "failed" => new(AddressStatuses.NotFound, "The post office does not recognise this address. Check the street number and spelling."
                                                    + (errors.FirstOrDefault() is { } why ? $" ({why})" : "")),
            _ => new(AddressStatuses.Unavailable, "The address could not be checked just now. You can still save the card.")
        };
    }

    /// <summary>"N of M free lookups used" from GET /v1/addver/, when PostGrid reports it.</summary>
    private static string? Allowance(string body)
    {
        try
        {
            var root = JsonDocument.Parse(body).RootElement;
            var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : root;
            long? Num(string name) => data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
            var used = Num("used");
            var free = Num("freeLimit");
            var subscribed = data.TryGetProperty("subscribed", out var s) && s.ValueKind == JsonValueKind.True;
            if (used is null) return null;
            return subscribed ? $"{used:N0} lookups used this period on a paid plan." : free is null ? $"{used:N0} lookups used." : $"{used:N0} of {free:N0} free lookups used.";
        }
        catch (JsonException) { return null; }
    }

    private static List<string> Errors(JsonElement data)
    {
        var list = new List<string>();
        if (!data.TryGetProperty("errors", out var e)) return list;
        void Add(JsonElement x)
        {
            if (x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString())) list.Add(x.GetString()!);
            else if (x.ValueKind == JsonValueKind.Array) foreach (var i in x.EnumerateArray()) Add(i);
            else if (x.ValueKind == JsonValueKind.Object) foreach (var p in x.EnumerateObject()) Add(p.Value);
        }
        Add(e);
        return list;
    }

    private static bool SameAsTyped(AddressSuggestion s, AddressInput typed) =>
        Canon(s.StreetAddress) == Canon(typed.Street)
        && (string.IsNullOrWhiteSpace(typed.City) || Canon(s.City) == Canon(typed.City))
        && (Zip5(typed.Zip) is null || Zip5(s.Zip) == Zip5(typed.Zip));

    private static string Canon(string? s) => Regex.Replace(Regex.Replace((s ?? "").ToUpperInvariant(), @"[^A-Z0-9 ]", ""), @"\s+", " ").Trim();
    private static string Clean(string? s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();
    private static string? Zip5(string? zip) => Regex.Match(zip ?? "", @"\d{5}") is { Success: true } m ? m.Value : null;
    private static string? Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// The site's address checking: USPS and PostGrid, in the order the System Administrator chose. When the first cannot
/// answer (not set up, refused, busy or down) the second is asked, so one outage never switches checking off.
/// </summary>
public class AddressCheckers(UspsAddressVerifier usps, PostGridAddressVerifier postGrid, PostGridAddressAccountStore postGridAccount) : IAddressVerifier
{
    public UspsAddressVerifier Usps => usps;
    public PostGridAddressVerifier PostGrid => postGrid;

    private IAddressVerifier[] Order => postGridAccount.Current?.UseFirst == true ? [postGrid, usps] : [usps, postGrid];

    public bool Configured => usps.Configured || postGrid.Configured;

    public string Description
    {
        get
        {
            var on = Order.Where(v => v.Configured).Select(v => v.Description).ToList();
            return on.Count switch { 0 => "Not set up", 1 => on[0], _ => $"{on[0]}, then {on[1]} when it cannot answer" };
        }
    }

    public async Task<AddressCheck> CheckAsync(AddressInput input, CancellationToken ct = default)
    {
        AddressCheck? last = null;
        foreach (var v in Order.Where(v => v.Configured))
        {
            var r = await v.CheckAsync(input, ct);
            if (r.Status != AddressStatuses.Unavailable) return r;
            last = r;
        }
        return last ?? new(AddressStatuses.Unavailable, "Address checking is not set up.");
    }

    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct = default)
    {
        var r = await CheckAsync(new AddressInput("475 L'Enfant Plaza SW", "Washington", "DC", "20260"), ct);
        return AddressStatuses.IsResult(r.Status) ? (true, "Address checking is working.") : (false, r.Message);
    }
}
