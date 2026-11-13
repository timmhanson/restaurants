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
    /// <summary>
    /// Figures for one restaurant, a chosen set (restaurantIds=1,2,3), or every restaurant the user can see.
    /// Owners and managers with several restaurants get the roll-up plus a per-restaurant comparison.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<AnalyticsDto>> Get([FromQuery] int? restaurantId, [FromQuery] string? restaurantIds, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        var me = await users.GetAsync();
        if (!me.Has(ManagerPermission.ViewAnalytics)) return Forbid();
        List<int> ids;
        if (restaurantId is not null) ids = me.CanAccessRestaurant(restaurantId.Value) ? [restaurantId.Value] : [];
        else if (!string.IsNullOrWhiteSpace(restaurantIds))
        {
            var asked = restaurantIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x, out var v) ? v : 0).Distinct().ToList();
            if (asked.Any(r => !me.CanAccessRestaurant(r))) return Forbid();
            ids = asked;
        }
        else ids = me.RestaurantIds.ToList();
        if (ids.Count == 0) return Forbid();
        var restaurants = await db.Restaurants.Where(r => ids.Contains(r.Id) && r.GroupId == me.GroupId).OrderBy(r => r.Name).ToListAsync();

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

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        static double? AvgReplyDays(IEnumerable<GuestComment> cs)
        {
            var days = cs.Where(c => c.Responded && c.RepliedOn is not null)
                .Select(c => (double)Math.Max(0, c.RepliedOn!.Value.DayNumber - c.VisitDate.DayNumber)).ToList();
            return days.Count == 0 ? null : Math.Round(days.Average(), 1);
        }
        // Measured from the guest's visit: that is how long the guest has been waiting, however late the card was entered.
        int WaitingDays(GuestComment c) => Math.Max(0, today.DayNumber - c.VisitDate.DayNumber);

        var rollups = restaurants.Select(r =>
        {
            var mine = rows.Where(c => c.RestaurantId == r.Id).ToList();
            var guests = mine.GroupBy(c => c.GuestKey).ToList();
            var waiting = mine.Where(c => !c.Responded).ToList();
            double rWeeks = mine.Count == 0 ? 1 : Math.Max(1, (mine.Max(c => c.VisitDate).DayNumber - mine.Min(c => c.VisitDate).DayNumber) / 7.0 + 1);
            return new RestaurantRollup(r.Id, r.Name, mine.Count, mine.Count - waiting.Count, waiting.Count,
                mine.Count == 0 ? 0 : Math.Round(100.0 * (mine.Count - waiting.Count) / mine.Count, 1),
                guests.Count, guests.Count(g => g.Count() >= 2), Math.Round(mine.Count / rWeeks, 2), AvgReplyDays(mine),
                waiting.Count(c => WaitingDays(c) > 7), waiting.Count == 0 ? null : waiting.Max(WaitingDays),
                mine.GroupBy(c => c.Reason).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).FirstOrDefault(),
                mine.Count == 0 ? null : mine.Max(c => c.VisitDate));
        }).ToList();
        var monthly = rows.GroupBy(c => new { Month = c.VisitDate.ToString("yyyy-MM"), c.RestaurantId, c.Restaurant.Name })
            .OrderBy(g => g.Key.Month).ThenBy(g => g.Key.Name)
            .Select(g => new RestaurantMonth(g.Key.Month, g.Key.RestaurantId, g.Key.Name, g.Count())).ToList();

        return new AnalyticsDto(
            total, responded, total - responded, byGuest.Count, byGuest.Count(g => g.Count() >= 2), Math.Round(total / weeks, 2),
            days,
            Counts(rows, r => r.Reason),
            Counts(rows, r => string.IsNullOrWhiteSpace(r.CouponOffer) ? "(none)" : r.CouponOffer),
            Counts(rows, r => r.Restaurant.Name),
            byWeek, byMonth,
            Counts(rows, r => r.CreatedByName),
            topRepeat)
        {
            Scope = restaurants.Select(r => new NamedRef(r.Id, r.Name)).ToList(),
            Restaurants = rollups,
            MonthlyByRestaurant = monthly,
            CrossRestaurantGuests = byGuest.Count(g => g.Select(x => x.RestaurantId).Distinct().Count() > 1),
            AvgDaysToReply = AvgReplyDays(rows),
            WaitingOverAWeek = rows.Count(c => !c.Responded && WaitingDays(c) > 7),
            ByReplyMethod = Counts(rows.Where(c => c.Responded), c => c.ReplyMethod ?? "Not recorded"),
            BySource = Counts(rows, c => c.Source ?? "Not recorded"),
            Printed = rows.Count(c => c.PrintedAt != null),
            Redeemed = rows.Count(c => c.RedeemedOn != null),
            FollowedUp = rows.Count(c => c.FollowedUpOn != null),
            PostageCents = rows.Where(c => !c.PostageTest).Sum(c => c.PostageCents ?? 0)
        };
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
