using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using RestaurantFeedback.Api.Auth;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Text.RegularExpressions;

namespace RestaurantFeedback.Api.Tests;

/// <summary>Boots the API against a throw-away SQLite file and local settings folder, with known seed accounts.</summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rf-tests-" + Guid.NewGuid().ToString("N"));

    public const string SuperAdminUser = "admin@test.local";
    public const string SuperAdminPassword = "SuperAdmin!123";
    public const string OwnerUser = "owner";
    public const string OwnerPassword = "OwnerPass!123";

    public ApiFactory()
    {
        Directory.CreateDirectory(_dir);
        // Program.cs reads these before the host is built, so WebApplicationFactory's deferred
        // configuration would arrive too late; environment variables are picked up by the default config.
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings__Default"] = $"Data Source={Path.Combine(_dir, "test.db")}",
            ["Jwt__Key"] = "unit-test-signing-key-0123456789-0123456789",
            ["Storage__LocalFolder"] = Path.Combine(_dir, "settings"),
            ["Storage__ConnectionString"] = "",
            ["Storage__AccountUrl"] = "",
            ["Seed__SuperAdminUsername"] = SuperAdminUser,
            ["Seed__SuperAdminPassword"] = SuperAdminPassword,
            ["Seed__OwnerUsername"] = OwnerUser,
            ["Seed__OwnerPassword"] = OwnerPassword,
            ["Seed__GroupName"] = "Test Group",
            ["Seed__RestaurantName"] = "Test Culver's",
            // The hourly billing round would create and send bills behind the tests' backs.
            ["PostGrid__BillingSchedule"] = "false",
        };
        foreach (var (k, v) in values) Environment.SetEnvironmentVariable(k, v);
    }

    public CapturingEmailSender Emails { get; } = new();
    public FakePostageProvider Postage { get; } = new();
    public FakeAddressVerifier Addresses { get; } = new();
    public FakeAddressSuggester Suggestions { get; } = new();
    public FakePostGridMailer Mail { get; } = new();
    public FakePayments Payments { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IEmailSender>(Emails);
            services.AddSingleton<IPostageProvider>(Postage);
            services.AddSingleton<IAddressVerifier>(Addresses);
            services.AddSingleton<IAddressSuggester>(Suggestions);
            services.AddSingleton<IPostGridMailer>(Mail);
            services.AddSingleton<IPaymentProvider>(Payments);
        });
    }

    /// <summary>Every client behaves like the current site, which always sends its session id.</summary>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add("X-Session-Id", "test-session");
    }

    /// <summary>Runs the two-step flow: request a code, read it from the captured email, submit the change.</summary>
    public async Task<HttpResponseMessage> ChangePasswordAsync(HttpClient client, string current, string next)
    {
        var req = await client.PostAsync("/api/auth/change-password/request", null);
        req.EnsureSuccessStatusCode();
        var code = Emails.LastCode ?? throw new InvalidOperationException("No code captured");
        return await client.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest(current, next, code));
    }

    public async Task<HttpClient> LoginAsync(string username, string password, int? groupId = null)
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, password));
        res.EnsureSuccessStatusCode();
        var login = (await res.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        if (groupId is not null) client.DefaultRequestHeaders.Add(CurrentUserProvider.GroupHeader, groupId.ToString());
        return client;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }
}

public class CapturingEmailSender : IEmailSender
{
    public List<(string to, string subject, string text)> Sent { get; } = [];
    public string Description => "test capture";
    public string? LastCode => Sent.Count == 0 ? null : Regex.Match(Sent[^1].text, @"\b(\d{6})\b").Groups[1].Value;

    public Task SendAsync(string to, string subject, string html, string plainText, CancellationToken ct = default)
    {
        Sent.Add((to, subject, plainText));
        return Task.CompletedTask;
    }
}

/// <summary>
/// All API test classes share one server and database. ApiFactory configures the app through process-wide
/// environment variables, so two factories must never start at the same time.
/// </summary>
[CollectionDefinition(Name)]
public class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}

/// <summary>Stands in for EasyPost: "buys" a 65-cent test label and remembers what it was asked for.</summary>
public class FakePostageProvider : IPostageProvider
{
    public List<(PostageAddress From, PostageAddress To)> Bought { get; } = [];
    /// <summary>The account key each purchase was made with (null = the site's own account).</summary>
    public List<string?> AccountKeys { get; } = [];
    public bool Configured => true;
    public bool AcceptsAccountKeys => true;
    public string Description => "Fake postage (tests)";

    public Task<PostageResult> BuyPostcardAsync(PostageAddress from, PostageAddress to, double widthIn, double heightIn, string? accountKey = null, CancellationToken ct = default)
    {
        lock (AccountKeys) AccountKeys.Add(accountKey);
        if (to.Name.Contains("Undeliverable")) throw new PostageException("EasyPost: Address not found (street1: House number is missing)");
        lock (Bought) Bought.Add((from, to));
        var n = Bought.Count;
        return Task.FromResult(new PostageResult("Fake", $"shp_test{n}", 65, $"https://labels.test/{n}.png", $"94001{n:000000}", true));
    }
}

/// <summary>
/// Stands in for PostGrid. Keys containing "bad" are refused; a guest named "Refused" is turned down by PostGrid and
/// one named "Unreachable" gets no answer. Like PostGrid, a repeated idempotency key returns the first card instead of
/// creating another. <see cref="Created"/> counts the cards really created.
/// </summary>
public class FakePostGridMailer : IPostGridMailer
{
    private readonly Dictionary<string, MailedPostcard> _byIdempotencyKey = [];
    private readonly Dictionary<string, string> _status = [];
    public List<(string Key, MailPostcardRequest Request, string IdempotencyKey)> Requests { get; } = [];
    public int Created { get; private set; }
    public string Description => "Fake PostGrid (tests)";

    public Task<(bool Ok, string Message)> TestKeyAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(key.Contains("bad") ? (false, "PostGrid did not accept this key (401).") : (true, "PostGrid accepted the key."));

    public Task<MailedPostcard> CreatePostcardAsync(string key, MailPostcardRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        lock (Requests)
        {
            Requests.Add((key, request, idempotencyKey));
            if (_byIdempotencyKey.TryGetValue(idempotencyKey, out var seen)) return Task.FromResult(seen);
            if (request.To.LastName?.Contains("Unreachable") == true) throw new PostGridException("PostGrid could not be reached.", answered: false);
            if (request.To.LastName?.Contains("Refused") == true) throw new PostGridException("PostGrid refused the card (422: address is invalid).", answered: true);
            Created++;
            var card = new MailedPostcard($"postcard_test{Created}", MailStatuses.Ready, !MailStatuses.IsKeyForTest(key), $"https://proofs.test/{Created}.pdf", null);
            _byIdempotencyKey[idempotencyKey] = card;
            _status[card.Id] = MailStatuses.Ready;
            return Task.FromResult(card);
        }
    }

    /// <summary>Moves a card along, as PostGrid does when it starts printing.</summary>
    public void SetStatus(string id, string status) { lock (Requests) _status[id] = status; }

    public Task<MailedPostcard> GetPostcardAsync(string key, string id, CancellationToken ct = default)
    {
        lock (Requests) return Task.FromResult(new MailedPostcard(id, _status.GetValueOrDefault(id, MailStatuses.Ready), true, $"https://proofs.test/{id}.pdf", null));
    }

    public Task<MailedPostcard> CancelPostcardAsync(string key, string id, CancellationToken ct = default)
    {
        lock (Requests)
        {
            if (_status.GetValueOrDefault(id) != MailStatuses.Ready) throw new PostGridException("PostGrid refused the cancellation (400: only ready orders can be cancelled).", answered: true);
            _status[id] = MailStatuses.Cancelled;
            return Task.FromResult(new MailedPostcard(id, MailStatuses.Cancelled, true, null, null));
        }
    }

    public List<(MailLetterRequest Request, string IdempotencyKey)> Letters { get; } = [];

    public Task<MailedPostcard> CreateLetterAsync(string key, MailLetterRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        lock (Requests)
        {
            Letters.Add((request, idempotencyKey));
            return Task.FromResult(new MailedPostcard($"letter_test{Letters.Count}", MailStatuses.Ready, !MailStatuses.IsKeyForTest(key), null, null));
        }
    }
}

/// <summary>
/// Stands in for Stripe. Saving a payment method gives <see cref="NextMethod"/> (a credit card unless a test sets
/// something else); a charge succeeds at once for a card and is "processing" for a bank account until
/// <see cref="Settle"/>. Like Stripe, a repeated idempotency key returns the first payment instead of charging again.
/// </summary>
public class FakePayments : IPaymentProvider
{
    private readonly Dictionary<string, PaymentState> _byIdempotencyKey = [];
    private readonly Dictionary<string, PaymentState> _payments = [];
    public List<(ChargeRequest Request, string IdempotencyKey)> Charges { get; } = [];
    public SavedMethod NextMethod { get; set; } = new("pm_card1", PayKinds.Card, "credit", "Visa credit card ending 4242", null);
    /// <summary>The next charge is declined.</summary>
    public bool DeclineNext { get; set; }
    public string Description => "Fake Stripe (tests)";

    public Task<(bool Ok, string Message)> TestKeyAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(key.Contains("bad") ? (false, "Stripe did not accept the site's key.") : (true, "Stripe accepted the key."));
    public Task<string> CreateCustomerAsync(string key, string name, string? email, int groupId, CancellationToken ct = default) => Task.FromResult($"cus_test{groupId}");
    public Task<PaymentSetupLink> StartSetupAsync(string key, string customerId, string returnUrl, string cancelUrl, CancellationToken ct = default) =>
        Task.FromResult(new PaymentSetupLink("cs_test_1", "https://pay.test/setup?return=" + Uri.EscapeDataString(returnUrl)));
    public Task<SavedMethod?> FinishSetupAsync(string key, string sessionId, string customerId, CancellationToken ct = default) =>
        Task.FromResult(sessionId == "cs_abandoned" ? null : NextMethod);

    public Task<PaymentState> ChargeAsync(string key, ChargeRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        lock (Charges)
        {
            if (_byIdempotencyKey.TryGetValue(idempotencyKey, out var seen)) return Task.FromResult(seen);
            Charges.Add((request, idempotencyKey));
            var id = $"pi_test{Charges.Count}";
            var state = DeclineNext ? new PaymentState(id, PayStates.Failed, "Your card was declined.")
                : new PaymentState(id, request.Kind == PayKinds.Bank ? PayStates.Processing : PayStates.Succeeded, null);
            DeclineNext = false;
            _byIdempotencyKey[idempotencyKey] = state;
            _payments[id] = state;
            return Task.FromResult(state);
        }
    }

    /// <summary>The bank payment arrives.</summary>
    public void Settle(string id) { lock (Charges) _payments[id] = new PaymentState(id, PayStates.Succeeded, null); }
    public Task<PaymentState> GetPaymentAsync(string key, string id, CancellationToken ct = default) { lock (Charges) return Task.FromResult(_payments[id]); }
    public Task RemoveMethodAsync(string key, string methodId, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Stands in for USPS: "Nowhere" streets do not exist, "Tower" buildings need a unit, "Avenue" is corrected to
/// "AVE" with a ZIP+4, a street containing "Outage" means USPS is down, and anything else is confirmed as typed.
/// </summary>
/// <summary>Stands in for the address service's suggestions: offers two addresses for whatever street was typed.</summary>
public class FakeAddressSuggester : IAddressSuggester
{
    public int Calls { get; private set; }
    public string? LastNearState { get; private set; }
    public bool Configured { get; set; } = true;

    public Task<List<AddressPreview>> SuggestAsync(AddressInput typed, string? nearState, CancellationToken ct = default)
    {
        Calls++;
        LastNearState = nearState;
        var street = (typed.Street ?? "").Trim();
        return Task.FromResult(street.Length < 4 ? [] : new List<AddressPreview>
        {
            new($"{street} St", "Joliet", "IL", "60431"),
            new($"{street} Ave", "Lockport", null, "60441"),
        });
    }
}

public class FakeAddressVerifier : IAddressVerifier
{
    public int Calls { get; private set; }
    public bool Configured => true;
    public string Description => "Fake USPS (tests)";

    public Task<AddressCheck> CheckAsync(AddressInput input, CancellationToken ct = default)
    {
        Calls++;
        var street = input.Street ?? "";
        if (string.IsNullOrWhiteSpace(street)) return Task.FromResult(new AddressCheck(AddressStatuses.Incomplete, "Enter the street address to check it."));
        if (street.Contains("Outage")) return Task.FromResult(new AddressCheck(AddressStatuses.Unavailable, "USPS could not be reached."));
        if (street.Contains("Nowhere")) return Task.FromResult(new AddressCheck(AddressStatuses.NotFound, "USPS does not recognise this address."));
        var suggestion = new AddressSuggestion(street.ToUpperInvariant().Replace("AVENUE", "AVE"), (input.City ?? "Joliet").ToUpperInvariant(), "IL", "60431-1234");
        if (street.Contains("Tower")) return Task.FromResult(new AddressCheck(AddressStatuses.NeedsUnit, "USPS needs an apartment, suite or unit number.", suggestion));
        return Task.FromResult(street.Contains("Avenue")
            ? new AddressCheck(AddressStatuses.Corrected, $"USPS writes this address as {suggestion.StreetAddress}.", suggestion)
            : new AddressCheck(AddressStatuses.Verified, "USPS delivers to this address.", suggestion));
    }
}
