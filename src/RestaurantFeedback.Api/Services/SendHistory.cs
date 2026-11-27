using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RestaurantFeedback.Api.Auth;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Services;

public static class SendMethods
{
    /// <summary>Printed and mailed for the restaurant by the mailing service.</summary>
    public const string Mail = "mail";
    /// <summary>Printed in the restaurant, for a stamp by hand (or with postage bought here).</summary>
    public const string Stamp = "stamp";
}

public static class SendOutcomes
{
    public const string Sent = "sent", Refused = "refused", Cancelled = "cancelled";
}

/// <summary>
/// The history of every attempt to send a card (CardSend): when, how, by whom, to which address, and what came of
/// it. A card can be sent more than once (it never arrived, or the address was wrong), so the card itself only
/// says how its latest send stands; this is the whole story. Rows are added, and a mailed row's status is kept up
/// to date; nothing else on a row changes, so the address each attempt used stays on record whatever the card's
/// address is changed to later.
/// </summary>
public class SendHistory(AppDbContext db)
{
    /// <summary>Adds an attempt to the card's history, with the address as it is on the card at this moment.</summary>
    public async Task<CardSend> AddAsync(GuestComment c, CurrentUser me, string method, string outcome, bool resend, CancellationToken ct = default)
    {
        var earlier = await db.CardSends.CountAsync(s => s.CommentId == c.Id, ct);
        var send = new CardSend
        {
            CommentId = c.Id, Number = earlier + 1, At = DateTime.UtcNow, ByUserId = me.Id, ByName = me.DisplayName, Method = method, Outcome = outcome, Resend = resend,
            StreetAddress = c.StreetAddress, City = c.City, State = c.State, Zip = c.Zip, AddressStatus = c.AddressStatus
        };
        db.CardSends.Add(send);
        return send;
    }

    /// <summary>Whether the card has been sent before, by any way, and not cancelled.</summary>
    public Task<bool> SentBeforeAsync(int commentId, CancellationToken ct = default) =>
        db.CardSends.AnyAsync(s => s.CommentId == commentId && s.Outcome == SendOutcomes.Sent, ct);

    /// <summary>Brings the history row of a mailed card in line with the card's latest status from the mailing service.</summary>
    public async Task SyncStatusAsync(GuestComment c, CancellationToken ct = default)
    {
        if (c.MailId is null) return;
        foreach (var s in await db.CardSends.Where(s => s.CommentId == c.Id && s.MailId == c.MailId).ToListAsync(ct))
        {
            s.MailStatus = c.MailStatus;
            s.StatusCheckedAt = DateTime.UtcNow;
            if (c.MailStatus == MailStatuses.Cancelled) s.Outcome = SendOutcomes.Cancelled;
        }
    }
}

/// <summary>
/// Asks the mailing service where each card is, for the cards it has not finished with, so nobody has to press
/// "Check status". Run for everything by a background worker, and for a person's own restaurants when they open
/// Sent Postcards. A card is asked about at most once every ten minutes.
/// </summary>
public class MailStatusRefresher(AppDbContext db, SiteMailing site, IPostGridMailer mailer, SendHistory history, IMemoryCache cache, ILogger<MailStatusRefresher> log)
{
    /// <summary>Statuses that will not change again.</summary>
    public static bool IsFinal(string? status) => status is "completed" or MailStatuses.Cancelled;

    /// <param name="restaurantIds">Only these restaurants' cards; null for every restaurant.</param>
    /// <returns>How many cards' statuses changed.</returns>
    public async Task<int> RefreshAsync(IReadOnlyCollection<int>? restaurantIds, int most = 200, CancellationToken ct = default)
    {
        var q = db.Comments.Where(c => c.MailId != null && c.MailStatus != "completed" && c.MailStatus != MailStatuses.Cancelled);
        if (restaurantIds is not null) q = q.Where(c => restaurantIds.Contains(c.RestaurantId));
        var cards = await q.OrderBy(c => c.MailedAt).Take(most).ToListAsync(ct);
        var changed = 0;
        foreach (var c in cards)
        {
            if (cache.TryGetValue($"mail-status:{c.Id}", out _)) continue;
            // A card is asked about with the key it was sent with: the test key for a test order, the live key for a real one.
            if (site.For(!c.MailTest) is not { } account) continue;
            try
            {
                var now = await mailer.GetPostcardAsync(account.ApiKey, c.MailId!, ct);
                cache.Set($"mail-status:{c.Id}", true, TimeSpan.FromMinutes(10));
                if (now.Status == c.MailStatus) continue;
                c.MailStatus = now.Status;
                await history.SyncStatusAsync(c, ct);
                changed++;
            }
            catch (PostGridException ex)
            {
                log.LogWarning("The status of card {CardId} could not be fetched: {Message}", c.Id, ex.Message);
                if (!ex.Answered) break; // the mailing service cannot be reached: try again next time
            }
        }
        if (changed > 0) await db.SaveChangesAsync(ct);
        return changed;
    }
}

/// <summary>Keeps mailed cards' statuses up to date in the background, every half hour.</summary>
public class MailStatusWorker(IServiceScopeFactory scopes, ILogger<MailStatusWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var changed = await scope.ServiceProvider.GetRequiredService<MailStatusRefresher>().RefreshAsync(null, 500, stoppingToken);
                if (changed > 0) log.LogInformation("Mailing statuses brought up to date for {Count} cards", changed);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { log.LogError(ex, "Mailing statuses could not be brought up to date"); }
            try { await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
