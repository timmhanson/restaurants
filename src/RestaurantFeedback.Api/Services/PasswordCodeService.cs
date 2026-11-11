using System.Security.Cryptography;
using System.Text;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Services;

/// <summary>Issues and verifies the 6-digit codes emailed before a user may change their own password.</summary>
public class PasswordCodeService(IEmailSender email, EmailOptions options, ILogger<PasswordCodeService> log)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);
    public const int MaxAttempts = 5;

    public int SecondsUntilResend(AppUser user)
    {
        if (user.PasswordCodeSentAt is null) return 0;
        var remaining = user.PasswordCodeSentAt.Value + ResendCooldown - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? (int)Math.Ceiling(remaining.TotalSeconds) : 0;
    }

    /// <summary>Generates a new code, stores its hash on the user and emails it. Caller saves the user.</summary>
    public async Task<DateTime> IssueAsync(AppUser user, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(user.Email)) throw new InvalidOperationException("No email on file.");
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var now = DateTime.UtcNow;
        user.PasswordCodeHash = Hash(user.Id, code);
        user.PasswordCodeExpiresAt = now + Lifetime;
        user.PasswordCodeSentAt = now;
        user.PasswordCodeAttempts = 0;

        var app = options.AppName;
        var text = $"Your {app} verification code is {code}.\n\nEnter it on the change-password screen within {Lifetime.TotalMinutes:0} minutes. " +
                   "If you did not ask to change your password, you can ignore this email.";
        var html = $"<p>Your <strong>{app}</strong> verification code is:</p>" +
                   $"<p style=\"font-size:28px;letter-spacing:6px;font-family:monospace\"><strong>{code}</strong></p>" +
                   $"<p>Enter it on the change-password screen within {Lifetime.TotalMinutes:0} minutes. If you did not ask to change your password, you can ignore this email.</p>";
        await email.SendAsync(user.Email, $"{app}: your verification code", html, text, ct);
        log.LogInformation("Password code issued for user {UserId}", user.Id);
        return user.PasswordCodeExpiresAt.Value;
    }

    /// <summary>Checks a submitted code. Mutates attempt counters; caller saves the user.</summary>
    public (bool ok, string? error) Verify(AppUser user, string code)
    {
        if (user.PasswordCodeHash is null || user.PasswordCodeExpiresAt is null) return (false, "Request a verification code first.");
        if (user.PasswordCodeExpiresAt < DateTime.UtcNow) return (false, "That code has expired. Request a new one.");
        if (user.PasswordCodeAttempts >= MaxAttempts) return (false, "Too many attempts. Request a new code.");
        user.PasswordCodeAttempts++;
        var expected = Encoding.UTF8.GetBytes(user.PasswordCodeHash);
        var actual = Encoding.UTF8.GetBytes(Hash(user.Id, code.Trim()));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return (false, "That code is not correct.");
        Clear(user);
        return (true, null);
    }

    public static void Clear(AppUser user)
    {
        user.PasswordCodeHash = null;
        user.PasswordCodeExpiresAt = null;
        user.PasswordCodeAttempts = 0;
    }

    private static string Hash(int userId, string code)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{userId}:{code}")));
}
