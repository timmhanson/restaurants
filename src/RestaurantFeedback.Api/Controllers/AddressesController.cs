using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Services;

namespace RestaurantFeedback.Api.Controllers;

/// <summary>
/// Checks a guest's mailing address with USPS while a card is being entered, so a coupon card is not mailed to an
/// address that does not exist. Anyone who can enter cards may ask. The address travels in the request body, so it
/// is not written to the interaction log.
/// </summary>
[ApiController]
[Route("api/addresses")]
[Authorize]
public class AddressesController(IAddressVerifier verifier) : ControllerBase
{
    [HttpPost("check")]
    public async Task<AddressCheckResponse> Check(AddressCheckRequest req, CancellationToken ct)
    {
        if (!verifier.Configured) return new AddressCheckResponse(false, AddressStatuses.Unavailable, "", null);
        var result = await verifier.CheckAsync(new AddressInput(req.StreetAddress, req.City, req.State, req.Zip), ct);
        return new AddressCheckResponse(true, result.Status, result.Message, result.Suggested);
    }
}
