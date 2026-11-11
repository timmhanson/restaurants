using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Auth;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Dtos;
using RestaurantFeedback.Api.Models;
using RestaurantFeedback.Api.Services;

namespace RestaurantFeedback.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(AppDbContext db, TokenService tokens, IPasswordHasher<AppUser> hasher, AuditService audit, PasswordCodeService codes) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest req)
    {
        var username = req.Username.Trim().ToLowerInvariant();
        var user = await db.Users.Include(u => u.UserRestaurants).Include(u => u.Group)
            .FirstOrDefaultAsync(u => u.Username == username);
        if (user is null || !user.IsActive || user.Group is { IsActive: false } ||
            hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password) == PasswordVerificationResult.Failed)
        {
            await audit.LogRawAsync(user?.Id, username, user?.Role, user?.GroupId, "LoginFailed");
            return Unauthorized(new { message = "Invalid username or password." });
        }

        user.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var (token, expires) = tokens.Create(user, user.UserRestaurants.Select(x => x.RestaurantId));
        await audit.LogRawAsync(user.Id, user.Username, user.Role, user.GroupId, "Login");
        return new LoginResponse(token, expires, UserDto.From(user), user.MustChangePassword);
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<UserDto>> Me()
    {
        var cu = new CurrentUser(User);
        var user = await db.Users.Include(u => u.UserRestaurants).Include(u => u.Group).FirstOrDefaultAsync(u => u.Id == cu.Id);
        return user is null || !user.IsActive ? Unauthorized() : UserDto.From(user);
    }

    /// <summary>Step 1 of a self-service password change: email a 6-digit code to the address on file.</summary>
    [HttpPost("change-password/request")]
    [Authorize]
    public async Task<ActionResult<CodeSentResponse>> RequestPasswordCode(CancellationToken ct)
    {
        var cu = new CurrentUser(User);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == cu.Id, ct);
        if (user is null || !user.IsActive) return Unauthorized();
        if (string.IsNullOrWhiteSpace(user.Email))
            return BadRequest(new { message = "There is no email address on file for your account. Ask an owner to add one before changing your password." });
        var wait = codes.SecondsUntilResend(user);
        if (wait > 0) return BadRequest(new { message = $"A code was sent recently. You can request another in {wait} seconds." });

        DateTime expires;
        try { expires = await codes.IssueAsync(user, ct); }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            await audit.LogAsync(cu, "PasswordCodeEmailFailed", "User", user.Id, new { error = ex.Message });
            return StatusCode(502, new { message = "We could not send the verification email right now. Please try again in a minute." });
        }
        await db.SaveChangesAsync(ct);
        await audit.LogAsync(cu, "PasswordCodeSent", "User", user.Id, new { email = AcsEmailSender.Mask(user.Email) });
        return new CodeSentResponse(AcsEmailSender.Mask(user.Email), expires, (int)PasswordCodeService.ResendCooldown.TotalSeconds);
    }

    /// <summary>
    /// Step 2: any signed-in user may change their own password with the current password plus the emailed code.
    /// Returns a fresh token (clears the must-change flag).
    /// </summary>
    [HttpPost("change-password")]
    [Authorize]
    public async Task<ActionResult<LoginResponse>> ChangePassword(ChangePasswordRequest req)
    {
        var cu = new CurrentUser(User);
        var user = await db.Users.Include(u => u.UserRestaurants).Include(u => u.Group).FirstOrDefaultAsync(u => u.Id == cu.Id);
        if (user is null || !user.IsActive) return Unauthorized();
        var (codeOk, codeError) = codes.Verify(user, req.Code);
        if (!codeOk)
        {
            await db.SaveChangesAsync(); // persist the attempt counter
            await audit.LogAsync(cu, "PasswordCodeRejected", "User", user.Id, new { reason = codeError });
            return BadRequest(new { message = codeError });
        }
        if (hasher.VerifyHashedPassword(user, user.PasswordHash, req.CurrentPassword) == PasswordVerificationResult.Failed)
            return BadRequest(new { message = "Current password is incorrect. Request a new code and try again." });
        if (req.NewPassword == req.CurrentPassword)
            return BadRequest(new { message = "The new password must be different from the current one. Request a new code and try again." });
        user.PasswordHash = hasher.HashPassword(user, req.NewPassword);
        user.MustChangePassword = false;
        await db.SaveChangesAsync();
        await audit.LogAsync(cu, "ChangePassword", "User", user.Id);
        var (token, expires) = tokens.Create(user, user.UserRestaurants.Select(x => x.RestaurantId));
        return new LoginResponse(token, expires, UserDto.From(user), false);
    }
}
