using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Auth;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Controllers;

[ApiController]
[Route("api/analytics")]
[Authorize]
public class AnalyticsController(AppDbContext db, CurrentUserProvider users) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AnalyticsDto>> Get([FromQuery] int? restaurantId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.ViewAnalytics)) return Forbid();
        List<int> ids = restaurantId is null ? me.RestaurantIds.ToList() : me.CanAccessRestaurant(restaurantId.Value) ? [restaurantId.Value] : [];
        if (ids.Count == 0) return Forbid();

        var q = db.Comments.Include(c => c.Restaurant).Where(c => ids.Contains(c.RestaurantId));
        if (from is not null) q = q.Where(c => c.VisitDate >= from);
        if (to is not null) q = q.Where(c => c.VisitDate <= to);
        var rows = await q.ToListAsync();

        var total = rows.Count;
        var responded = rows.Count(r => r.Responded);
        var byGuest = rows.GroupBy(r => r.GuestKey).ToList();
        var days = Enum.GetValues<DayOfWeek>().Select(d => new CountItem(d.ToString(), rows.Count(r => r.VisitDate.DayOfWeek == d), rows.Count(r => r.VisitDate.DayOfWeek == d && r.Responded))).ToList();
        // Start the week on Monday to match restaurant scheduling.
        var byWeek = rows.GroupBy(r => StartOfWeek(r.VisitDate)).OrderBy(g => g.Key)
            .Select(g => new CountItem(g.Key.ToString("yyyy-MM-dd"), g.Count(), g.Count(x => x.Responded))).ToList();
        var byMonth = rows.GroupBy(r => new DateOnly(r.VisitDate.Year, r.VisitDate.Month, 1)).OrderBy(g => g.Key)
            .Select(g => new CountItem(g.Key.ToString("yyyy-MM"), g.Count(), g.Count(x => x.Responded))).ToList();
        double weeks = rows.Count == 0 ? 1 : Math.Max(1, (rows.Max(r => r.VisitDate).DayNumber - rows.Min(r => r.VisitDate).DayNumber) / 7.0 + 1);

        var topRepeat = byGuest.Where(g => g.Count() >= 2).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(x => x.VisitDate)).Take(25)
            .Select(g =>
            {
                var latest = g.OrderByDescending(x => x.VisitDate).First();
                return new RepeatGuestItem(g.Key, $"{latest.GuestFirstName} {latest.GuestLastName}", latest.Phone,
                    string.Join(", ", new[] { latest.StreetAddress, latest.City }.Where(s => !string.IsNullOrWhiteSpace(s))),
                    g.Count(), latest.VisitDate, g.Select(x => x.Restaurant.Name).Distinct().ToArray(), g.Select(x => x.Reason).Distinct().ToArray());
            }).ToList();

        return new AnalyticsDto(
            total, responded, total - responded, byGuest.Count, byGuest.Count(g => g.Count() >= 2), Math.Round(total / weeks, 2),
            days,
            Counts(rows, r => r.Reason),
            Counts(rows, r => string.IsNullOrWhiteSpace(r.CouponOffer) ? "(none)" : r.CouponOffer),
            Counts(rows, r => r.Restaurant.Name),
            byWeek, byMonth,
            Counts(rows, r => r.CreatedByName),
            topRepeat);
    }

    private static List<CountItem> Counts(IEnumerable<GuestComment> rows, Func<GuestComment, string> key) =>
        rows.GroupBy(key).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => new CountItem(g.Key, g.Count(), g.Count(x => x.Responded))).ToList();

    private static DateOnly StartOfWeek(DateOnly d)
    {
        var diff = ((int)d.DayOfWeek + 6) % 7;
        return d.AddDays(-diff);
    }
}
