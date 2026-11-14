using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Auth;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Models;
using RestaurantFeedback.Api.Services;

namespace RestaurantFeedback.Api.Controllers;

/// <summary>
/// The System Administrator's own area: an overview of the whole site, security, system status, and directories of
/// every restaurant and person across investment groups. Nothing here is restaurant-level work (entering cards,
/// replying to guests); for that a System Administrator views a group as its administrator.
/// </summary>
[ApiController]
[Route("api/system")]
[Authorize(Roles = "SuperAdmin")]
public class SystemAdminController(AppDbContext db, AppInfo app, IWebHostEnvironment env, IGroupSettingsStore settingsStore, IEmailSender mail,
    InteractionLogOptions logOptions, AuditService audit, CurrentUserProvider users, IPostageProvider postage, CardFollowUps followUps,
    IAddressVerifier addressVerifier, UspsAccountStore uspsAccount) : ControllerBase
{
    private const string SignIn = "Login";
    private const string SignInFailed = "LoginFailed";

    // ---- Overview -------------------------------------------------------------------------------------------

    [HttpGet("overview")]
    public async Task<SystemOverview> Overview()
    {
        var now = DateTime.UtcNow;
        var today = now.Date;
        var todayDate = DateOnly.FromDateTime(now);
        var weekAgo = now.AddDays(-7);
        var dayAgo = now.AddHours(-24);

        var groups = await db.Groups.AsNoTracking().OrderBy(g => g.Name).ToListAsync();
        var restaurants = await db.Restaurants.AsNoTracking().ToListAsync();
        var people = await db.Users.AsNoTracking().Select(u => new { u.Id, u.Username, u.DisplayName, u.Role, u.GroupId, u.IsActive, u.MustChangePassword, u.LastLoginAt, u.CreatedAt }).ToListAsync();
        var assigned = (await db.UserRestaurants.AsNoTracking().Where(ur => ur.User.IsActive).Select(ur => ur.RestaurantId).Distinct().ToListAsync()).ToHashSet();
        var accounts = people.ToDictionary(p => p.Id);
        var groupNames = groups.ToDictionary(g => g.Id, g => g.Name);

        // The last week's interactions per person and day (System Administrators' own visits don't count as use).
        var acts = await db.InteractionLogs.AsNoTracking().Where(l => l.At >= weekAgo && l.UserId != null && l.Role != UserRole.SuperAdmin)
            .GroupBy(l => new { l.UserId, l.GroupId, Day = l.At.Date })
            .Select(g => new { g.Key.UserId, g.Key.GroupId, g.Key.Day, Last = g.Max(l => l.At) }).ToListAsync();
        acts = acts.Where(a => accounts.ContainsKey(a.UserId!.Value)).ToList();

        var signIns = await db.InteractionLogs.AsNoTracking().Where(l => l.At >= weekAgo && (l.Action == SignIn || l.Action == SignInFailed))
            .OrderByDescending(l => l.At).Take(500).ToListAsync();
        var errors24h = await db.InteractionLogs.CountAsync(l => l.At >= dayAgo && l.Status >= 500);
        var stale24h = await db.InteractionLogs.CountAsync(l => l.At >= dayAgo && l.Status == 426);

        var cards = await db.Comments.AsNoTracking().Where(c => !c.Responded || c.CreatedAt >= weekAgo)
            .Select(c => new { c.Restaurant.GroupId, c.Responded, c.VisitDate, c.CreatedAt }).ToListAsync();
        bool OverAWeek(DateOnly visit) => todayDate.DayNumber - visit.DayNumber > 7;

        var attention = new List<AttentionItem>();
        if (errors24h > 0)
            attention.Add(new("high", $"{errors24h} server error{(errors24h == 1 ? "" : "s")} in the last 24 hours", "Something failed for someone. System status lists what and when.", "/system"));
        var failed24h = signIns.Where(s => s.Action == SignInFailed && s.At >= dayAgo).ToList();
        foreach (var burst in failed24h.GroupBy(s => (s.UserName ?? "").ToLowerInvariant()).Where(g => g.Count() >= 5))
            attention.Add(new("high", $"{burst.Count()} failed sign-ins for \"{burst.Key}\" in 24 hours", "Could be a forgotten password or someone guessing. Security shows where they came from.", "/security"));
        foreach (var g in groups.Where(g => g.IsActive))
        {
            if (!people.Any(u => u.GroupId == g.Id && u.Role == UserRole.GroupAdmin && u.IsActive))
                attention.Add(new("high", $"{g.Name} has no Investment Group Administrator", "Nobody can manage this group's people, restaurants and settings.", "/groups"));
            if (!restaurants.Any(r => r.GroupId == g.Id && r.IsActive))
                attention.Add(new("medium", $"{g.Name} has no restaurants", "Add its restaurants so cards can be entered.", "/groups"));
            var waiting = cards.Count(c => c.GroupId == g.Id && !c.Responded && OverAWeek(c.VisitDate));
            if (waiting > 0)
                attention.Add(new("medium", $"{waiting} card{(waiting == 1 ? " has" : "s have")} waited over a week at {g.Name}", "Guests who have not had a reply yet.", $"/reports?groupIds={g.Id}"));
            var dueFollowUps = (await followUps.DueAsync(g.Id, restaurants.Where(r => r.GroupId == g.Id).Select(r => r.Id))).Count;
            if (dueFollowUps > 0)
                attention.Add(new("info", $"{dueFollowUps} guest{(dueFollowUps == 1 ? " is" : "s are")} due a follow-up at {g.Name}", "Their coupon cards were sent and nobody has checked they arrived. The group's managers and owners see a reminder.", $"/reports?groupIds={g.Id}"));
        }
        var never = people.Count(u => u.IsActive && u.LastLoginAt == null && u.CreatedAt < now.AddDays(-2));
        if (never > 0)
            attention.Add(new("medium", $"{never} {(never == 1 ? "person has" : "people have")} never signed in", "Their accounts were created more than two days ago.", "/people?status=never"));
        if (!addressVerifier.Configured)
            attention.Add(new("info", "Guest addresses are not being checked with USPS", "Enter the site's USPS account details on Connected Systems to switch address checking on.", "/connected-systems"));
        if (mail is LoggingEmailSender)
            attention.Add(new("medium", "Email is not set up", "Password codes cannot be emailed, so nobody can change their own password.", "/system"));
        foreach (var r in restaurants.Where(r => r.IsActive && !assigned.Contains(r.Id) && groups.Any(g => g.Id == r.GroupId && g.IsActive)))
            attention.Add(new("info", $"{r.Name} has no owner, manager or team login yet", $"Only the administrators of {groupNames.GetValueOrDefault(r.GroupId)} can enter cards there.", "/all-restaurants"));
        var pending = people.Count(u => u.IsActive && u.MustChangePassword);
        if (pending > 0)
            attention.Add(new("info", $"{pending} {(pending == 1 ? "person still has" : "people still have")} a temporary password", "They will be asked to choose a new one when they next sign in.", "/people?status=temporary"));
        if (stale24h > 0)
            attention.Add(new("info", $"{stale24h} request{(stale24h == 1 ? "" : "s")} from out-of-date copies of the site", "Those browsers were told to reload. Nothing to do unless it keeps happening.", "/security"));
        if (people.Count(u => u.Role == UserRole.SuperAdmin && u.IsActive) == 1)
            attention.Add(new("info", "You are the only System Administrator", "Add a second one so the site can still be run if you lose access.", "/people?role=SuperAdmin"));

        var snapshots = groups.Select(g => new GroupSnapshot(g.Id, g.Name, g.IsActive, restaurants.Count(r => r.GroupId == g.Id),
            people.Count(u => u.GroupId == g.Id && u.IsActive), people.Count(u => u.GroupId == g.Id && u.IsActive && u.Role == UserRole.GroupAdmin),
            acts.Where(a => a.GroupId == g.Id).Select(a => a.UserId).Distinct().Count(),
            cards.Count(c => c.GroupId == g.Id && c.CreatedAt >= weekAgo), cards.Count(c => c.GroupId == g.Id && !c.Responded),
            cards.Count(c => c.GroupId == g.Id && !c.Responded && OverAWeek(c.VisitDate)),
            acts.Where(a => a.GroupId == g.Id).Select(a => (DateTime?)Utc(a.Last)).Max())).ToList();

        return new SystemOverview(now, groups.Count, groups.Count(g => g.IsActive), restaurants.Count(r => r.IsActive),
            people.Count(u => u.IsActive && u.Role != UserRole.SuperAdmin), people.Count(u => u.IsActive && u.Role == UserRole.SuperAdmin),
            acts.Where(a => a.Day == today).Select(a => a.UserId).Distinct().Count(), acts.Select(a => a.UserId).Distinct().Count(),
            signIns.Count(s => s.Action == SignIn && s.At >= today), failed24h.Count,
            cards.Count(c => c.CreatedAt >= today), cards.Count(c => c.CreatedAt >= weekAgo), cards.Count(c => !c.Responded),
            cards.Count(c => !c.Responded && OverAWeek(c.VisitDate)), errors24h,
            attention, snapshots, signIns.Take(12).Select(s => ToSignIn(s, accounts.GetValueOrDefault(s.UserId ?? 0)?.DisplayName, groupNames)).ToList());
    }

    // ---- Security -------------------------------------------------------------------------------------------

    [HttpGet("security")]
    public async Task<SystemSecurity> Security([FromQuery] int days = 7)
    {
        days = Math.Clamp(days, 1, 365);
        var since = DateTime.UtcNow.AddDays(-days);
        var groupNames = await db.Groups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name);
        var people = await db.Users.AsNoTracking().Include(u => u.Group).Include(u => u.UserRestaurants).ToListAsync();
        var byId = people.ToDictionary(u => u.Id);
        var usernames = people.Select(u => u.Username).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rows = await db.InteractionLogs.AsNoTracking()
            .Where(l => l.At >= since && (l.Action == SignIn || l.Action == SignInFailed || l.Status == 403 || l.Status == 426))
            .OrderByDescending(l => l.At).Take(5000).ToListAsync();
        var signIns = rows.Where(r => r.Action is SignIn or SignInFailed).ToList();
        var refused = rows.Where(r => r.Status == 403).ToList();

        var failed = signIns.Where(s => s.Action == SignInFailed).GroupBy(s => (s.UserName ?? "(blank)").ToLowerInvariant())
            .Select(g => new FailedSignInRow(g.Key, usernames.Contains(g.Key), g.Count(), Utc(g.Max(s => s.At)),
                g.Select(s => s.IpAddress).Where(ip => ip != null).Distinct().Take(5).ToArray()!))
            .OrderByDescending(f => f.Attempts).ThenByDescending(f => f.Last).Take(50).ToList();

        var refusedBy = refused.GroupBy(r => r.UserId).Select(g =>
        {
            var u = g.Key is int id ? byId.GetValueOrDefault(id) : null;
            return new RefusedRow(u?.Username ?? g.First().UserName, u?.DisplayName, u is null ? null : RoleTitles.Of(u.Role), u?.Group?.Name, g.Count(),
                Utc(g.Max(r => r.At)), g.Select(r => $"{r.Method} {r.Path.Split('?')[0]}").Distinct().Take(5).ToArray());
        }).OrderByDescending(r => r.Count).Take(50).ToList();

        var now = DateTime.UtcNow;
        var flags = new List<AccountFlag>();
        foreach (var u in people.OrderBy(u => u.DisplayName))
        {
            AccountFlag Flag(string issue, DateTime? since) => new(u.Id, u.DisplayName, u.Username, RoleTitles.Of(u.Role), u.GroupId, u.Group?.Name, issue, since);
            if (!u.IsActive) flags.Add(Flag("Suspended", null));
            else if (u.Group is { IsActive: false }) flags.Add(Flag("Their investment group is inactive, so they cannot sign in", null));
            else if (u.LastLoginAt is null) flags.Add(Flag("Has never signed in", Utc(u.CreatedAt)));
            else if (u.LastLoginAt < now.AddDays(-60)) flags.Add(Flag("Has not signed in for over 60 days", Utc(u.LastLoginAt.Value)));
            if (u.IsActive && u.MustChangePassword) flags.Add(Flag("Still has a temporary password", null));
            if (u.IsActive && u.Role != UserRole.TeamMember && string.IsNullOrWhiteSpace(u.Email)) flags.Add(Flag("No email on file, so they cannot change their own password", null));
        }

        return new SystemSecurity(days, signIns.Count(s => s.Action == SignIn), signIns.Count(s => s.Action == SignInFailed), refused.Count,
            rows.Count(r => r.Status == 426), failed, refusedBy, flags,
            people.Where(u => u.Role == UserRole.SuperAdmin).OrderBy(u => u.DisplayName).Select(UserDto.From).ToList(),
            signIns.Take(50).Select(s => ToSignIn(s, byId.GetValueOrDefault(s.UserId ?? 0)?.DisplayName, groupNames)).ToList());
    }

    // ---- System status --------------------------------------------------------------------------------------

    [HttpGet("status")]
    public async Task<SystemStatus> Status()
    {
        var me = await users.GetAsync();
        var myEmail = await db.Users.Where(u => u.Id == me.Id).Select(u => u.Email).FirstOrDefaultAsync();
        var dayAgo = DateTime.UtcNow.AddHours(-24);
        var groupNames = await db.Groups.AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Name);
        var calls = await db.InteractionLogs.AsNoTracking().Where(l => l.At >= dayAgo && l.Kind == InteractionKinds.Api)
            .Select(l => new { l.Method, l.Path, l.Status, l.DurationMs }).ToListAsync();
        var times = calls.Where(c => c.DurationMs != null).Select(c => c.DurationMs!.Value).OrderBy(d => d).ToList();
        var slowest = calls.Where(c => c.DurationMs != null).GroupBy(c => $"{c.Method} {Endpoint(c.Path)}")
            .Select(g => new EndpointStat(g.Key, g.Count(), (int)g.Average(c => c.DurationMs!.Value), g.Max(c => c.DurationMs!.Value), g.Count(c => c.Status >= 500)))
            .OrderByDescending(e => e.AvgMs).Take(8).ToList();
        var recentErrors = (await db.InteractionLogs.AsNoTracking().Where(l => l.Status >= 500).OrderByDescending(l => l.At).Take(20).ToListAsync())
            .Select(l => new ErrorRow(Utc(l.At), l.UserName, l.GroupId is int g ? groupNames.GetValueOrDefault(g) : null, l.Method ?? "", l.Path, l.Status ?? 500, l.Detail)).ToList();
        var database = app.Database();
        var oldest = await db.InteractionLogs.OrderBy(l => l.At).Select(l => (DateTime?)l.At).FirstOrDefaultAsync();

        return new SystemStatus(app.Version, env.EnvironmentName, app.StartedAt, DateTime.UtcNow,
            database is null ? db.Database.ProviderName ?? "database" : $"SQLite ({database.Value.File})", database?.Bytes ?? 0,
            (await db.Database.GetAppliedMigrationsAsync()).LastOrDefault(),
            [
                new("Investment groups", await db.Groups.CountAsync()),
                new("Restaurants", await db.Restaurants.CountAsync()),
                new("People", await db.Users.CountAsync()),
                new("Guest cards", await db.Comments.CountAsync()),
                new("Follow-up entries", await db.CommentActivities.CountAsync()),
                new("Audit log entries", await db.AuditLogs.CountAsync()),
                new("Interaction log entries", await db.InteractionLogs.CountAsync())
            ],
            settingsStore.Description, mail.Description, mail is not LoggingEmailSender, myEmail, logOptions.RetentionDays, oldest is null ? null : Utc(oldest.Value),
            calls.Count, calls.Count(c => c.Status >= 500), calls.Count(c => c.Status is 401 or 403),
            times.Count == 0 ? 0 : (int)times.Average(), times.Count == 0 ? 0 : times[Math.Min(times.Count - 1, (int)(times.Count * 0.95))],
            slowest, recentErrors)
        {
            AddressChecking = addressVerifier.Description,
            AddressCheckingConfigured = addressVerifier.Configured,
            AddressesConfirmed = await db.Comments.CountAsync(c => c.AddressStatus == AddressStatuses.Verified || c.AddressStatus == AddressStatuses.Corrected),
            AddressesNotFound = await db.Comments.CountAsync(c => c.AddressStatus == AddressStatuses.NotFound),
            Postage = postage.Description,
            PostageConfigured = postage.Configured,
            PostageGroupAccounts = await db.Groups.CountAsync(g => g.PostageKeyEncrypted != null),
            PostageCardsBought = await db.Comments.CountAsync(c => c.PostageId != null && !c.PostageTest),
            PostageCentsSpent = await db.Comments.Where(c => c.PostageId != null && !c.PostageTest).SumAsync(c => c.PostageCents ?? 0),
            PostageTestLabels = await db.Comments.CountAsync(c => c.PostageId != null && c.PostageTest)
        };
    }

    /// <summary>Sends a test message to the System Administrator's own address to prove the email service works.</summary>
    [HttpPost("test-email")]
    public async Task<IActionResult> TestEmail(CancellationToken ct)
    {
        var me = await users.GetAsync();
        var to = await db.Users.Where(u => u.Id == me.Id).Select(u => u.Email).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(to)) return BadRequest(new { message = "Your account has no email address on file. Add one on People first." });
        if (mail is LoggingEmailSender) return BadRequest(new { message = "No email service is set up, so nothing can be sent." });
        try
        {
            await mail.SendAsync(to, "Guest Feedback test email", "<p>This is a test from Guest Feedback. Email is working.</p>", "This is a test from Guest Feedback. Email is working.", ct);
        }
        catch (Exception ex)
        {
            await audit.LogAsync(me, "TestEmailFailed", details: new { error = ex.Message });
            return StatusCode(502, new { message = $"The email service refused the message: {ex.Message}" });
        }
        await audit.LogAsync(me, "TestEmailSent", details: new { to = AcsEmailSender.Mask(to) });
        return Ok(new { message = $"Test email sent to {AcsEmailSender.Mask(to)}." });
    }

    // ---- The site's USPS account for address checking ----------------------------------------------------------

    [HttpGet("usps-account")]
    public UspsAccountDto UspsAccount() => UspsDto();

    /// <summary>
    /// Saves the USPS developer app's Consumer Key and Secret. They are encrypted before they are stored and are
    /// never shown again or written to any log; changing them means typing both again.
    /// </summary>
    [HttpPut("usps-account")]
    public async Task<ActionResult<UspsAccountDto>> SetUspsAccount(UspsAccountRequest req, CancellationToken ct)
    {
        var me = await users.GetAsync();
        var key = (req.ConsumerKey ?? "").Trim();
        var secret = (req.ConsumerSecret ?? "").Trim();
        if (key.Length is < 8 or > 200 || secret.Length is < 8 or > 200 || key.Any(char.IsWhiteSpace) || secret.Any(char.IsWhiteSpace))
            return BadRequest(new { message = "Enter both the Consumer Key and the Consumer Secret exactly as USPS shows them for your app, with no spaces." });
        await uspsAccount.SaveAsync(key, secret, req.TestEnvironment, me.DisplayName, ct);
        await audit.LogAsync(me, "UspsAccountSet", details: new { KeyEnding = key[^4..], req.TestEnvironment });
        return UspsDto();
    }

    [HttpDelete("usps-account")]
    public async Task<ActionResult<UspsAccountDto>> RemoveUspsAccount(CancellationToken ct)
    {
        var me = await users.GetAsync();
        var before = uspsAccount.Current;
        await uspsAccount.RemoveAsync(ct);
        if (before?.Source == "Saved in the site") await audit.LogAsync(me, "UspsAccountRemoved", details: new { KeyEnding = before.ClientId[^4..] });
        return UspsDto();
    }

    /// <summary>Signs in to USPS with the saved details and checks one known address, to prove it works.</summary>
    [HttpPost("usps-account/test")]
    public async Task<ServiceTestResult> TestUspsAccount(CancellationToken ct)
    {
        var me = await users.GetAsync();
        var (ok, message) = await addressVerifier.TestAsync(ct);
        await audit.LogAsync(me, ok ? "UspsAccountTestPassed" : "UspsAccountTestFailed", details: new { message });
        return new ServiceTestResult(ok, message);
    }

    private UspsAccountDto UspsDto()
    {
        var a = uspsAccount.Current;
        // CanEnter is false only when the address service is a stand-in (local development) that takes no account.
        var real = addressVerifier is UspsAddressVerifier;
        return a is null
            ? new UspsAccountDto(false, "None", null, false, null, null, real)
            : new UspsAccountDto(true, a.Source, a.ClientId.Length >= 4 ? a.ClientId[^4..] : null, a.BaseUrl.Contains("-tem."), a.SavedAt, a.SavedBy, real);
    }

    // ---- Directories across every investment group ----------------------------------------------------------

    [HttpGet("restaurants")]
    public async Task<List<DirectoryRestaurant>> Restaurants()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var weekAgo = today.AddDays(-7);
        var restaurants = await db.Restaurants.AsNoTracking().Include(r => r.Group).ToListAsync();
        var staff = await db.UserRestaurants.AsNoTracking().Where(ur => ur.User.IsActive)
            .Select(ur => new { ur.RestaurantId, ur.User.Role, ur.User.DisplayName }).ToListAsync();
        var cards = (await db.Comments.AsNoTracking().GroupBy(c => c.RestaurantId)
            .Select(g => new { RestaurantId = g.Key, Cards = g.Count(), Waiting = g.Count(c => !c.Responded),
                Over = g.Count(c => !c.Responded && c.VisitDate < weekAgo), Last = g.Max(c => (DateOnly?)c.VisitDate) }).ToListAsync())
            .ToDictionary(c => c.RestaurantId);
        return restaurants.OrderBy(r => r.Group.Name).ThenBy(r => r.Name).Select(r =>
        {
            var mine = staff.Where(s => s.RestaurantId == r.Id).ToList();
            var c = cards.GetValueOrDefault(r.Id);
            return new DirectoryRestaurant(r.Id, r.Name, r.City, r.State, r.IsActive, r.GroupId, r.Group.Name, r.Group.IsActive,
                mine.Where(s => s.Role == UserRole.Owner).Select(s => s.DisplayName).OrderBy(n => n).ToArray(),
                mine.Count(s => s.Role == UserRole.Manager), mine.Count(s => s.Role == UserRole.TeamMember),
                c?.Cards ?? 0, c?.Waiting ?? 0, c?.Over ?? 0, c?.Last, Utc(r.CreatedAt));
        }).ToList();
    }

    [HttpGet("people")]
    public async Task<List<SystemPerson>> People()
    {
        var since = DateTime.UtcNow.AddDays(-30);
        var people = await db.Users.AsNoTracking().Include(u => u.Group).Include(u => u.UserRestaurants).OrderBy(u => u.DisplayName).ToListAsync();
        var names = await db.Restaurants.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Name);
        var seen = (await db.InteractionLogs.AsNoTracking().Where(l => l.UserId != null).GroupBy(l => l.UserId)
            .Select(g => new { UserId = g.Key, Last = g.Max(l => l.At), Recent = g.Count(l => l.At >= since) }).ToListAsync())
            .ToDictionary(s => s.UserId!.Value);
        return people.Select(u => new SystemPerson(UserDto.From(u),
            u.UserRestaurants.Select(ur => names.GetValueOrDefault(ur.RestaurantId) ?? $"#{ur.RestaurantId}").OrderBy(n => n).ToArray(),
            seen.TryGetValue(u.Id, out var s) ? Utc(s.Last) : null, seen.TryGetValue(u.Id, out var r) ? r.Recent : 0)).ToList();
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private static SignInRow ToSignIn(InteractionLog l, string? displayName, Dictionary<int, string> groupNames) =>
        new(Utc(l.At), l.UserName, displayName ?? l.DisplayName, l.Role is { } r ? RoleTitles.Of(r) : null,
            l.GroupId is int g ? groupNames.GetValueOrDefault(g) : null, l.Action == SignIn, l.IpAddress, l.UserAgent);

    /// <summary>"/api/comments/12/followup?x=1" becomes "/api/comments/{id}/followup" so calls to one endpoint group together.</summary>
    private static string Endpoint(string path) =>
        string.Join('/', path.Split('?')[0].Split('/').Select(s => s.Length > 0 && s.All(char.IsDigit) ? "{id}" : s));

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
}
