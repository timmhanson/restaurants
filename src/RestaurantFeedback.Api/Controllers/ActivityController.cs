using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RestaurantFeedback.Api.Auth;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Models;
using RestaurantFeedback.Api.Services;

namespace RestaurantFeedback.Api.Controllers;

/// <summary>
/// The browser reports page views, clicks and choices here in small batches. Anyone signed in may send them;
/// only System Administrators can read them back (see <see cref="SystemController"/>).
/// </summary>
[ApiController]
[Route("api/activity")]
[Authorize]
public class ActivityController(InteractionLogQueue queue, CurrentUserProvider users) : ControllerBase
{
    public const int MaxBatch = 100;

    [HttpPost]
    public async Task<IActionResult> Record(ActivityBatch batch)
    {
        // Only accounts that still exist and are active (a removed or suspended person's open tab gets 401).
        await users.GetAsync();
        var now = DateTime.UtcNow;
        foreach (var e in batch.Events.Take(MaxBatch))
        {
            var kind = InteractionKinds.FromBrowser.FirstOrDefault(k => k.Equals(e.Kind, StringComparison.OrdinalIgnoreCase));
            if (kind is null || string.IsNullOrWhiteSpace(e.Path)) continue;
            var row = InteractionLogQueue.Describe(HttpContext, kind);
            // Trust the browser's clock only within a day of ours (queued events can arrive a little late).
            row.At = e.At is { } at && Math.Abs((now - at.ToUniversalTime()).TotalHours) < 24 ? at.ToUniversalTime() : now;
            row.Path = InteractionLogQueue.Clip(e.Path, 400)!;
            row.Action = InteractionLogQueue.Clip(e.Label?.Trim(), 200);
            row.Detail = InteractionLogQueue.Clip(e.Detail?.Trim(), 500);
            if (e.RestaurantId is int rid) row.RestaurantId = rid;
            row.SessionId = InteractionLogQueue.Clip(batch.SessionId, 64) ?? row.SessionId;
            queue.Enqueue(row);
        }
        return NoContent();
    }
}
