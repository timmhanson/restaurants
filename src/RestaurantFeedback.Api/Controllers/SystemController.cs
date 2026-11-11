using System.Text;
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
/// System Administrator only: the full interaction log and reports across investment groups and restaurants.
/// Nobody else can read the interaction log, including group administrators.
/// </summary>
[ApiController]
[Route("api/system")]
[Authorize(Roles = "SuperAdmin")]
public class SystemController(AppDbContext db, AuditService audit, CurrentUserProvider users) : ControllerBase
{
    private const int ExportLimit = 100_000;

    [HttpGet("catalog")]
    public async Task<SystemCatalog> Catalog()
    {
        var groups = await db.Groups.OrderBy(g => g.Name).Select(g => new CatalogGroup(g.Id, g.Name, g.IsActive)).ToListAsync();
        var restaurants = await db.Restaurants.OrderBy(r => r.Name).Select(r => new CatalogRestaurant(r.Id, r.Name, r.GroupId, r.IsActive)).ToListAsync();
        var people = (await db.Users.OrderBy(u => u.DisplayName).Select(u => new { u.Id, u.Username, u.DisplayName, u.Role, u.GroupId }).ToListAsync())
            .Select(u => new CatalogUser(u.Id, u.Username, u.DisplayName, RoleTitles.Of(u.Role), u.GroupId)).ToList();
        return new SystemCatalog(groups, restaurants, people);
    }

    // ---- Interaction log ----------------------------------------------------------------------------------

    [HttpGet("activity")]
    public async Task<InteractionPage> Activity([FromQuery] ActivityQuery q)
    {
        var rows = Filter(q);
        var total = await rows.CountAsync();
        var size = Math.Clamp(q.PageSize ?? 100, 10, 500);
        var page = Math.Max(1, q.Page ?? 1);
        var items = await rows.OrderByDescending(l => l.At).ThenByDescending(l => l.Id).Skip((page - 1) * size).Take(size).ToListAsync();
        return new InteractionPage(total, page, size, await ToDtos(items));
    }

    [HttpGet("activity/export")]
    public async Task<IActionResult> ExportActivity([FromQuery] ActivityQuery q)
    {
        var items = await Filter(q).OrderByDescending(l => l.At).ThenByDescending(l => l.Id).Take(ExportLimit).ToListAsync();
        var dtos = await ToDtos(items);
        var sb = new StringBuilder();
        sb.AppendLine("Time (UTC),Kind,User,Username,Role,Investment Group,Restaurant,Viewing As,Method,Path,Action,Detail,Status,Duration (ms),Session,IP Address,User Agent");
        foreach (var d in dtos)
            sb.AppendLine(string.Join(',', d.At.ToString("yyyy-MM-dd HH:mm:ss"), d.Kind, Csv(d.DisplayName), Csv(d.UserName), Csv(d.RoleTitle), Csv(d.GroupName),
                Csv(d.RestaurantName), d.ViewingAs ? "Yes" : "", d.Method ?? "", Csv(d.Path), Csv(d.Action), Csv(d.Detail), d.Status?.ToString() ?? "",
                d.DurationMs?.ToString() ?? "", d.SessionId ?? "", d.IpAddress ?? "", Csv(d.UserAgent)));
        await audit.LogAsync(await users.GetAsync(), "InteractionLogExported", details: new { q.From, q.To, q.GroupId, q.RestaurantId, q.UserId, q.Kind, q.Search, rows = dtos.Count });
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", $"interaction-log-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
    }

    private IQueryable<InteractionLog> Filter(ActivityQuery q)
    {
        var rows = db.InteractionLogs.AsNoTracking().AsQueryable();
        if (q.From is { } from) rows = rows.Where(l => l.At >= from.ToDateTime(TimeOnly.MinValue));
        if (q.To is { } to) rows = rows.Where(l => l.At < to.AddDays(1).ToDateTime(TimeOnly.MinValue));
        if (q.GroupId is { } gid) rows = gid == 0 ? rows.Where(l => l.GroupId == null) : rows.Where(l => l.GroupId == gid);
        if (q.RestaurantId is { } rid)
        {
            // Rows about the restaurant, plus everything done by the people who work there.
            var staff = db.UserRestaurants.Where(ur => ur.RestaurantId == rid).Select(ur => (int?)ur.UserId);
            rows = rows.Where(l => l.RestaurantId == rid || (l.RestaurantId == null && staff.Contains(l.UserId)));
        }
        if (q.UserId is { } uid) rows = rows.Where(l => l.UserId == uid);
        if (!string.IsNullOrWhiteSpace(q.Kind)) rows = rows.Where(l => l.Kind == q.Kind);
        if (q.ErrorsOnly == true) rows = rows.Where(l => l.Status >= 400);
        if (!string.IsNullOrWhiteSpace(q.SessionId)) rows = rows.Where(l => l.SessionId == q.SessionId);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var like = $"%{q.Search.Trim()}%";
            rows = rows.Where(l => EF.Functions.Like(l.Path, like) || EF.Functions.Like(l.Action ?? "", like) || EF.Functions.Like(l.Detail ?? "", like)
                                   || EF.Functions.Like(l.UserName ?? "", like) || EF.Functions.Like(l.DisplayName ?? "", like));
        }
        return rows;
    }

    private async Task<List<InteractionDto>> ToDtos(List<InteractionLog> items)
    {
        var groups = await db.Groups.ToDictionaryAsync(g => g.Id, g => g.Name);
        var restaurants = await db.Restaurants.ToDictionaryAsync(r => r.Id, r => r.Name);
        return items.Select(l => new InteractionDto(l.Id, DateTime.SpecifyKind(l.At, DateTimeKind.Utc), l.Kind, l.UserId, l.UserName, l.DisplayName,
            l.Role is { } r ? RoleTitles.Of(r) : null, l.GroupId, l.GroupId is { } g ? groups.GetValueOrDefault(g) : null,
            l.RestaurantId, l.RestaurantId is { } rid ? restaurants.GetValueOrDefault(rid) : null, l.ViewingAs, l.Method, l.Path, l.Action, l.Detail,
            l.Status, l.DurationMs, l.SessionId, l.IpAddress, l.UserAgent)).ToList();
    }

    // ---- Reports ------------------------------------------------------------------------------------------

    /// <summary>
    /// Usage and guest-card figures by investment group, restaurant and person for a date range (default: the
    /// last 30 days). Card figures use the visit date; activity uses when it happened. A restaurant's activity is
    /// what was done about it plus everything done by the people who work there. "Active people" never counts
    /// System Administrators, so viewing a group as its admin does not inflate its numbers.
    /// </summary>
    [HttpGet("reports")]
    public async Task<SystemReport> Reports([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? groupIds, [FromQuery] string? restaurantIds)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var end = to ?? today;
        var start = from ?? end.AddDays(-29);
        var startAt = start.ToDateTime(TimeOnly.MinValue);
        var endAt = end.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var gFilter = Ids(groupIds);
        var rFilter = Ids(restaurantIds);

        var allGroups = await db.Groups.AsNoTracking().ToListAsync();
        var allRestaurants = await db.Restaurants.AsNoTracking().ToListAsync();
        var restaurants = allRestaurants.Where(r => (gFilter.Count == 0 || gFilter.Contains(r.GroupId)) && (rFilter.Count == 0 || rFilter.Contains(r.Id))).ToList();
        var groupIdsInScope = rFilter.Count > 0 ? restaurants.Select(r => r.GroupId).ToHashSet() : gFilter.Count > 0 ? gFilter : allGroups.Select(g => g.Id).ToHashSet();
        var groups = allGroups.Where(g => groupIdsInScope.Contains(g.Id)).OrderBy(g => g.Name).ToList();
        var restaurantIdSet = restaurants.Select(r => r.Id).ToHashSet();
        var groupNames = allGroups.ToDictionary(g => g.Id, g => g.Name);

        var people = await db.Users.AsNoTracking().Select(u => new { u.Id, u.Username, u.DisplayName, u.Role, u.GroupId, u.IsActive }).ToListAsync();
        var accounts = people.Select(p => p.Id).ToHashSet(); // active-people counts only include people who still have an account
        var assignments = await db.UserRestaurants.AsNoTracking().Select(ur => new { ur.UserId, ur.RestaurantId }).ToListAsync();
        var staffOf = assignments.GroupBy(a => a.RestaurantId).ToDictionary(g => g.Key, g => g.Select(a => a.UserId).ToHashSet());
        var restaurantsOf = assignments.GroupBy(a => a.UserId).ToDictionary(g => g.Key, g => g.Select(a => a.RestaurantId).ToHashSet());

        // Cards with a visit in range.
        var cards = await db.Comments.AsNoTracking()
            .Where(c => c.VisitDate >= start && c.VisitDate <= end && restaurantIdSet.Contains(c.RestaurantId))
            .Select(c => new CardRow(c.RestaurantId, c.Restaurant.GroupId, c.VisitDate, c.Responded, c.RepliedOn, c.Source)).ToListAsync();
        // Cards entered in range, by who entered them and on which day.
        var entered = await db.Comments.AsNoTracking()
            .Where(c => c.CreatedAt >= startAt && c.CreatedAt < endAt && restaurantIdSet.Contains(c.RestaurantId))
            .Select(c => new { c.CreatedByUserId, c.Restaurant.GroupId, c.CreatedAt }).ToListAsync();

        // Interactions in range, pre-aggregated per person, day, group, restaurant and kind.
        var acts = await db.InteractionLogs.AsNoTracking()
            .Where(l => l.At >= startAt && l.At < endAt)
            .GroupBy(l => new { l.UserId, l.GroupId, l.RestaurantId, Day = l.At.Date, l.Kind, SignIn = l.Action == "Login", Failed = l.Action == "LoginFailed", System = l.Role == UserRole.SuperAdmin })
            .Select(g => new { g.Key.UserId, g.Key.GroupId, g.Key.RestaurantId, g.Key.Day, g.Key.Kind, g.Key.SignIn, g.Key.Failed, g.Key.System, Count = g.Count(), Last = g.Max(l => l.At) })
            .ToListAsync();
        // Restaurant filter: rows about those restaurants or by the people who work there.
        bool InRestaurants(int? rid, int? uid) => rid is int r ? restaurantIdSet.Contains(r)
            : uid is int u && restaurantsOf.TryGetValue(u, out var mine) && mine.Overlaps(restaurantIdSet);
        var scoped = acts.Where(a => rFilter.Count > 0 ? InRestaurants(a.RestaurantId, a.UserId) : gFilter.Count == 0 || (a.GroupId is int g && gFilter.Contains(g))).ToList();

        var byGroup = groups.Select(g =>
        {
            var rows = scoped.Where(a => a.GroupId == g.Id).ToList();
            var groupRestaurants = restaurants.Where(r => r.GroupId == g.Id).Select(r => r.Id).ToHashSet();
            return new GroupReportRow(g.Id, g.Name, g.IsActive, groupRestaurants.Count, people.Count(u => u.GroupId == g.Id && u.IsActive),
                rows.Where(a => a.UserId is int au && !a.System && accounts.Contains(au)).Select(a => a.UserId).Distinct().Count(), rows.Where(a => a.SignIn && a.Kind == InteractionKinds.Api).Sum(a => a.Count),
                rows.Sum(a => a.Count), rows.Count == 0 ? null : Utc(rows.Max(a => a.Last)), Stats(cards.Where(c => c.GroupId == g.Id), today));
        }).ToList();

        var byRestaurant = restaurants.OrderBy(r => groupNames.GetValueOrDefault(r.GroupId)).ThenBy(r => r.Name).Select(r =>
        {
            var staff = staffOf.GetValueOrDefault(r.Id) ?? [];
            var rows = acts.Where(a => a.RestaurantId == r.Id || (a.RestaurantId == null && a.UserId is int u && staff.Contains(u))).ToList();
            var mine = cards.Where(c => c.RestaurantId == r.Id).ToList();
            return new RestaurantReportRow(r.Id, r.Name, r.GroupId, groupNames.GetValueOrDefault(r.GroupId) ?? "", r.IsActive, staff.Count,
                rows.Where(a => a.UserId is int au && !a.System && accounts.Contains(au)).Select(a => a.UserId).Distinct().Count(), rows.Sum(a => a.Count),
                rows.Count == 0 ? null : Utc(rows.Max(a => a.Last)), mine.Count == 0 ? null : mine.Max(c => c.VisitDate), Stats(mine, today));
        }).ToList();

        var enteredBy = entered.GroupBy(e => e.CreatedByUserId).ToDictionary(g => g.Key, g => g.Count());
        var topUsers = scoped.Where(a => a.UserId != null).GroupBy(a => a.UserId!.Value).Select(g =>
        {
            var u = people.FirstOrDefault(p => p.Id == g.Key);
            return new UserReportRow(g.Key, u?.DisplayName ?? "(removed user)", u?.Username ?? "", u is null ? "" : RoleTitles.Of(u.Role), u?.GroupId,
                u?.GroupId is int gid ? groupNames.GetValueOrDefault(gid) : null, g.Sum(a => a.Count),
                g.Where(a => a.SignIn && a.Kind == InteractionKinds.Api).Sum(a => a.Count), g.Where(a => a.Kind == InteractionKinds.Page).Sum(a => a.Count),
                enteredBy.GetValueOrDefault(g.Key), Utc(g.Max(a => a.Last)));
        }).OrderByDescending(u => u.Interactions).Take(50).ToList();

        var enteredScoped = entered.Where(e => groupIdsInScope.Contains(e.GroupId)).ToList();
        var daily = scoped.GroupBy(a => (Day: DateOnly.FromDateTime(a.Day), a.GroupId))
            .Select(g => (g.Key.Day, g.Key.GroupId, Interactions: g.Sum(a => a.Count), Active: g.Where(a => a.UserId is int au && !a.System && accounts.Contains(au)).Select(a => a.UserId).Distinct().Count()))
            .ToList();
        var cardDays = enteredScoped.GroupBy(e => (Day: DateOnly.FromDateTime(e.CreatedAt), GroupId: (int?)e.GroupId)).ToDictionary(g => g.Key, g => g.Count());
        var dailyRows = daily.Select(d => new DailyReportRow(d.Day, d.GroupId, d.Interactions, d.Active, cardDays.GetValueOrDefault((d.Day, d.GroupId))))
            .Concat(cardDays.Where(k => !daily.Any(d => d.Day == k.Key.Day && d.GroupId == k.Key.GroupId))
                .Select(k => new DailyReportRow(k.Key.Day, k.Key.GroupId, 0, 0, k.Value)))
            .OrderBy(d => d.Date).ThenBy(d => d.GroupId).ToList();

        // People with access: the groups' own accounts (System Administrators are not part of any group's use).
        var scopedUsers = people.Where(u => u.IsActive && u.GroupId is int g && groupIdsInScope.Contains(g)).ToList();
        return new SystemReport(start, end, Stats(cards, today), groups.Count, restaurants.Count,
            rFilter.Count > 0 ? scopedUsers.Count(u => restaurantsOf.TryGetValue(u.Id, out var m) && m.Overlaps(restaurantIdSet)) : scopedUsers.Count,
            scoped.Where(a => a.UserId is int au && !a.System && accounts.Contains(au)).Select(a => a.UserId).Distinct().Count(),
            scoped.Where(a => a.SignIn && a.Kind == InteractionKinds.Api).Sum(a => a.Count),
            scoped.Where(a => a.Failed && a.Kind == InteractionKinds.Api).Sum(a => a.Count),
            scoped.Sum(a => a.Count), byGroup, byRestaurant, topUsers, dailyRows,
            scoped.GroupBy(a => a.Kind).Select(g => new CountItem(g.Key, g.Sum(a => a.Count))).OrderByDescending(c => c.Count).ToList());
    }

    private record CardRow(int RestaurantId, int GroupId, DateOnly VisitDate, bool Responded, DateOnly? RepliedOn, string? Source);

    private static CardStats Stats(IEnumerable<CardRow> rows, DateOnly today)
    {
        var list = rows.ToList();
        var replyDays = list.Where(c => c.Responded && c.RepliedOn is not null).Select(c => (double)Math.Max(0, c.RepliedOn!.Value.DayNumber - c.VisitDate.DayNumber)).ToList();
        return new CardStats(list.Count, list.Count(c => c.Responded), list.Count(c => !c.Responded),
            list.Count(c => !c.Responded && today.DayNumber - c.VisitDate.DayNumber > 7),
            replyDays.Count == 0 ? null : Math.Round(replyDays.Average(), 1),
            list.Count(c => c.Source == CommentSources.Smg), list.Count(c => c.Source == CommentSources.Website),
            list.Count(c => c.Source == CommentSources.Phone), list.Count(c => c.Source is null));
    }

    private static HashSet<int> Ids(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => int.TryParse(s, out var i) ? i : 0).Where(i => i > 0).ToHashSet();

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);

    /// <summary>Quotes a CSV field and stops spreadsheet apps treating user-supplied text as a formula.</summary>
    private static string Csv(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if ("=+-@".Contains(s[0])) s = "'" + s;
        return s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}

public class ActivityQuery
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    /// <summary>0 = rows not tied to any group (System Administrator oversight).</summary>
    public int? GroupId { get; set; }
    public int? RestaurantId { get; set; }
    public int? UserId { get; set; }
    public string? Kind { get; set; }
    public string? Search { get; set; }
    public bool? ErrorsOnly { get; set; }
    /// <summary>Follow one browser tab's visit.</summary>
    public string? SessionId { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
}
