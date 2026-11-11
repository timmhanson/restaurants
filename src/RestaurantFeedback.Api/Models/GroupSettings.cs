namespace RestaurantFeedback.Api.Models;

/// <summary>
/// Per-investment-group configuration. Stored as one JSON blob per group
/// (blob name "group-{id}.json") in Azure Blob Storage, not in the relational database.
/// </summary>
public class GroupSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string GroupDisplayName { get; set; } = "";
    public string AccentColor { get; set; } = "#005daa";

    /// <summary>Options for the "Please enjoy ___" line on the card.</summary>
    public List<string> CouponOffers { get; set; } =
    [
        "Free Value Basket", "Free Single ButterBurger", "Free Concrete Mixer", "Free Sundae", "Free Kids' Meal", "$5 off your next visit", "$10 off your next visit"
    ];

    /// <summary>Why the guest received a card. Drives the Reason dropdown and analytics.</summary>
    public List<string> ReasonCategories { get; set; } =
    [
        "Order mistake", "Missing item", "Wait time", "Food quality", "Food temperature", "Service", "Cleanliness", "Drive-thru", "Compliment", "Other"
    ];

    /// <summary>How a manager can reply to a guest. Drives the "How did you reply?" choice on each comment.</summary>
    public List<string> ReplyMethods { get; set; } =
    [
        "Mailed coupon card", "Phone call", "Email", "Text message", "In person"
    ];

    /// <summary>"This coupon is good for 3 months from date of issue."</summary>
    public int CouponValidityMonths { get; set; } = 3;

    /// <summary>A second card for the same guest within this many days of a visit is flagged as a likely duplicate entry.</summary>
    public int DuplicateWindowDays { get; set; } = 7;

    /// <summary>Show the repeat-guest alert once a guest has this many cards on file (including the new one).</summary>
    public int RepeatGuestAlertThreshold { get; set; } = 2;

    /// <summary>Require at least a phone number or a street address so guests can be matched.</summary>
    public bool RequirePhoneOrAddress { get; set; } = true;

    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}
