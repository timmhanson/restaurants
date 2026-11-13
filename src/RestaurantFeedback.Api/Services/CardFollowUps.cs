using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Services;

/// <summary>
/// The rule for following up after a coupon card is sent: once a card has gone out by mail, the guest should be
/// contacted N days later to check it arrived (N per restaurant, defaulting to the group's setting, 5). No
/// follow-up is needed once it has been done, once the coupon is redeemed (it clearly arrived), or when N is 0.
/// </summary>
public class CardFollowUps(AppDbContext db, IGroupSettingsStore settings)
{
    public const string MailedMethod = "Mailed coupon card";

    /// <summary>Sent by mail: printed onto a postcard here, or replied to with a "mailed" method.</summary>
    public static bool WasMailed(GuestComment c) => c.PrintedAt != null || (c.ReplyMethod ?? "").Contains("mailed", StringComparison.OrdinalIgnoreCase);

    public static DateOnly? DueOn(GuestComment c, int days) =>
        days > 0 && c.Responded && c.RepliedOn is { } sent && c.FollowedUpOn is null && c.RedeemedOn is null && WasMailed(c) ? sent.AddDays(days) : null;

    /// <summary>Days for each restaurant: its own setting, else the group's default.</summary>
    public async Task<Dictionary<int, int>> DaysByRestaurantAsync(int? groupId, IEnumerable<int> restaurantIds, CancellationToken ct = default)
    {
        var ids = restaurantIds.Distinct().ToList();
        var fallback = groupId is int g ? (await settings.GetAsync(g, ct)).FollowUpDays : new GroupSettings().FollowUpDays;
        var own = await db.Restaurants.AsNoTracking().Where(r => ids.Contains(r.Id)).Select(r => new { r.Id, r.FollowUpDays }).ToListAsync(ct);
        return own.ToDictionary(r => r.Id, r => r.FollowUpDays ?? fallback);
    }

    /// <summary>Cards whose follow-up day has arrived, oldest first.</summary>
    public async Task<List<(int Id, DateOnly DueOn)>> DueAsync(int? groupId, IEnumerable<int> restaurantIds, CancellationToken ct = default)
    {
        var days = await DaysByRestaurantAsync(groupId, restaurantIds, ct);
        var active = days.Where(d => d.Value > 0).Select(d => d.Key).ToList();
        if (active.Count == 0) return [];
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var candidates = await db.Comments.AsNoTracking()
            .Where(c => active.Contains(c.RestaurantId) && c.Responded && c.RepliedOn != null && c.FollowedUpOn == null && c.RedeemedOn == null
                        && (c.PrintedAt != null || EF.Functions.Like(c.ReplyMethod ?? "", "%mailed%")))
            .Select(c => new { c.Id, c.RestaurantId, c.RepliedOn }).ToListAsync(ct);
        return candidates.Select(c => (c.Id, DueOn: c.RepliedOn!.Value.AddDays(days[c.RestaurantId])))
            .Where(c => c.DueOn <= today).OrderBy(c => c.DueOn).ToList();
    }
}

/// <summary>Builds the list/detail view of comments: follow-up counts, the latest note and the follow-up due date.</summary>
public class CommentSummaryBuilder(AppDbContext db, CardFollowUps followUps)
{
    public async Task<List<CommentSummary>> BuildAsync(int? groupId, List<GuestComment> items, Func<GuestComment, int> guestCount, CancellationToken ct = default)
    {
        var ids = items.Select(c => c.Id).ToList();
        var activity = await db.CommentActivities.Where(a => ids.Contains(a.CommentId) && (a.Kind == ActivityKind.Note || a.Kind == ActivityKind.Attempt)).ToListAsync(ct);
        var byComment = activity.GroupBy(a => a.CommentId).ToDictionary(g => g.Key, g => g.ToList());
        var days = await followUps.DaysByRestaurantAsync(groupId, items.Select(c => c.RestaurantId), ct);
        return items.Select(c =>
        {
            var mine = byComment.GetValueOrDefault(c.Id) ?? [];
            var latest = mine.Where(a => a.Kind == ActivityKind.Note).MaxBy(a => a.CreatedAt);
            return CommentSummary.From(c, guestCount(c)) with
            {
                NoteCount = mine.Count(a => a.Kind == ActivityKind.Note),
                AttemptCount = mine.Count(a => a.Kind == ActivityKind.Attempt),
                LatestNote = latest is null ? null : ActivityDto.From(latest),
                FollowUpDueOn = CardFollowUps.DueOn(c, days.GetValueOrDefault(c.RestaurantId))
            };
        }).ToList();
    }
}
