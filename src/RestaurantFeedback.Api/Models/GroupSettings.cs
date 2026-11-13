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

    /// <summary>
    /// Days after a coupon card is sent before the guest should be followed up with (did it arrive?).
    /// Each restaurant can override this; 0 turns the reminders off.
    /// </summary>
    public int FollowUpDays { get; set; } = 5;

    /// <summary>How coupon cards are printed onto postcards.</summary>
    public PostcardSettings Postcard { get; set; } = new();

    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// Postcard printing. "Preprinted" stock already carries the card's wording, so only the blanks are printed at
/// the positions below; "Blank" stock gets the whole card. Positions are inches from the card's top-left corner.
/// </summary>
public class PostcardSettings
{
    public const string Preprinted = "Preprinted";
    public const string Blank = "Blank";
    public static readonly string[] FieldKeys = ["offer", "dateIssued", "goodUntil", "authorizedBy", "guestName", "street", "cityStateZip"];

    public string Stock { get; set; } = Blank;
    public double WidthIn { get; set; } = 6;
    public double HeightIn { get; set; } = 4;
    /// <summary>Moves everything right (+) or left (-), to line up with a particular printer.</summary>
    public double NudgeXIn { get; set; }
    /// <summary>Moves everything down (+) or up (-).</summary>
    public double NudgeYIn { get; set; }
    public double FontPt { get; set; } = 11;
    /// <summary>Let people with the "buy postage" permission buy USPS postage when printing (stamps by hand always work).</summary>
    public bool AllowBoughtPostage { get; set; } = true;
    /// <summary>Where each blank is printed on pre-printed stock.</summary>
    public Dictionary<string, PostcardField> Fields { get; set; } = Defaults();

    public static Dictionary<string, PostcardField> Defaults() => new()
    {
        ["offer"] = new(0.95, 1.20),
        ["dateIssued"] = new(1.15, 1.95),
        ["goodUntil"] = new(1.15, 2.30) { Show = false },
        ["authorizedBy"] = new(1.60, 2.75),
        ["guestName"] = new(3.45, 1.70),
        ["street"] = new(3.45, 2.10),
        ["cityStateZip"] = new(3.45, 2.50)
    };

    /// <summary>Keeps saved values within what a printer can do and makes sure every blank has a position.</summary>
    public void Normalize()
    {
        Stock = Stock == Preprinted ? Preprinted : Blank;
        WidthIn = Math.Clamp(WidthIn, 3, 11);
        HeightIn = Math.Clamp(HeightIn, 3, 9);
        NudgeXIn = Math.Clamp(NudgeXIn, -1, 1);
        NudgeYIn = Math.Clamp(NudgeYIn, -1, 1);
        FontPt = Math.Clamp(FontPt, 7, 18);
        var defaults = Defaults();
        Fields ??= defaults;
        foreach (var key in FieldKeys)
        {
            if (!Fields.TryGetValue(key, out var f) || f is null) Fields[key] = f = defaults[key];
            f.X = Math.Clamp(f.X, 0, WidthIn);
            f.Y = Math.Clamp(f.Y, 0, HeightIn);
        }
        foreach (var extra in Fields.Keys.Except(FieldKeys).ToList()) Fields.Remove(extra);
    }
}

public class PostcardField
{
    public PostcardField() { }
    public PostcardField(double x, double y) { X = x; Y = y; }
    public double X { get; set; }
    public double Y { get; set; }
    public bool Show { get; set; } = true;
}
