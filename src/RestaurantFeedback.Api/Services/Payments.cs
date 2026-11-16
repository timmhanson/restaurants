using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace RestaurantFeedback.Api.Services;

/// <summary>
/// Taking payment for mailing bills through Stripe (stripe.com), on the site's own Stripe account. Investment groups
/// save a bank account or a card on Stripe's own page, so no card or bank number ever reaches this site; the site
/// keeps only Stripe's references and a label such as "Visa credit card ending 4242". Settings section "Payments".
/// </summary>
public class PaymentOptions
{
    public string BaseUrl { get; set; } = "https://api.stripe.com";
    /// <summary>Optional Stripe secret key (App Service setting Payments__SecretKey), used only when none is saved on Connected Systems.</summary>
    public string? SecretKey { get; set; }
    /// <summary>Local development only: use <see cref="SamplePayments"/> instead of Stripe.</summary>
    public bool Sample { get; set; }
}

public static class PayKinds
{
    public const string Card = "card", Bank = "bank";
}

public static class PayStates
{
    public const string Succeeded = "succeeded", Processing = "processing", Failed = "failed";
}

public record PaymentSetupLink(string SessionId, string Url);
/// <param name="Kind">card or bank.</param>
/// <param name="Funding">For a card: credit, debit or prepaid.</param>
/// <param name="Label">Fit to show, such as "Visa credit card ending 4242". Never a full number.</param>
public record SavedMethod(string MethodId, string Kind, string? Funding, string Label, string? MandateId);
/// <param name="Status">succeeded, processing (a bank payment takes a few days) or failed.</param>
public record PaymentState(string Id, string Status, string? Error);
public record ChargeRequest(string CustomerId, string MethodId, string Kind, string? MandateId, int AmountCents, string Description, Dictionary<string, string> Metadata);

/// <summary>A problem with the payment service, in words fit to show the person paying.</summary>
/// <param name="Answered">The service gave a definite answer (so trying again needs a new idempotency key); false when it could not be reached.</param>
public class PaymentException(string message, bool answered) : Exception(message)
{
    public bool Answered { get; } = answered;
}

public interface IPaymentProvider
{
    string Description { get; }
    Task<(bool Ok, string Message)> TestKeyAsync(string key, CancellationToken ct = default);
    Task<string> CreateCustomerAsync(string key, string name, string? email, int groupId, CancellationToken ct = default);
    /// <summary>A page on the payment service where the group saves a bank account or card for later bills.</summary>
    Task<PaymentSetupLink> StartSetupAsync(string key, string customerId, string returnUrl, string cancelUrl, CancellationToken ct = default);
    /// <summary>What was saved on that page, or null when the person left without finishing.</summary>
    Task<SavedMethod?> FinishSetupAsync(string key, string sessionId, string customerId, CancellationToken ct = default);
    /// <param name="idempotencyKey">The same key returns the first answer instead of charging again.</param>
    Task<PaymentState> ChargeAsync(string key, ChargeRequest request, string idempotencyKey, CancellationToken ct = default);
    Task<PaymentState> GetPaymentAsync(string key, string id, CancellationToken ct = default);
    Task RemoveMethodAsync(string key, string methodId, CancellationToken ct = default);
}

public class StripePayments : IPaymentProvider
{
    private readonly HttpClient _http;

    public StripePayments(HttpClient http, PaymentOptions options)
    {
        _http = http;
        _http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public string Description => "Stripe";

    public static bool IsKeyForTest(string key) => key.Contains("_test_", StringComparison.OrdinalIgnoreCase);

    public async Task<(bool Ok, string Message)> TestKeyAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await SendAsync(HttpMethod.Get, "v1/balance", key, null, null, ct);
            return (true, IsKeyForTest(key)
                ? "Stripe accepted the test key. Payments made with it use Stripe's test cards and bank accounts; no money moves."
                : "Stripe accepted the live key. Payments made with it move real money into this Stripe account.");
        }
        catch (PaymentException ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<string> CreateCustomerAsync(string key, string name, string? email, int groupId, CancellationToken ct = default)
    {
        var form = new List<KeyValuePair<string, string>> { new("name", name), new("metadata[groupId]", groupId.ToString()) };
        if (!string.IsNullOrWhiteSpace(email)) form.Add(new("email", email));
        var e = await SendAsync(HttpMethod.Post, "v1/customers", key, form, null, ct);
        return Text(e, "id") ?? throw new PaymentException("Stripe did not say which customer it created.", answered: true);
    }

    public async Task<PaymentSetupLink> StartSetupAsync(string key, string customerId, string returnUrl, string cancelUrl, CancellationToken ct = default)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("mode", "setup"),
            new("customer", customerId),
            new("payment_method_types[0]", "us_bank_account"),
            new("payment_method_types[1]", "card"),
            // Stripe fills in the session's id, so the site can read back what was saved.
            new("success_url", returnUrl + (returnUrl.Contains('?') ? "&" : "?") + "session_id={CHECKOUT_SESSION_ID}"),
            new("cancel_url", cancelUrl)
        };
        var e = await SendAsync(HttpMethod.Post, "v1/checkout/sessions", key, form, null, ct);
        var id = Text(e, "id");
        var url = Text(e, "url");
        if (id is null || url is null) throw new PaymentException("Stripe did not return its payment page.", answered: true);
        return new PaymentSetupLink(id, url);
    }

    public async Task<SavedMethod?> FinishSetupAsync(string key, string sessionId, string customerId, CancellationToken ct = default)
    {
        var e = await SendAsync(HttpMethod.Get, $"v1/checkout/sessions/{Uri.EscapeDataString(sessionId)}?expand[]=setup_intent.payment_method", key, null, null, ct);
        // The page must belong to this group's customer, or one group could claim another's saved method.
        if (Text(e, "customer") != customerId) throw new PaymentException("That payment page belongs to a different account.", answered: true);
        if (Text(e, "status") != "complete") return null;
        if (!e.TryGetProperty("setup_intent", out var intent) || intent.ValueKind != JsonValueKind.Object) return null;
        if (Text(intent, "status") != "succeeded")
            throw new PaymentException("The bank account is not confirmed yet. Stripe emails instructions to confirm it (two small deposits); when that is done, add it here again.", answered: true);
        if (!intent.TryGetProperty("payment_method", out var pm) || pm.ValueKind != JsonValueKind.Object) return null;
        return Read(pm, Text(intent, "mandate"));
    }

    public static SavedMethod? Read(JsonElement pm, string? mandate)
    {
        var id = Text(pm, "id");
        if (id is null) return null;
        if (Text(pm, "type") == "card" && pm.TryGetProperty("card", out var card))
        {
            var brand = Text(card, "brand") is { Length: > 0 } b ? char.ToUpperInvariant(b[0]) + b[1..] : "Card";
            var funding = Text(card, "funding");
            var kind = funding is "credit" or "debit" or "prepaid" ? $"{funding} card" : "card";
            return new SavedMethod(id, PayKinds.Card, funding, $"{brand} {kind} ending {Text(card, "last4")}", null);
        }
        if (Text(pm, "type") == "us_bank_account" && pm.TryGetProperty("us_bank_account", out var bank))
            return new SavedMethod(id, PayKinds.Bank, null, $"{Text(bank, "bank_name") ?? "Bank"} account ending {Text(bank, "last4")}", mandate);
        return null;
    }

    public async Task<PaymentState> ChargeAsync(string key, ChargeRequest r, string idempotencyKey, CancellationToken ct = default)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("amount", r.AmountCents.ToString()),
            new("currency", "usd"),
            new("customer", r.CustomerId),
            new("payment_method", r.MethodId),
            new("payment_method_types[0]", r.Kind == PayKinds.Bank ? "us_bank_account" : "card"),
            new("confirm", "true"),
            new("off_session", "true"),
            new("description", r.Description)
        };
        if (r.Kind == PayKinds.Bank && !string.IsNullOrWhiteSpace(r.MandateId)) form.Add(new("mandate", r.MandateId));
        foreach (var (k, v) in r.Metadata) form.Add(new($"metadata[{k}]", v));
        return State(await SendAsync(HttpMethod.Post, "v1/payment_intents", key, form, idempotencyKey, ct, declineIsAnAnswer: true));
    }

    public async Task<PaymentState> GetPaymentAsync(string key, string id, CancellationToken ct = default) =>
        State(await SendAsync(HttpMethod.Get, $"v1/payment_intents/{Uri.EscapeDataString(id)}", key, null, null, ct));

    public async Task RemoveMethodAsync(string key, string methodId, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Post, $"v1/payment_methods/{Uri.EscapeDataString(methodId)}/detach", key, [], null, ct);

    /// <summary>Stripe's payment statuses, reduced to the three the site acts on.</summary>
    public static PaymentState State(JsonElement e)
    {
        // A declined card comes back as an error that carries the payment inside it.
        if (e.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var inner = error.TryGetProperty("payment_intent", out var pi) && pi.ValueKind == JsonValueKind.Object ? Text(pi, "id") : null;
            return new PaymentState(inner ?? "", PayStates.Failed, Text(error, "message") ?? "The payment was declined.");
        }
        var id = Text(e, "id") ?? throw new PaymentException("Stripe did not say which payment it made. Check the Stripe dashboard before trying again.", answered: true);
        var why = e.TryGetProperty("last_payment_error", out var last) && last.ValueKind == JsonValueKind.Object ? Text(last, "message") : null;
        return Text(e, "status") switch
        {
            "succeeded" => new(id, PayStates.Succeeded, null),
            "processing" => new(id, PayStates.Processing, null),
            "requires_action" => new(id, PayStates.Failed, "The bank or card needs the account holder to approve this payment. Save the payment method again, or pay another way."),
            _ => new(id, PayStates.Failed, why ?? "The payment did not go through.")
        };
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, string key, List<KeyValuePair<string, string>>? form, string? idempotencyKey,
        CancellationToken ct, bool declineIsAnAnswer = false)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (form is not null) req.Content = new FormUrlEncodedContent(form);
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        HttpResponseMessage res;
        try { res = await _http.SendAsync(req, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new PaymentException("The payment service could not be reached. Nothing was charged that this site knows of; try again in a minute.", answered: false);
        }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            JsonElement body;
            try { body = JsonDocument.Parse(text).RootElement.Clone(); }
            catch (JsonException) { throw new PaymentException($"The payment service gave an unexpected answer ({(int)res.StatusCode}).", answered: (int)res.StatusCode < 500); }
            if (res.IsSuccessStatusCode) return body;
            if (declineIsAnAnswer && res.StatusCode == HttpStatusCode.PaymentRequired) return body;
            var said = body.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? Text(e, "message") : null;
            if (res.StatusCode is HttpStatusCode.Unauthorized)
                throw new PaymentException("Stripe did not accept the site's key. The System Administrator can check it on Connected Systems.", answered: true);
            if ((int)res.StatusCode >= 500)
                throw new PaymentException($"The payment service had a problem ({(int)res.StatusCode}). Try again in a few minutes.", answered: false);
            throw new PaymentException($"The payment service refused this ({(int)res.StatusCode}{(said is null ? "" : ": " + said)}).", answered: true);
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Local development only (Payments:Sample = true): pretends to save a card and take payments, so billing can be
/// tried without a Stripe account. Nothing is sent anywhere and no money moves.
/// </summary>
public class SamplePayments : IPaymentProvider
{
    private int _count;
    public string Description => "Sample payments (not Stripe)";
    public Task<(bool Ok, string Message)> TestKeyAsync(string key, CancellationToken ct = default) =>
        Task.FromResult((true, "Sample payments accepted the key (nothing is sent anywhere on this computer)."));
    public Task<string> CreateCustomerAsync(string key, string name, string? email, int groupId, CancellationToken ct = default) =>
        Task.FromResult($"cus_sample{groupId}");
    public Task<PaymentSetupLink> StartSetupAsync(string key, string customerId, string returnUrl, string cancelUrl, CancellationToken ct = default)
    {
        var id = $"cs_sample{Interlocked.Increment(ref _count)}";
        // There is no payment page on this computer, so go straight back as if a card had been saved.
        return Task.FromResult(new PaymentSetupLink(id, returnUrl + (returnUrl.Contains('?') ? "&" : "?") + "session_id=" + id));
    }
    public Task<SavedMethod?> FinishSetupAsync(string key, string sessionId, string customerId, CancellationToken ct = default) =>
        Task.FromResult<SavedMethod?>(new SavedMethod("pm_sample", PayKinds.Card, "credit", "Sample credit card ending 4242", null));
    public Task<PaymentState> ChargeAsync(string key, ChargeRequest request, string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(new PaymentState($"pi_sample_{idempotencyKey}", PayStates.Succeeded, null));
    public Task<PaymentState> GetPaymentAsync(string key, string id, CancellationToken ct = default) =>
        Task.FromResult(new PaymentState(id, PayStates.Succeeded, null));
    public Task RemoveMethodAsync(string key, string methodId, CancellationToken ct = default) => Task.CompletedTask;
}

/// <param name="Source">"Saved in the site" or "App settings".</param>
public record PaymentAccount(string SecretKey, string Source, DateTime? SavedAt, string? SavedBy)
{
    public bool Test => StripePayments.IsKeyForTest(SecretKey);
}

/// <summary>
/// The site's Stripe secret key, entered by the System Administrator on Connected Systems. Encrypted with Data
/// Protection before it is stored; never returned to a browser, logged or audited beyond its last four characters.
/// </summary>
public class PaymentAccountStore(IServiceScopeFactory scopes, IDataProtectionProvider protection, PaymentOptions options, ILogger<PaymentAccountStore> log)
{
    private const string SettingKey = "stripe.account";
    private readonly IDataProtector _protector = protection.CreateProtector("RestaurantFeedback.SiteSecret.Stripe.v1");
    private volatile PaymentAccount? _current;

    private record Saved(string SecretKey);

    public PaymentAccount? Current => _current;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        PaymentAccount? found = null;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
            var row = await db.SiteSettings.FindAsync([SettingKey], ct);
            if (row is not null)
            {
                var saved = JsonSerializer.Deserialize<Saved>(_protector.Unprotect(row.Value))!;
                found = new PaymentAccount(saved.SecretKey, "Saved in the site", DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc), row.UpdatedBy);
            }
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            log.LogError(ex, "The saved Stripe key can no longer be read and must be entered again");
        }
        if (found is null && !string.IsNullOrWhiteSpace(options.SecretKey))
            found = new PaymentAccount(options.SecretKey.Trim(), "App settings", null, null);
        _current = found;
    }

    public async Task SaveAsync(string secretKey, string by, CancellationToken ct = default)
    {
        await SiteSettingRows.SetAsync(scopes, SettingKey, _protector.Protect(JsonSerializer.Serialize(new Saved(secretKey.Trim()))), by, ct);
        await LoadAsync(ct);
    }

    public async Task RemoveAsync(CancellationToken ct = default)
    {
        await SiteSettingRows.RemoveAsync(scopes, SettingKey, ct);
        await LoadAsync(ct);
    }
}
