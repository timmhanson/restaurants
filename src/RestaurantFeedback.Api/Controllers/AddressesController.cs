using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Auth;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Services;

namespace RestaurantFeedback.Api.Controllers;

/// <summary>
/// Checks a guest's mailing address with USPS while a card is being entered, so a coupon card is not mailed to an
/// address that does not exist, and offers addresses to pick as the street is typed. Anyone who can enter cards may
/// ask. The address travels in the request body, so it is not written to the interaction log.
/// </summary>
[ApiController]
[Route("api/addresses")]
[Authorize]
public class AddressesController(IAddressVerifier verifier, IAddressSuggester suggester, AppDbContext db, CurrentUserProvider users, SiteMailing site) : ControllerBase
{
    [HttpPost("check")]
    public async Task<AddressCheckResponse> Check(AddressCheckRequest req, CancellationToken ct)
    {
        if (!verifier.Configured) return new AddressCheckResponse(false, AddressStatuses.Unavailable, "", null);
        var result = await verifier.CheckAsync(new AddressInput(req.StreetAddress, req.City, req.State, req.Zip), ct);
        return new AddressCheckResponse(true, result.Status, result.Message, result.Suggested);
    }

    /// <summary>
    /// Addresses that begin the way the street has been typed so far, for the person entering the card to pick
    /// from. Offered for restaurants that have their cards printed and mailed for them (the address matters most
    /// there), when the site has an address service that can. Offering uses no paid lookup; the address picked is
    /// then checked like any other. Nothing typed is logged or kept.
    /// </summary>
    [HttpPost("suggest")]
    public async Task<AddressSuggestResponse> Suggest(AddressSuggestRequest req, CancellationToken ct)
    {
        var none = new AddressSuggestResponse(false, []);
        var me = await users.GetAsync();
        if (me.GroupId is not int groupId || !me.CanAccessRestaurant(req.RestaurantId)) return none;
        if (!suggester.Configured) return none;
        var restaurant = await db.Restaurants.AsNoTracking().FirstOrDefaultAsync(r => r.Id == req.RestaurantId && r.GroupId == groupId, ct);
        if (restaurant is null) return none;
        var group = await db.GroupBilling.AsNoTracking().FirstOrDefaultAsync(b => b.GroupId == groupId, ct);
        if (!GroupPolicy.For(site.Billing, group, restaurant).MailOn) return none;
        var found = await suggester.SuggestAsync(new AddressInput(req.StreetAddress, req.City, req.State, req.Zip), restaurant.State, ct);
        return new AddressSuggestResponse(true, found);
    }
}
