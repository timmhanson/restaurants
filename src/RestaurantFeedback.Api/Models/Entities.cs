namespace RestaurantFeedback.Api.Models;

/// <summary>
/// Roles, from widest to narrowest. Stored as numbers, so new roles are appended rather than inserted.
/// Display titles live in RoleTitles.
/// </summary>
public enum UserRole
{
    /// <summary>System Administrator: administers every investment group. Not a member of any group.</summary>
    SuperAdmin = 0,
    /// <summary>Restaurant Owner: everything at the restaurants they own, including the people who work there.</summary>
    Owner = 1,
    /// <summary>Manager: works at one or more restaurants, with the permissions the owners grant.</summary>
    Manager = 2,
    /// <summary>Team Member: a shared login that can only enter guest cards.</summary>
    TeamMember = 3,
    /// <summary>Investment Group Administrator: everything in one investment group, including every user account.</summary>
    GroupAdmin = 4
}

public static class RoleTitles
{
    /// <summary>The title with "a" or "an" in front, e.g. "an Investment Group Administrator".</summary>
    public static string WithArticle(UserRole role) => ("AEIOU".Contains(Of(role)[0]) ? "an " : "a ") + Of(role);

    public static string Of(UserRole role) => role switch
    {
        UserRole.SuperAdmin => "System Administrator",
        UserRole.GroupAdmin => "Investment Group Administrator",
        UserRole.Owner => "Restaurant Owner",
        UserRole.Manager => "Manager",
        UserRole.TeamMember => "Team Member",
        _ => role.ToString()
    };

    /// <summary>Roles that hold every manager permission automatically.</summary>
    public static bool HasAllPermissions(UserRole role) => role is UserRole.SuperAdmin or UserRole.GroupAdmin or UserRole.Owner;
}

/// <summary>Fine-grained permissions an owner can grant to a manager. Owners always hold all of them.</summary>
[Flags]
public enum ManagerPermission
{
    None = 0,
    ViewComments = 1,
    EditComments = 2,
    DeleteComments = 4,
    MarkResponded = 8,
    ViewAnalytics = 16,
    ExportComments = 32,
    ViewAudit = 64,
    ManageTeamLogin = 128,
    /// <summary>Spend money: buy USPS postage when printing a guest's coupon card.</summary>
    BuyPostage = 256,
    /// <summary>Change how many days after a card is sent the guest should be followed up with, at the manager's restaurants.</summary>
    SetFollowUpDays = 512,
    All = ViewComments | EditComments | DeleteComments | MarkResponded | ViewAnalytics | ExportComments | ViewAudit | ManageTeamLogin | BuyPostage | SetFollowUpDays
}

/// <summary>An investment / ownership group. The tenant boundary for users and restaurants.</summary>
public class OwnerGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // The group's own EasyPost account, so it pays for its own postage. The key is stored encrypted
    // (see GroupPostageAccounts) and never leaves the server; only its last four characters are shown.
    public string? PostageKeyEncrypted { get; set; }
    public string? PostageKeyEnding { get; set; }
    public bool PostageKeyIsTest { get; set; }
    public DateTime? PostageKeySetAt { get; set; }
    public string? PostageKeySetBy { get; set; }

    public List<Restaurant> Restaurants { get; set; } = [];
    public List<AppUser> Users { get; set; } = [];
}

public class Restaurant
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public OwnerGroup Group { get; set; } = null!;
    public string Name { get; set; } = "";
    public string? StreetAddress { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip { get; set; }
    public string? Phone { get; set; }
    /// <summary>
    /// The name that signs cards entered with this restaurant's shared team login (its manager or owner, usually).
    /// Managers, owners and administrators sign the cards they enter with their own name instead. See CardSignature.
    /// </summary>
    public string? SignatureName { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>
    /// Days after a coupon card is sent before the guest should be called to check it arrived.
    /// Null uses the investment group's default (5); 0 turns the reminders off for this restaurant.
    /// </summary>
    public int? FollowUpDays { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<UserRestaurant> UserRestaurants { get; set; } = [];
    public List<GuestComment> Comments { get; set; } = [];
}

public class AppUser
{
    public int Id { get; set; }
    /// <summary>Null for super admins, who belong to no single group.</summary>
    public int? GroupId { get; set; }
    public OwnerGroup? Group { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Email { get; set; }
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; }
    /// <summary>Only meaningful for managers. Owners implicitly have everything; team members have nothing.</summary>
    public ManagerPermission Permissions { get; set; } = ManagerPermission.None;
    public bool IsActive { get; set; } = true;
    /// <summary>Set when an admin resets the password; the user must pick a new one at next login. Never set for team logins.</summary>
    public bool MustChangePassword { get; set; }

    // Email round-trip for self-service password changes (6-digit code, hashed at rest).
    public string? PasswordCodeHash { get; set; }
    public DateTime? PasswordCodeExpiresAt { get; set; }
    public DateTime? PasswordCodeSentAt { get; set; }
    public int PasswordCodeAttempts { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public List<UserRestaurant> UserRestaurants { get; set; } = [];
}

/// <summary>Many-to-many link between users (owners, managers, team logins) and restaurants.</summary>
public class UserRestaurant
{
    public int UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public int RestaurantId { get; set; }
    public Restaurant Restaurant { get; set; } = null!;
}

/// <summary>One guest complaint / coupon postcard, modelled on the Culver's guest card.</summary>
public class GuestComment
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    public Restaurant Restaurant { get; set; } = null!;

    // Guest (postcard right-hand side)
    public string GuestFirstName { get; set; } = "";
    public string GuestLastName { get; set; } = "";
    public string? StreetAddress { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip { get; set; }
    public string? Phone { get; set; }

    // Coupon (postcard left-hand side)
    /// <summary>"Please enjoy ___ the next time you visit us" - what was sent to the guest.</summary>
    public string? CouponOffer { get; set; }
    public DateOnly DateIssued { get; set; }
    /// <summary>Coupon is good for 3 months from date of issue.</summary>
    public DateOnly ExpiresOn { get; set; }
    public string? AuthorizedBy { get; set; }

    /// <summary>How the comment reached the restaurant: one of <see cref="CommentSources.All"/>. Null on cards entered before it was asked.</summary>
    public string? Source { get; set; }

    // What happened
    public DateOnly VisitDate { get; set; }
    /// <summary>Reason category. The allowed list lives in the group's settings blob.</summary>
    public string Reason { get; set; } = "";
    public string? ReasonDetails { get; set; }
    public string? Notes { get; set; }

    // Follow-up. The full history (attempts, replies, notes) lives in CommentActivity; these are the latest reply.
    public bool Responded { get; set; }
    /// <summary>When the reply was confirmed in the app.</summary>
    public DateTime? RespondedAt { get; set; }
    public int? RespondedByUserId { get; set; }
    public string? RespondedByName { get; set; }
    /// <summary>How the guest was replied to, e.g. "Mailed coupon card" or "Phone call".</summary>
    public string? ReplyMethod { get; set; }
    /// <summary>The day the reply was actually sent or made (may be earlier than RespondedAt).</summary>
    public DateOnly? RepliedOn { get; set; }
    /// <summary>Last note, attempt or reply, so lists can show what is new.</summary>
    public DateTime? LastActivityAt { get; set; }

    /// <summary>What USPS said about the guest's mailing address when the card was saved (see AddressStatuses); null when not checked.</summary>
    public string? AddressStatus { get; set; }
    public DateTime? AddressCheckedAt { get; set; }

    // Printing the coupon card onto a postcard
    public DateTime? PrintedAt { get; set; }
    public string? PrintedByName { get; set; }
    public int PrintCount { get; set; }

    // Postage bought for the card (null when a stamp goes on by hand). Money is kept in cents.
    public string? PostageProvider { get; set; }
    public string? PostageId { get; set; }
    public int? PostageCents { get; set; }
    public string? PostageLabelUrl { get; set; }
    public string? PostageTrackingCode { get; set; }
    public DateTime? PostagePurchasedAt { get; set; }
    public string? PostageByName { get; set; }
    /// <summary>Bought with the provider's test key: a sample label that is not valid postage.</summary>
    public bool PostageTest { get; set; }

    /// <summary>
    /// The card's own random identifier, unique across every investment group. The QR code printed on the card
    /// carries it (signed, see CardCodes), so a scanned card leads back to exactly this record.
    /// </summary>
    public Guid PublicId { get; set; } = Guid.NewGuid();

    // Printed and mailed for us by a mail house (PostGrid), instead of printed at the restaurant
    public string? MailProvider { get; set; }
    public string? MailId { get; set; }
    /// <summary>The mail house's status: ready (can still be cancelled), printing, processed_for_delivery, completed or cancelled.</summary>
    public string? MailStatus { get; set; }
    public DateTime? MailedAt { get; set; }
    public string? MailedByName { get; set; }
    /// <summary>Sent with the mail house's test key: nothing is printed, mailed or charged.</summary>
    public bool MailTest { get; set; }
    /// <summary>What the mail house was expected to charge the site for this card when it was sent (it does not report its price per card).</summary>
    public int? MailEstimateCents { get; set; }
    /// <summary>The site's own fee for this card when it was sent. The investment group is billed MailEstimateCents + MailFeeCents.</summary>
    public int? MailFeeCents { get; set; }
    /// <summary>Times this card has been sent to the mail house; part of the key that stops one request mailing it twice.</summary>
    public int MailAttempts { get; set; }

    // The guest came back and used the coupon
    public DateOnly? RedeemedOn { get; set; }
    public DateTime? RedeemedAt { get; set; }
    public string? RedeemedByName { get; set; }

    // Checking with the guest, some days after the card was sent, that it arrived
    public DateOnly? FollowedUpOn { get; set; }
    public DateTime? FollowedUpAt { get; set; }
    public string? FollowedUpByName { get; set; }

    // Duplicate detection keys (derived, see GuestMatcher)
    public string GuestKey { get; set; } = "";
    public string? PhoneNormalized { get; set; }
    public string NameNormalized { get; set; } = "";

    // Bookkeeping
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int CreatedByUserId { get; set; }
    public string CreatedByName { get; set; } = "";
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedByUserId { get; set; }
    public bool IsDeleted { get; set; }
}

public enum ActivityKind
{
    /// <summary>A note for the other managers. Does not change the reply status.</summary>
    Note = 0,
    /// <summary>Tried to reach the guest without completing the reply, e.g. left a voicemail.</summary>
    Attempt = 1,
    /// <summary>The manager confirmed the guest was replied to.</summary>
    Replied = 2,
    /// <summary>The reply was undone and the comment needs a reply again.</summary>
    Reopened = 3
    ,
    /// <summary>The coupon card was printed onto a postcard.</summary>
    Printed = 4,
    /// <summary>USPS postage was bought for the card.</summary>
    PostageBought = 5,
    /// <summary>The guest used the coupon.</summary>
    Redeemed = 6,
    /// <summary>A redemption recorded by mistake was taken back.</summary>
    RedemptionUndone = 7,
    /// <summary>Spoke with the guest after the card was sent (did it arrive?). Notes hold the conversation.</summary>
    FollowedUp = 8,
    /// <summary>Tried to follow up but did not reach the guest; the follow-up stays due.</summary>
    FollowUpAttempt = 9,
    /// <summary>Sent to the mail house (PostGrid), which prints and mails the card.</summary>
    Mailed = 10,
    /// <summary>A mailing was cancelled before it printed; the card is waiting to be sent again.</summary>
    MailCancelled = 11
}

/// <summary>
/// How one investment group is billed for the cards the site mails for it: its own fee and monthly limit where they
/// differ from the site's, where its bills go, and the payment method it has saved with the payment service.
/// </summary>
public class GroupBilling
{
    public int GroupId { get; set; }
    /// <summary>The site's fee per card for this group, in cents. Null uses the site's default.</summary>
    public int? FeeCents { get; set; }
    /// <summary>The most this group may be billed for mailed cards in a calendar month, in cents. Null uses the site's default; 0 switches mailing off.</summary>
    public int? MonthlyLimitCents { get; set; }
    public string? BillingName { get; set; }
    /// <summary>Where bills are emailed. Empty sends them to the group's administrators.</summary>
    public string? BillingEmail { get; set; }
    public string? Street { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip { get; set; }

    // The payment service's records for this group. No card or bank number is ever stored here: only the payment
    // service's own references and a label such as "Visa credit card ending 4242".
    public string? PayCustomerId { get; set; }
    public string? PayMethodId { get; set; }
    /// <summary>card or bank.</summary>
    public string? PayKind { get; set; }
    /// <summary>For a card: credit, debit or prepaid. The card fee applies to credit cards only.</summary>
    public string? PayFunding { get; set; }
    public string? PayLabel { get; set; }
    public string? PayMandateId { get; set; }
    public DateTime? PaySetAt { get; set; }
    public string? PaySetBy { get; set; }
    /// <summary>Charge the saved payment method automatically when a bill is issued.</summary>
    public bool AutoPay { get; set; }
}

public static class InvoiceStatuses
{
    public const string Open = "open", Processing = "processing", Paid = "paid", Void = "void";
}

/// <summary>
/// One month's bill to one investment group for mailed cards, frozen when it is issued: the cards, what the mail
/// house cost the site, the site's fee, and how and when it was paid.
/// </summary>
public class MailInvoice
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    /// <summary>yyyy-MM. One bill per group per month.</summary>
    public string Month { get; set; } = "";
    public string Number { get; set; } = "";
    public int Cards { get; set; }
    public int CostCents { get; set; }
    public int FeeCents { get; set; }
    /// <summary>What the group owes: CostCents + FeeCents.</summary>
    public int TotalCents { get; set; }
    /// <summary>Per-restaurant lines as JSON: [{restaurantId, restaurant, cards, cents}].</summary>
    public string LinesJson { get; set; } = "[]";
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public DateOnly DueOn { get; set; }
    public string Status { get; set; } = InvoiceStatuses.Open;
    public DateTime? PaidAt { get; set; }
    /// <summary>bank, card, check, cash or other.</summary>
    public string? PaidHow { get; set; }
    public string? PaidNote { get; set; }
    public string? PaidBy { get; set; }
    /// <summary>The credit card fee added when it was paid by credit card, on top of TotalCents.</summary>
    public int SurchargeCents { get; set; }
    /// <summary>The payment service's reference for the payment, and how many payments have been tried (part of the key that stops a double charge).</summary>
    public string? PaymentRef { get; set; }
    public int PaymentAttempts { get; set; }
    public string? PaymentError { get; set; }
    public DateTime? EmailedAt { get; set; }
    public string? EmailedTo { get; set; }
    /// <summary>The mail house's reference for the printed statement, when one was posted.</summary>
    public string? PrintedId { get; set; }
    public DateTime? PrintedAt { get; set; }
    public string? DeliveryError { get; set; }
}

/// <summary>
/// How the coupon cards a mail house prints look: one for the investment group (RestaurantId null, the default for
/// every restaurant) and optionally one for a restaurant, which then replaces the group's for that restaurant.
/// </summary>
public class CardDesign
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public int? RestaurantId { get; set; }
    /// <summary>plain (the restaurant's name on the group's colour), designed (a layout with words, logo and photo) or picture (finished artwork).</summary>
    public string Front { get; set; } = CardFronts.Plain;
    /// <summary>For a designed front: centered, photo or split.</summary>
    public string Layout { get; set; } = CardLayouts.Centered;
    public string? Headline { get; set; }
    public string? Message { get; set; }
    /// <summary>A short personal line printed above the coupon on the back.</summary>
    public string? BackMessage { get; set; }
    /// <summary>How the back is laid out: classic (the restaurant's own coupon card: logo, address, fill-in lines, legal text) or simple (a plain coupon box).</summary>
    public string BackStyle { get; set; } = CardBacks.Classic;
    /// <summary>The colour of the words and lines on a classic back. Empty prints the usual dark blue.</summary>
    public string? InkColor { get; set; }
    /// <summary>The web address printed under the restaurant's address and in the bottom corner of a classic back, such as culvers.com.</summary>
    public string? Website { get; set; }
    /// <summary>The small print on a classic back. Empty prints the standard wording.</summary>
    public string? LegalText { get; set; }
    /// <summary>For a back laid out in the card designer (BackStyle "custom"): its items as JSON (see CardElement).</summary>
    public string? BackLayoutJson { get; set; }
    /// <summary>For a front laid out in the card designer (Front "custom"): its items as JSON.</summary>
    public string? FrontLayoutJson { get; set; }
    public string? BackgroundColor { get; set; }
    public string? TextColor { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string UpdatedBy { get; set; } = "";
    public List<CardImage> Images { get; set; } = [];
}

public static class CardFronts
{
    /// <summary>custom is a front laid out item by item in the card designer.</summary>
    public const string Plain = "plain", Designed = "designed", Picture = "picture", Custom = "custom";
    public static readonly string[] All = [Plain, Designed, Picture, Custom];
}

public static class CardBacks
{
    /// <summary>custom is a back laid out item by item in the card designer; classic is the standard layout it starts from.</summary>
    public const string Classic = "classic", Simple = "simple", Custom = "custom";
    public static readonly string[] All = [Classic, Simple, Custom];
    public const string DefaultInk = "#2b4a8b";
    /// <summary>The standard small print, with its lines broken where the restaurants' printed cards break them.</summary>
    public const string DefaultLegal = "Please present this card prior to ordering. One order\nper card. Cannot be exchanged for gift certificates\nor cash in whole or part and has no cash value. Not\nvalid with other promotion or discounts. Valid only\nwith authorized signature.";
}

public static class CardLayouts
{
    public const string Centered = "centered", Photo = "photo", Split = "split";
    public static readonly string[] All = [Centered, Photo, Split];
}

public static class CardImageKinds
{
    /// <summary>Finished artwork for the whole front.</summary>
    public const string Picture = "picture";
    public const string Logo = "logo";
    public const string Photo = "photo";
    /// <summary>The logo printed in the top corner of a classic back (usually in the ink colour, on white).</summary>
    public const string BackLogo = "backlogo";
    /// <summary>A small picture for the bottom corner of a classic back, such as a strip of social media icons.</summary>
    public const string Footer = "footer";
    public static readonly string[] All = [Picture, Logo, Photo, BackLogo, Footer];
}

/// <summary>A picture belonging to a card design. Kept in its own table so loading a design never loads the image.</summary>
public class CardImage
{
    public int Id { get; set; }
    public int DesignId { get; set; }
    public CardDesign Design { get; set; } = null!;
    /// <summary>Copied from the design: the picture's public address is /card-art/{group}/{hash}.</summary>
    public int GroupId { get; set; }
    public string Kind { get; set; } = CardImageKinds.Picture;
    public string ContentType { get; set; } = "image/jpeg";
    public byte[] Data { get; set; } = [];
    /// <summary>Short hash of the image, part of its public address so a new upload gets a new address.</summary>
    public string Hash { get; set; } = "";
    public int? WidthPx { get; set; }
    public int? HeightPx { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public string UploadedBy { get; set; } = "";
}

/// <summary>One entry in a comment's follow-up history, shared by every manager who can see the comment.</summary>
public class CommentActivity
{
    public int Id { get; set; }
    public int CommentId { get; set; }
    public ActivityKind Kind { get; set; }
    public string? Method { get; set; }
    /// <summary>Day the guest was contacted (attempts and replies).</summary>
    public DateOnly? ContactedOn { get; set; }
    public string? Text { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int CreatedByUserId { get; set; }
    public string CreatedByName { get; set; } = "";
    public UserRole CreatedByRole { get; set; }
}

public class AuditLog
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int? UserId { get; set; }
    public string UserName { get; set; } = "";
    public UserRole? UserRole { get; set; }
    public int? GroupId { get; set; }
    public int? RestaurantId { get; set; }
    public string Action { get; set; } = "";
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
}

/// <summary>The ways a guest comment comes in. When it came by phone, the guest's phone number is required.</summary>
public static class CommentSources
{
    public const string Smg = "SMG survey";
    public const string Website = "Culvers.com Contact Us";
    public const string Phone = "Phone call";
    public static readonly string[] All = [Smg, Website, Phone];

    /// <summary>The canonical spelling of a source, or null when it is not one of <see cref="All"/>.</summary>
    public static string? Match(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : All.FirstOrDefault(s => s.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One user interaction: every API call (recorded by the server), plus page views, clicks and choices the browser
/// reports. Tagged with the investment group and, where known, the restaurant, so the System Administrator can
/// filter and report by either. Only System Administrators can read these. Never holds passwords, codes or
/// anything typed into a field.
/// </summary>
public class InteractionLog
{
    public long Id { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    /// <summary>See <see cref="InteractionKinds"/>.</summary>
    public string Kind { get; set; } = InteractionKinds.Api;
    public int? UserId { get; set; }
    public string? UserName { get; set; }
    public string? DisplayName { get; set; }
    public UserRole? Role { get; set; }
    public int? GroupId { get; set; }
    public int? RestaurantId { get; set; }
    /// <summary>A System Administrator acting inside a group ("View as admin").</summary>
    public bool ViewingAs { get; set; }
    public string? Method { get; set; }
    /// <summary>API path with its query string, or the page the browser was on.</summary>
    public string Path { get; set; } = "";
    /// <summary>The audited action this request performed (e.g. CommentCreated), or what was clicked.</summary>
    public string? Action { get; set; }
    public string? Detail { get; set; }
    public int? Status { get; set; }
    public int? DurationMs { get; set; }
    /// <summary>Random id per browser tab, to follow one visit.</summary>
    public string? SessionId { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}

public static class InteractionKinds
{
    public const string Api = "Api";
    public const string Page = "Page";
    public const string Click = "Click";
    public const string Choice = "Choice";
    public const string SignOut = "SignOut";
    public static readonly string[] FromBrowser = [Page, Click, Choice, SignOut];
    public static readonly string[] All = [Api, Page, Click, Choice, SignOut];
}

/// <summary>
/// A site-wide setting the System Administrator enters in the site itself (for example the USPS account).
/// Secret values are stored encrypted; see the service that owns each key.
/// </summary>
public class SiteSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
}
