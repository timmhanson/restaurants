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
if (usps.Sample && builder.Environment.IsDevelopment())
    builder.Services.AddSingleton<IAddressVerifier, SampleAddressVerifier>();
else
    builder.Services.AddHttpClient<IAddressVerifier, UspsAddressVerifier>(c => c.Timeout = TimeSpan.FromSeconds(8));

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
// An unknown API address is "not found", never the web page (which would look like a successful answer).
app.MapFallback("/api/{**path}", () => Results.NotFound(new { message = "Not found." }));
// Serve the React app for any non-API route (client-side routing).
app.MapFallbackToFile("index.html", CachePolicy.StaticFiles());

await DbSeeder.SeedAsync(app.Services);
await app.Services.GetRequiredService<UspsAccountStore>().LoadAsync();
app.Run();

public partial class Program;
