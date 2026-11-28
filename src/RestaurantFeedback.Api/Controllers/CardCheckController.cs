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
/// Checking a scanned coupon card. The QR code on a card leads here. Only someone signed in can ask, the code's
/// signature must match (so it cannot be made up or altered), and the card's details are shown only to people who
/// work at its restaurant. What comes back is the card as the site has it, so a card whose printed offer or dates
/// were changed by hand is caught by comparing it with the screen. Every check is written to the audit log.
/// </summary>
[ApiController]
[Route("api/cards")]
[Authorize]
public class CardCheckController(AppDbContext db, AuditService audit, CurrentUserProvider users, CardCodes codes) : ControllerBase
{
    [HttpGet("check/{code}")]
    public async Task<ActionResult<CardCheck>> Check(string code, CancellationToken ct)
    {
        var me = await users.GetAsync();
        if (codes.Read(code) is not { } cardId)
        {
            await audit.LogAsync(me, "CardCodeRejected", details: new { Reason = "The code is not one this site issued, or it was altered." });
            return new CardCheck(false, "This is not a genuine card code. It was not issued by this site, or it has been altered. Do not accept the coupon on the strength of it.", null, false);
        }
        var c = await db.Comments.AsNoTracking().Include(x => x.Restaurant).ThenInclude(r => r.Group).FirstOrDefaultAsync(x => x.PublicId == cardId, ct);
        if (c is null)
        {
            await audit.LogAsync(me, "CardCodeRejected", details: new { Reason = "The card's record no longer exists.", CardId = cardId });
            return new CardCheck(false, "This code was issued by this site, but the card's record has since been removed. Do not accept the coupon.", null, false);
        }
        if (!me.CanAccessRestaurant(c.RestaurantId))
        {
            // Genuine, but not this person's restaurant: say so without showing the guest or the offer.
            await audit.LogAsync(me, "CardCodeChecked", "Comment", c.Id, new { CardId = cardId, Shown = false }, c.RestaurantId, c.Restaurant.GroupId);
            return new CardCheck(true, me.IsSuperAdmin
                ? $"This is a genuine card from {c.Restaurant.Name} ({c.Restaurant.Group.Name}). Open that investment group with Impersonate to see it."
                : "This is a genuine card from this site, but it belongs to a restaurant you do not work at, so its details are not shown.", null, false);
        }
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var expired = c.RedeemedOn is null && today > c.ExpiresOn;
        var message = c.RedeemedOn is not null
            ? $"Genuine, but already redeemed on {c.RedeemedOn:MMM d, yyyy}{(string.IsNullOrWhiteSpace(c.RedeemedByName) ? "" : $" by {c.RedeemedByName}")}. Do not accept it again."
            : expired ? $"Genuine, but it expired on {c.ExpiresOn:MMM d, yyyy}."
            : "Genuine, and not redeemed yet. Check that the card in your hand says the same as below.";
        await audit.LogAsync(me, "CardCodeChecked", "Comment", c.Id, new { CardId = cardId, Shown = true, Redeemed = c.RedeemedOn is not null, Expired = expired }, c.RestaurantId);
        return new CardCheck(true, message,
            new CardCheckDetails(c.Id, c.PublicId, c.Restaurant.Name, $"{c.GuestFirstName} {c.GuestLastName}".Trim(), c.CouponOffer, c.DateIssued, c.ExpiresOn, expired,
                c.RedeemedOn, c.RedeemedByName, c.SentAt is not null, me.Has(ManagerPermission.ViewComments)),
            c.RedeemedOn is null && me.Has(ManagerPermission.MarkResponded));
    }
}
