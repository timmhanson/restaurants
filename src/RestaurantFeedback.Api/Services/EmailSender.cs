using Azure;
using Azure.Communication.Email;
using Azure.Identity;

namespace RestaurantFeedback.Api.Services;

public class EmailOptions
{
    /// <summary>Azure Communication Services connection string (access key).</summary>
    public string? ConnectionString { get; set; }
    /// <summary>ACS endpoint, used with DefaultAzureCredential when no connection string is set.</summary>
    public string? Endpoint { get; set; }
    /// <summary>Verified sender, e.g. DoNotReply@xxxx.azurecomm.net</summary>
    public string Sender { get; set; } = "";
    public string AppName { get; set; } = "Guest Feedback";
}

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string html, string plainText, CancellationToken ct = default);
    string Description { get; }
}

/// <summary>Sends through Azure Communication Services Email.</summary>
public class AcsEmailSender : IEmailSender
{
    private readonly EmailClient _client;
    private readonly EmailOptions _options;
    private readonly ILogger<AcsEmailSender> _log;

    public AcsEmailSender(EmailOptions options, ILogger<AcsEmailSender> log)
    {
        _options = options;
        _log = log;
        if (string.IsNullOrWhiteSpace(options.Sender)) throw new InvalidOperationException("Email:Sender must be configured.");
        _client = !string.IsNullOrWhiteSpace(options.ConnectionString)
            ? new EmailClient(options.ConnectionString)
            : new EmailClient(new Uri(options.Endpoint!), new DefaultAzureCredential());
    }

    public string Description => $"Azure Communication Services ({_options.Sender})";

    public async Task SendAsync(string to, string subject, string html, string plainText, CancellationToken ct = default)
    {
        var message = new EmailMessage(_options.Sender, new EmailRecipients([new EmailAddress(to)]),
            new EmailContent(subject) { Html = html, PlainText = plainText });
        // Wait for ACS to accept the message (not for delivery); failures surface as exceptions to the caller.
        var op = await _client.SendAsync(WaitUntil.Started, message, ct);
        _log.LogInformation("Queued email {OperationId} to {Recipient}", op.Id, Mask(to));
    }

    public static string Mask(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 1) return "***" + email[Math.Max(at, 0)..];
        return email[0] + "***" + email[(at - 1)..];
    }
}

/// <summary>Development fallback: writes the email to the log instead of sending it.</summary>
public class LoggingEmailSender(ILogger<LoggingEmailSender> log) : IEmailSender
{
    public string Description => "Log only (no email service configured)";

    public Task SendAsync(string to, string subject, string html, string plainText, CancellationToken ct = default)
    {
        log.LogWarning("EMAIL (not sent) to {To}: {Subject}\n{Body}", to, subject, plainText);
        return Task.CompletedTask;
    }
}
