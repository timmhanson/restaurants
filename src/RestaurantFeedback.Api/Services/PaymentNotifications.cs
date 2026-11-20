using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Data;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Services;

/// <summary>What a notification from Stripe says, read out of its JSON. Everything else stays in the payload itself.</summary>
public record ReadNotification(string? EventId, string Type, DateTime? CreatedAt, bool LiveMode, string? ApiVersion, string? ObjectType, string? ObjectId, string? PaymentId,
    int? AmountCents, string? Currency, string? Status, string? Reason);

public record DataPoint(string Name, string Value);

/// <summary>
/// The log of everything Stripe tells the site (its webhook). Each notification is kept whole, once, after its
/// signature has been checked: every data point in it can be looked up later on the System Administrator's Billing
/// page. A notification is tied to the bill it is about when its payment is one of ours.
///
/// Stripe's notifications carry no card or bank account number (at most the last four digits and the brand), so
/// keeping them whole stays inside the rule that such numbers never touch the site. They do carry the payer's
/// name, email and billing address, so only System Administrators can read them, and they are never written to
/// the application log or the audit log.
/// </summary>
public class PaymentNotifications(AppDbContext db, AuditService audit)
{
    /// <summary>
    /// Keeps the notification. Stripe sends the same one again when it is unsure the site got it; that is counted
    /// on the row already kept, not kept twice.
    /// </summary>
    public async Task<StripeEvent> RecordAsync(string body, CancellationToken ct = default)
    {
        var read = Read(body);
        var known = read.EventId is null ? null : await db.StripeEvents.FirstOrDefaultAsync(e => e.EventId == read.EventId, ct);
        if (known is not null)
        {
            known.Deliveries++;
            known.LastReceivedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return known;
        }
        var invoice = read.PaymentId is null ? null : await db.MailInvoices.AsNoTracking().FirstOrDefaultAsync(i => i.PaymentRef == read.PaymentId, ct);
        var row = new StripeEvent
        {
            EventId = read.EventId ?? $"none-{Guid.NewGuid():N}", Type = read.Type, CreatedAt = read.CreatedAt, LiveMode = read.LiveMode, ApiVersion = read.ApiVersion,
            ObjectType = read.ObjectType, ObjectId = read.ObjectId, PaymentId = read.PaymentId, AmountCents = read.AmountCents, Currency = read.Currency, Status = read.Status,
            Reason = read.Reason, InvoiceId = invoice?.Id, GroupId = invoice?.GroupId, Payload = body
        };
        db.StripeEvents.Add(row);
        await db.SaveChangesAsync(ct);
        // The audit row says that a notification came and what it was about, without the payload.
        await audit.LogRawAsync(null, "Stripe", null, invoice?.GroupId, "PaymentNotificationReceived", "StripeEvent", row.Id,
            new { row.Type, Bill = invoice?.Number, row.AmountCents, row.Status, row.Reason, Test = !row.LiveMode });
        return row;
    }

    /// <summary>Notes what the site did about a notification.</summary>
    public async Task OutcomeAsync(StripeEvent row, string outcome, CancellationToken ct = default)
    {
        row.Outcome = outcome;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A notification in plain words for the Billing page.</summary>
    public static string Label(string type) => type switch
    {
        "payment_intent.succeeded" => "Payment arrived",
        "payment_intent.payment_failed" => "Payment failed",
        "payment_intent.canceled" => "Payment cancelled",
        "payment_intent.processing" => "Payment on its way",
        "charge.refunded" => "Payment refunded",
        "charge.dispute.created" => "Payment disputed",
        "charge.dispute.closed" => "Dispute closed",
        "charge.succeeded" => "Charge succeeded",
        "charge.failed" => "Charge failed",
        _ => type.Replace('_', ' ').Replace('.', ' ')
    };

    /// <summary>Refunds and disputes: money that came in has gone, or may go, back out, so someone should look.</summary>
    public static bool NeedsALook(string type) => type is "charge.refunded" or "charge.dispute.created";

    public static ReadNotification Read(string body)
    {
        try
        {
            var root = JsonDocument.Parse(body).RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Unreadable;
            var type = Text(root, "type") ?? "unknown";
            DateTime? created = root.TryGetProperty("created", out var c)
                ? c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out var unix) ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                    : c.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(c.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var iso) ? iso.UtcDateTime : null
                : null;
            var live = root.TryGetProperty("livemode", out var l) && l.ValueKind == JsonValueKind.True;
            // The whole object comes under data.object; a "thin" notification names it under related_object instead.
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("object", out var o) && o.ValueKind == JsonValueKind.Object)
            {
                var kind = Text(o, "object");
                var payment = kind == "payment_intent" ? Text(o, "id") : Text(o, "payment_intent");
                var amount = kind switch { "charge" when type == "charge.refunded" => Int(o, "amount_refunded") ?? Int(o, "amount"), _ => Int(o, "amount") };
                var reason = Text(o, "failure_message") ?? Nested(o, "last_payment_error", "message") ?? Text(o, "cancellation_reason") ?? (kind == "dispute" ? Text(o, "reason") : null);
                return new(Text(root, "id"), type, created, live, Text(root, "api_version"), kind, Text(o, "id"), payment, amount, Text(o, "currency"), Text(o, "status"), reason);
            }
            if (root.TryGetProperty("related_object", out var r) && r.ValueKind == JsonValueKind.Object)
            {
                var kind = Text(r, "type");
                return new(Text(root, "id"), type, created, live, null, kind, Text(r, "id"), (kind ?? "").EndsWith("payment_intent", StringComparison.Ordinal) ? Text(r, "id") : null, null, null, null, null);
            }
            return new(Text(root, "id"), type, created, live, Text(root, "api_version"), null, null, null, null, null, null, null);
        }
        catch (JsonException) { return Unreadable; }
    }

    private static readonly ReadNotification Unreadable = new(null, "unreadable", null, false, null, null, null, null, null, null, null, null);

    /// <summary>Every data point in a notification, one to a line: "data.object.amount" = "1250". Nothing is left out.</summary>
    public static List<DataPoint> DataPoints(string payload)
    {
        var list = new List<DataPoint>();
        try { Walk(JsonDocument.Parse(payload).RootElement, "", list); }
        catch (JsonException) { list.Add(new("(not JSON)", payload)); }
        return list;
    }

    private static void Walk(JsonElement e, string path, List<DataPoint> list)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var any = false;
                foreach (var p in e.EnumerateObject()) { any = true; Walk(p.Value, path.Length == 0 ? p.Name : $"{path}.{p.Name}", list); }
                if (!any) list.Add(new(path, "{}"));
                break;
            case JsonValueKind.Array:
                var n = 0;
                foreach (var item in e.EnumerateArray()) Walk(item, $"{path}[{n++}]", list);
                if (n == 0) list.Add(new(path, "[]"));
                break;
            case JsonValueKind.String: list.Add(new(path, e.GetString() ?? "")); break;
            case JsonValueKind.Null: list.Add(new(path, "(none)")); break;
            default: list.Add(new(path, e.GetRawText())); break;
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    private static string? Nested(JsonElement e, string outer, string inner) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(outer, out var v) ? Text(v, inner) : null;
}
