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
        };
        foreach (var (k, v) in values) Environment.SetEnvironmentVariable(k, v);
    }

    public CapturingEmailSender Emails { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services => services.AddSingleton<IEmailSender>(Emails));
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
