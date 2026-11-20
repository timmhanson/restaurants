using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using RestaurantFeedback.Api.Auth;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Models;
using RestaurantFeedback.Api.Services;

var builder = WebApplication.CreateBuilder(args);
// Local-only overrides (git-ignored): seed passwords, storage connection strings, etc.
// Inserted ahead of environment variables so deployment / test settings still win.
{
    var sources = builder.Configuration.Sources;
    var envIndex = sources.ToList().FindIndex(s => s is EnvironmentVariablesConfigurationSource);
    var local = new JsonConfigurationSource { Path = "appsettings.Local.json", Optional = true, ReloadOnChange = true, FileProvider = builder.Environment.ContentRootFileProvider };
    sources.Insert(envIndex < 0 ? sources.Count : envIndex, local);
}
var config = builder.Configuration;

// ---- Database (SQLite on the free tier; the file lives on App Service's persistent /home share) ----
var connection = config.GetConnectionString("Default") ?? "Data Source=App_Data/restaurant-feedback.db";
if (connection.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
{
    var path = connection["Data Source=".Length..].Split(';')[0];
    var dir = Path.GetDirectoryName(Path.GetFullPath(path));
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
}
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(connection));

// ---- Auth ----
var jwt = config.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.Key) || jwt.Key.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be configured with at least 32 characters.");
builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = jwt.Issuer,
        ValidateAudience = true, ValidAudience = jwt.Audience,
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
        ValidateLifetime = true, ClockSkew = TimeSpan.FromMinutes(1)
    };
});
builder.Services.AddAuthorization();

// ---- Group settings: Azure Blob Storage, one blob per investment group ----
var storage = config.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
builder.Services.AddSingleton(storage);
if (!string.IsNullOrWhiteSpace(storage.ConnectionString) || !string.IsNullOrWhiteSpace(storage.AccountUrl))
    builder.Services.AddSingleton<IGroupSettingsStore, BlobGroupSettingsStore>();
else
    builder.Services.AddSingleton<IGroupSettingsStore, FileGroupSettingsStore>();

builder.Services.AddSingleton(config.GetSection("Seed").Get<SeedOptions>() ?? new SeedOptions());

// ---- Email (Azure Communication Services) for password-change verification codes ----
var email = config.GetSection("Email").Get<EmailOptions>() ?? new EmailOptions();
builder.Services.AddSingleton(email);
if (!string.IsNullOrWhiteSpace(email.ConnectionString) || !string.IsNullOrWhiteSpace(email.Endpoint))
    builder.Services.AddSingleton<IEmailSender, AcsEmailSender>();
else
    builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();
builder.Services.AddScoped<PasswordCodeService>();

// ---- Checking guest addresses with USPS (optional; cards always save without it) ----
var usps = config.GetSection("Usps").Get<UspsOptions>() ?? new UspsOptions();
builder.Services.AddSingleton(usps);
builder.Services.AddMemoryCache();
// The System Administrator enters the USPS account on System status (stored encrypted); the Usps__* app settings
// are a fallback. Until one exists, address checking simply stays off.
builder.Services.AddSingleton<UspsAccountStore>();
// PostGrid: the site's own account prints and mails cards for every investment group (billed on to each group
// with the site's fee); its address verification key checks addresses before or after USPS. Both keys are
// entered by the System Administrator on Connected Systems.
var postGrid = config.GetSection("PostGrid").Get<PostGridOptions>() ?? new PostGridOptions();
builder.Services.AddSingleton(postGrid);
builder.Services.AddSingleton<PostGridAddressAccountStore>();
builder.Services.AddSingleton<SiteMailing>();
builder.Services.AddSingleton<DatabaseHealth>();
builder.Services.AddSingleton<CardCodes>();
builder.Services.AddScoped<SiteAddress>();
builder.Services.AddScoped<MailLedger>();
builder.Services.AddScoped<BillingService>();
builder.Services.AddHostedService<BillingWorker>();
// Copies of the database: every night, and when the System Administrator asks.
var backup = config.GetSection("Backups").Get<BackupOptions>() ?? new BackupOptions();
builder.Services.AddSingleton(backup);
builder.Services.AddScoped<DatabaseBackups>();
builder.Services.AddHostedService<BackupWorker>();
// Stripe takes payment for the monthly mailing bills, on the site's own Stripe account (Connected Systems).
var payments = config.GetSection("Payments").Get<PaymentOptions>() ?? new PaymentOptions();
builder.Services.AddSingleton(payments);
builder.Services.AddSingleton<PaymentAccountStore>();
if (payments.Sample && builder.Environment.IsDevelopment())
    builder.Services.AddSingleton<IPaymentProvider, SamplePayments>();
else
    builder.Services.AddHttpClient<IPaymentProvider, StripePayments>();
builder.Services.AddScoped<CardDesigns>();
builder.Services.AddScoped<PaymentNotifications>();
builder.Services.AddHttpClient();
if (postGrid.Sample && builder.Environment.IsDevelopment())
    builder.Services.AddSingleton<IPostGridMailer, SamplePostGridMailer>();
else
    builder.Services.AddHttpClient<IPostGridMailer, PostGridMailer>();
if (usps.Sample && builder.Environment.IsDevelopment())
    builder.Services.AddSingleton<IAddressVerifier, SampleAddressVerifier>();
else
{
    builder.Services.AddHttpClient<UspsAddressVerifier>(c => c.Timeout = TimeSpan.FromSeconds(8));
    builder.Services.AddHttpClient<PostGridAddressVerifier>(c => c.Timeout = TimeSpan.FromSeconds(8));
    builder.Services.AddTransient<IAddressVerifier, AddressCheckers>();
}
// Addresses offered as the street is typed. Locally, with stand-ins switched on, they are made up.
if ((usps.Sample || postGrid.Sample) && builder.Environment.IsDevelopment())
    builder.Services.AddSingleton<IAddressSuggester, SampleAddressSuggester>();
else
    builder.Services.AddTransient<IAddressSuggester>(sp => sp.GetRequiredService<PostGridAddressVerifier>());

// ---- Postage for printed coupon cards (optional; stamps by hand always work) ----
var postage = config.GetSection("Postage").Get<PostageOptions>() ?? new PostageOptions();
builder.Services.AddSingleton(postage);
// Each investment group brings its own EasyPost account key (Settings); Postage__EasyPostApiKey is an optional
// site-wide fallback for groups without one.
if (postage.Sample && builder.Environment.IsDevelopment())
    builder.Services.AddSingleton<IPostageProvider, SamplePostageProvider>();
else
    builder.Services.AddHttpClient<IPostageProvider, EasyPostProvider>();
builder.Services.AddScoped<GroupPostageAccounts>();
// Encryption keys for the stored account keys live beside the database, on the app's private persistent storage,
// so they survive restarts and deploys.
var keyRing = config["DataProtection:KeysPath"] ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(
    connection.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase) ? connection["Data Source=".Length..].Split(';')[0] : "App_Data/x")) ?? "App_Data", "keys");
Directory.CreateDirectory(keyRing);
builder.Services.AddDataProtection().SetApplicationName("RestaurantFeedback").PersistKeysToFileSystem(new DirectoryInfo(keyRing));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<CardFollowUps>();
builder.Services.AddScoped<CommentSummaryBuilder>();
// Every user interaction (API calls here, page views and clicks from the browser) for the System Administrator.
builder.Services.AddSingleton(config.GetSection("InteractionLog").Get<InteractionLogOptions>() ?? new InteractionLogOptions());
builder.Services.AddSingleton<InteractionLogQueue>();
builder.Services.AddSingleton(new AppInfo { ConnectionString = connection });
builder.Services.AddHostedService<InteractionLogWriter>();
builder.Services.AddScoped<CurrentUserProvider>();
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(config.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"])
    .AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddHealthChecks();

var app = builder.Build();

var appVersion = CachePolicy.AppVersion(app.Environment);
app.Services.GetRequiredService<AppInfo>().Version = appVersion;
app.UseCors();
app.UseApiNoStore(appVersion);
app.UseMiddleware<AccountUnavailableMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles(CachePolicy.StaticFiles());
app.UseAuthentication();
app.UseMiddleware<InteractionLoggingMiddleware>();
app.UseCurrentClientOnly();
app.UseAuthorization();
app.UseMiddleware<PasswordChangeRequiredMiddleware>();
app.MapControllers();
app.MapHealthChecks("/api/health");
app.MapGet("/api/info", (IGroupSettingsStore store, IEmailSender mail, IWebHostEnvironment env) => new
{
    name = "Restaurant Guest Feedback API",
    environment = env.EnvironmentName,
    settingsStore = store.Description,
    email = mail.Description,
    version = appVersion,
    utc = DateTime.UtcNow
});
// Pictures on the coupon cards the mail house prints (a front, a logo or a photo). Public, because the mail house
// downloads them without signing in; the address includes a hash of the image, so it changes with every upload
// and cannot be guessed.
app.MapGet("/card-art/{groupId:int}/{file}", async (int groupId, string file, AppDbContext db, HttpContext http) =>
{
    var hash = Path.GetFileNameWithoutExtension(file);
    var art = await db.CardImages.AsNoTracking().Where(i => i.GroupId == groupId && i.Hash == hash).Select(i => new { i.Data, i.ContentType }).FirstOrDefaultAsync();
    if (art is null) return Results.NotFound();
    CachePolicy.NoStore(http.Response.Headers);
    return Results.File(art.Data, art.ContentType);
});
// The typefaces cards are printed in, for the card designer in the browser (the printed card carries them inside
// its own page). Only the handful of files built into the site are served.
app.MapGet("/card-fonts/{file}", (string file, HttpContext http) =>
{
    var font = CardElements.FontFile(file);
    if (font is null) return Results.NotFound();
    CachePolicy.NoStore(http.Response.Headers);
    return Results.File(font, "font/woff2");
});
// Stripe's notifications about payments (webhooks). Outside /api because Stripe is not a copy of the site and
// sends no session id. Nothing in a notification is trusted until its signature has been checked, and even then
// the site asks Stripe itself how the payment stands before a bill is marked paid.
app.MapPost("/webhooks/stripe", async (HttpContext http, PaymentAccountStore account, BillingService billing, PaymentNotifications notifications, ILogger<StripePayments> log) =>
{
    CachePolicy.NoStore(http.Response.Headers);
    if (account.WebhookSecret is not { } secret) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (http.Request.ContentLength is > 512_000) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    using var reader = new StreamReader(http.Request.Body, System.Text.Encoding.UTF8);
    var body = await reader.ReadToEndAsync(http.RequestAborted);
    if (body.Length > 512_000) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    if (!StripeWebhooks.Verify(body, http.Request.Headers["Stripe-Signature"].ToString(), secret, DateTimeOffset.UtcNow))
    {
        log.LogWarning("A Stripe notification was refused: its signature did not match or it was too old");
        return Results.BadRequest(new { message = "The signature did not match." });
    }
    account.NoteNotification();
    // Everything Stripe sent is kept, whatever it is about, before anything is done with it.
    var kept = await notifications.RecordAsync(body, http.RequestAborted);
    try
    {
        var settled = StripeWebhooks.PaymentId(body) is { } paymentId && await billing.PaymentNotifiedAsync(paymentId, http.RequestAborted);
        // A repeat of a notification that was already acted on leaves what was done the first time on record.
        if (settled || kept.Outcome is null)
            await notifications.OutcomeAsync(kept, settled ? "The site asked Stripe how the payment stands and brought the bill up to date."
                : kept.InvoiceId is null ? "Kept. It is not about a bill on this site." : "Kept. The bill was already up to date.", http.RequestAborted);
    }
    catch (PaymentException ex)
    {
        // Stripe sends it again later; the hourly round also checks payments that are on their way.
        log.LogWarning("A Stripe notification could not be acted on yet: {Message}", ex.Message);
        await notifications.OutcomeAsync(kept, "Kept. Stripe could not be asked about the payment just then, so it will be looked at again.", http.RequestAborted);
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    return Results.Ok(new { received = true });
});
// An unknown API address is "not found", never the web page (which would look like a successful answer).
app.MapFallback("/api/{**path}", () => Results.NotFound(new { message = "Not found." }));
// Serve the React app for any non-API route (client-side routing).
app.MapFallbackToFile("index.html", CachePolicy.StaticFiles());

await DbSeeder.SeedAsync(app.Services);
await app.Services.GetRequiredService<UspsAccountStore>().LoadAsync();
await app.Services.GetRequiredService<PostGridAddressAccountStore>().LoadAsync();
await app.Services.GetRequiredService<SiteMailing>().LoadAsync();
await app.Services.GetRequiredService<CardCodes>().LoadAsync();
await app.Services.GetRequiredService<PaymentAccountStore>().LoadAsync();
app.Run();

public partial class Program;
