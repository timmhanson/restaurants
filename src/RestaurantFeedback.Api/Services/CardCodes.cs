using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using QRCoder;
using RestaurantFeedback.Api.Data;

namespace RestaurantFeedback.Api.Services;

/// <summary>
/// The code printed on every coupon card as a QR code, which leads back to that card's record.
///
/// Each card has its own random GUID. The printed code is that GUID followed by a signature: the first ten bytes
/// of an HMAC-SHA256 of the GUID, keyed with a secret only this site holds. So a code cannot be made up, guessed or
/// altered: change any character and the signature no longer matches, and nobody outside the site can produce a
/// matching one. The code itself reveals nothing and opens nothing: it only leads to a page that makes the person
/// sign in, and then shows the card as the site has it, so a card whose printed words were changed is caught too.
///
/// The signing secret is made once, kept in the SiteSettings table encrypted with Data Protection, and never leaves
/// the server. If it were ever lost, cards printed before then would no longer check as genuine.
/// </summary>
public class CardCodes(IServiceScopeFactory scopes, IDataProtectionProvider protection, ILogger<CardCodes> log)
{
    private const string SettingKey = "card.codekey";
    private const int SignatureBytes = 10;
    private static readonly byte[] Purpose = Encoding.ASCII.GetBytes("rf-card-v1:");
    private readonly IDataProtector _protector = protection.CreateProtector("RestaurantFeedback.SiteSecret.CardCodes.v1");
    private volatile byte[]? _key;

    /// <summary>Reads the signing secret, making and saving one the first time.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.SiteSettings.FindAsync([SettingKey], ct);
        if (row is not null)
        {
            try
            {
                _key = Convert.FromBase64String(_protector.Unprotect(row.Value));
                return;
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // Without the old secret, cards already printed no longer check as genuine. Say so loudly, then
                // carry on with a new secret so that cards printed from now on work.
                log.LogError(ex, "The secret that signs coupon card codes can no longer be read. A new one was made: QR codes on cards printed before now will not check as genuine");
                db.SiteSettings.Remove(row);
                await db.SaveChangesAsync(ct);
            }
        }
        var key = RandomNumberGenerator.GetBytes(32);
        db.SiteSettings.Add(new Models.SiteSetting { Key = SettingKey, Value = _protector.Protect(Convert.ToBase64String(key)), UpdatedAt = DateTime.UtcNow, UpdatedBy = "Site" });
        await db.SaveChangesAsync(ct);
        _key = key;
    }

    private byte[] Key => _key ?? throw new InvalidOperationException("The card code secret has not been loaded.");

    private byte[] Sign(byte[] id)
    {
        using var hmac = new HMACSHA256(Key);
        return hmac.ComputeHash([.. Purpose, .. id])[..SignatureBytes];
    }

    /// <summary>The code printed for a card: its GUID and signature, 35 letters and digits safe in a web address.</summary>
    public string CodeFor(Guid id)
    {
        var bytes = id.ToByteArray();
        return WebEncoders.Base64UrlEncode([.. bytes, .. Sign(bytes)]);
    }

    /// <summary>The card a code names, or null when the code is malformed or its signature does not match.</summary>
    public Guid? Read(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 64) return null;
        byte[] raw;
        try { raw = WebEncoders.Base64UrlDecode(code.Trim()); }
        catch (FormatException) { return null; }
        if (raw.Length != 16 + SignatureBytes) return null;
        var id = raw[..16];
        return CryptographicOperations.FixedTimeEquals(raw[16..], Sign(id)) ? new Guid(id) : null;
    }

    /// <summary>The address the QR code holds: this site's verify page for that card.</summary>
    public string Url(Guid id, string siteOrigin) => $"{siteOrigin.TrimEnd('/')}/v/{CodeFor(id)}";

    /// <summary>
    /// A QR code as a picture that can be placed in a page or a printed card (an SVG in a data address, so nothing
    /// has to be downloaded to show it). Medium error correction, so a scuffed or creased card still scans.
    /// </summary>
    public static string QrDataUri(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var rows = data.ModuleMatrix;
        var n = rows.Count;
        var path = new StringBuilder();
        for (var y = 0; y < n; y++)
        {
            var row = rows[y];
            for (var x = 0; x < n; x++)
            {
                if (!row[x]) continue;
                // Join a run of dark squares into one rectangle to keep the picture small.
                var start = x;
                while (x + 1 < n && row[x + 1]) x++;
                path.Append($"M{start} {y}h{x - start + 1}v1h-{x - start + 1}z");
            }
        }
        var svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {n} {n}\" shape-rendering=\"crispEdges\"><rect width=\"{n}\" height=\"{n}\" fill=\"#fff\"/><path d=\"{path}\" fill=\"#000\"/></svg>";
        return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }
}

/// <summary>
/// The address people use to reach this site, for links that leave it (the QR code on a card, the way back from the
/// payment service): the one set on Billing, else the local dev server's when developing, else the configured
/// public address, else the address of the request.
/// </summary>
public class SiteAddress(IHttpContextAccessor http, SiteMailing site, PostGridOptions options)
{
    public string Origin()
    {
        if (!string.IsNullOrWhiteSpace(site.Billing.SiteUrl)) return site.Billing.SiteUrl;
        var req = http.HttpContext?.Request;
        var local = req is not null && req.Host.Host is "localhost" or "127.0.0.1" or "[::1]";
        // Locally the page may be served by the dev server on another port, which the browser names in Origin.
        if (local && Uri.TryCreate(req!.Headers.Origin.ToString(), UriKind.Absolute, out var o) && o.Host is "localhost" or "127.0.0.1") return o.GetLeftPart(UriPartial.Authority);
        if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl)) return options.PublicBaseUrl.TrimEnd('/');
        return req is null ? "" : $"{(local ? req.Scheme : "https")}://{req.Host}";
    }
}
