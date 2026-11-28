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

/// <summary>
/// What a person may do. Investment Group Administrators hold everything. What owners and managers hold is handed
/// down level by level: see PermissionPolicy.
/// </summary>
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
    // The next four belong to owners and are never given to a manager (PermissionPolicy.OwnerOnly).
    /// <summary>Add, change, reset and suspend the managers and team logins at their restaurants, and assign them to restaurants.</summary>
    ManageStaff = 1024,
    /// <summary>Choose what each manager may do.</summary>
    SetPermissions = 2048,
    /// <summary>Add restaurants, and change a restaurant's details and coupon card settings.</summary>
    EditRestaurants = 4096,
    /// <summary>Change a restaurant's coupon card design.</summary>
    DesignCards = 8192,
    /// <summary>Switch the AI assistant on or off (where the group decides), and choose its level and the reward each face stands for. Owner-only.</summary>
    ManageAiAssistant = 16384,
    All = ViewComments | EditComments | DeleteComments | MarkResponded | ViewAnalytics | ExportComments | ViewAudit | ManageTeamLogin | BuyPostage | SetFollowUpDays
        | ManageStaff | SetPermissions | EditRestaurants | DesignCards | ManageAiAssistant
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

    // What owners and managers may do in this group (see PermissionPolicy). The first four are the System
    // Administrator's and take precedence: all can, or none can; a permission in neither is left to the group.
    public ManagerPermission SiteOwnerYes { get; set; }
    public ManagerPermission SiteOwnerNo { get; set; }
    public ManagerPermission SiteManagerYes { get; set; }
    public ManagerPermission SiteManagerNo { get; set; }
    // The group's administrators' own answers, which count where the site left the permission to them. Owners can
    // unless listed in OwnerNo. A manager permission in neither list is set manager by manager.
    public ManagerPermission OwnerNo { get; set; }
    public ManagerPermission ManagerYes { get; set; }
    public ManagerPermission ManagerNo { get; set; }

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
    /// <summary>
    /// On Print cards, "Mailed for you" is chosen to begin with for this restaurant's cards (when mailing is switched
    /// on for its group). The restaurant's owners and the group's administrators decide; anyone printing can still pick another way.
    /// </summary>
    public bool MailByDefault { get; set; }
    /// <summary>Cards can be mailed for this restaurant. Looked at only when its group leaves that to each restaurant.</summary>
    public bool MailOn { get; set; }
    /// <summary>This restaurant's cards are mailed for real, not as tests. Looked at only when the site and its group leave that to each restaurant.</summary>
    public bool MailLive { get; set; }
    /// <summary>
    /// The set of restaurants this one is billed with, when its group has its bills made for sets of restaurants
    /// (GroupBilling.BillSplit). Restaurants with the same name here share a bill; the ones with none share one too.
    /// </summary>
    public string? BillSet { get; set; }
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
    /// <summary>What was ticked for this manager. What they hold also depends on their group's rules: PermissionPolicy.Effective.</summary>
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
    /// <summary>
    /// The site's fee charged to the group because the restaurant printed this card itself for a stamp by hand, in
    /// cents, and when. Charged once, at the card's first print; null when no fee was charged.
    /// </summary>
    public int? PrintFeeCents { get; set; }
    public DateTime? PrintFeeAt { get; set; }

    /// <summary>How the comment reached the restaurant: one of <see cref="CommentSources.All"/>. Null on cards entered before it was asked.</summary>
    public string? Source { get; set; }

    // What happened
    public DateOnly VisitDate { get; set; }
    /// <summary>Reason category. The allowed list lives in the group's settings blob.</summary>
    public string Reason { get; set; } = "";
    public string? ReasonDetails { get; set; }
    public string? Notes { get; set; }

    // Follow-up. The full history (attempts, replies, notes) lives in CommentActivity; these are the latest reply.
    // Replied is what a person records (a call, an email, a word in person). Sending the coupon card is a separate
    // fact (SentAt below): mailing or printing a card never marks the guest as replied to.
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

    // Sending the coupon card: handed to the mailing service, or printed here and marked as sent. The latest send
    // that went; every attempt is in CardSend. Null while the card has not been sent (or its only send was cancelled).
    public DateTime? SentAt { get; set; }
    public string? SentByName { get; set; }
    /// <summary>
    /// When a card mailed for the restaurant really went into the mail: the mailing service handed it to the post
    /// office. Null until then, and always for a card printed here or a test order.
    /// </summary>
    public DateTime? InMailAt { get; set; }

    // An email to the guest, as well as (never instead of) the card: optional. The address, and a reply written
    // or drafted for it. Sending it from the site comes later; until then it is kept with the card to copy.
    public string? GuestEmail { get; set; }
    public string? EmailSubject { get; set; }
    public string? EmailBody { get; set; }
    /// <summary>How upset the guest was, 1 (a small slip) to 5 (furious), when a face was chosen on the card (AiMoods); null otherwise.</summary>
    public int? Mood { get; set; }

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
    MailCancelled = 11,
    /// <summary>The card was sent: printed here and marked as sent. (A card mailed for the restaurant has Mailed.)</summary>
    Sent = 12
}

/// <summary>
/// How one investment group is billed for the cards the site mails for it: its own fee and monthly limit where they
/// differ from the site's, where its bills go, and the payment method it has saved with the payment service.
/// </summary>
/// <summary>The group settings the System Administrator can hand to the group's own administrators to decide.</summary>
[Flags]
public enum GroupChoice
{
    None = 0,
    /// <summary>Whether cards can be printed and mailed.</summary>
    Mail = 1,
    /// <summary>Real or test mailing.</summary>
    Mode = 2,
    /// <summary>How cards usually go out.</summary>
    Usual = 4,
    /// <summary>The name that signs for the team login.</summary>
    Signature = 8,
    /// <summary>Whether the AI assistant is switched on. Not left to groups by default: it is off until the System Administrator says otherwise.</summary>
    Ai = 16,
    /// <summary>What a group decides for itself unless the System Administrator says otherwise: everything but switching mailing on.</summary>
    Default = Mode | Usual | Signature
}

public class GroupBilling
{
    public int GroupId { get; set; }
    /// <summary>
    /// The settings below that the group's own administrators decide. The others are the System Administrator's
    /// answer for this group, which the group cannot change.
    /// </summary>
    public GroupChoice GroupDecides { get; set; } = GroupChoice.Default;
    /// <summary>
    /// How the group's month is billed, which the group's own administrators choose: "one" bill for everything,
    /// a bill for each "restaurant", or bills for "sets" of restaurants they group (Restaurant.BillSet).
    /// </summary>
    public string BillSplit { get; set; } = BillSplits.One;
    /// <summary>
    /// Whether the System Administrator has switched on having cards printed and mailed for this group. Off until
    /// then: the group still sees the choice on Print cards, greyed out, with what it takes to use it.
    /// </summary>
    public bool MailEnabled { get; set; }
    /// <summary>Each restaurant decides whether cards are mailed for it (Restaurant.MailOn); MailEnabled is then not looked at.</summary>
    public bool MailPerRestaurant { get; set; }
    /// <summary>
    /// Real or test mailing for the group: "live" or "test" for all its restaurants, or "each" (each restaurant
    /// decides). The site can fix this for every group (BillingSettings.MailMode). A group starts in test.
    /// </summary>
    public string? MailMode { get; set; } = Services.MailChoices.Test;
    /// <summary>How cards usually go out: "mail" or "stamp" for all the group's restaurants, or "each" (each restaurant decides).</summary>
    public string UsualWay { get; set; } = Services.MailChoices.Each;
    /// <summary>The name that signs team-login cards at every restaurant in the group. Empty: each restaurant decides.</summary>
    public string? SignatureForAll { get; set; }
    /// <summary>
    /// The AI assistant on Enter Card is switched on for this group. Off to begin with. Whose decision that is depends
    /// on GroupDecides (GroupChoice.Ai): the System Administrator's, or left to the group.
    /// </summary>
    public bool AiEnabled { get; set; }
    /// <summary>The level of the assistant the group chose (AiLevels); null is the usual one.</summary>
    public string? AiLevel { get; set; }
    /// <summary>The most the group may be charged for the assistant in a calendar month, in cents. Null uses the site's default.</summary>
    public int? AiMonthlyLimitCents { get; set; }
    /// <summary>
    /// Whether this group must have a payment method saved before its cards can be mailed. Null follows the site's
    /// setting (required unless the System Administrator has switched that off).
    /// </summary>
    public bool? RequirePayMethod { get; set; }
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

/// <summary>
/// One notification from Stripe (its webhook), kept whole after its signature was checked. Payload is everything
/// Stripe sent; the other columns are read out of it so the list can be searched and shown. See PaymentNotifications.
/// </summary>
public class StripeEvent
{
    public int Id { get; set; }
    /// <summary>Stripe's own id for the notification (evt_…). The same one sent again is counted, not kept twice.</summary>
    public string EventId { get; set; } = "";
    public string Type { get; set; } = "";
    /// <summary>When Stripe says it happened.</summary>
    public DateTime? CreatedAt { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastReceivedAt { get; set; }
    public int Deliveries { get; set; } = 1;
    public bool LiveMode { get; set; }
    public string? ApiVersion { get; set; }
    public string? ObjectType { get; set; }
    public string? ObjectId { get; set; }
    /// <summary>The payment it is about (pi_…), which ties it to a bill.</summary>
    public string? PaymentId { get; set; }
    public int? InvoiceId { get; set; }
    public int? GroupId { get; set; }
    public int? AmountCents { get; set; }
    public string? Currency { get; set; }
    public string? Status { get; set; }
    /// <summary>Why a payment failed or was cancelled, or what a dispute is about, in Stripe's words.</summary>
    public string? Reason { get; set; }
    /// <summary>What the site did about it.</summary>
    public string? Outcome { get; set; }
    public string Payload { get; set; } = "";
}

/// <summary>
/// One attempt to send a card: see SendHistory. The address is the one the attempt used, kept as it was then.
/// </summary>
public class CardSend
{
    public int Id { get; set; }
    public int CommentId { get; set; }
    public GuestComment Comment { get; set; } = null!;
    /// <summary>1 for the card's first attempt, 2 for the next, and so on.</summary>
    public int Number { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public int? ByUserId { get; set; }
    public string? ByName { get; set; }
    /// <summary>mail (printed and mailed for the restaurant) or stamp (printed in the restaurant).</summary>
    public string Method { get; set; } = "";
    /// <summary>sent, refused (the mailing service did not take it) or cancelled (before it was printed).</summary>
    public string Outcome { get; set; } = "";
    /// <summary>Sent again, after an earlier send of the same card.</summary>
    public bool Resend { get; set; }
    public string? StreetAddress { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip { get; set; }
    public string? AddressStatus { get; set; }
    /// <summary>The mailing service's reference and latest status, for a mailed attempt.</summary>
    public string? MailId { get; set; }
    public string? MailStatus { get; set; }
    public DateTime? StatusCheckedAt { get; set; }
    /// <summary>When this attempt really went into the mail (the mailing service handed it to the post office).</summary>
    public DateTime? InMailAt { get; set; }
    public bool Test { get; set; }
    /// <summary>What the group is billed for this attempt: the mail house's cost and the site's fee. Nothing for a test, a refusal or a card printed here.</summary>
    public int CostCents { get; set; }
    public int FeeCents { get; set; }
    /// <summary>Why it was refused, in words a group can read.</summary>
    public string? Error { get; set; }
}

public static class BillSplits
{
    public const string One = "one", Restaurant = "restaurant", Sets = "sets";
    public static string Of(GroupBilling? b) => b?.BillSplit is Restaurant or Sets ? b.BillSplit : One;
    /// <summary>What the bill for the restaurants in no set is called.</summary>
    public const string OtherRestaurants = "Other restaurants";
}

public static class InvoiceStatuses
{
    public const string Open = "open", Processing = "processing", Paid = "paid", Void = "void";
}

/// <summary>
/// One line in a group's credit account, which is never edited or removed. The System Administrator gives a credit
/// (a check received, a correction, a welcome bonus) with its details; the site uses credit on the group's bills and
/// records each use here too. The group's credit is the sum of its lines.
/// </summary>
public class GroupCredit
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    /// <summary>Above zero for credit given or returned; below zero for credit used on a bill or taken back.</summary>
    public int AmountCents { get; set; }
    public string Kind { get; set; } = CreditKinds.Other;
    /// <summary>What it was for, in the System Administrator's words. The group does not see this.</summary>
    public string Details { get; set; } = "";
    /// <summary>The bill it was used on, or returned from.</summary>
    public int? InvoiceId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "";
    public int? CreatedByUserId { get; set; }
    /// <summary>Sent with the request that made the line, so pressing the button twice cannot record it twice.</summary>
    public string? Key { get; set; }
}

public static class CreditKinds
{
    public const string Check = "check", Correction = "correction", Bonus = "bonus", Other = "other", TakenBack = "takenback", Applied = "applied", Returned = "returned";
    /// <summary>The reasons a System Administrator can give a credit for.</summary>
    public static readonly string[] Given = [Check, Correction, Bonus, Other];
    public static string Label(string kind) => kind switch
    {
        Check => "Check received", Correction => "Correction", Bonus => "Welcome bonus", Other => "Other credit", TakenBack => "Credit taken back",
        Applied => "Used on a bill", Returned => "Returned from a cancelled bill", _ => kind
    };
}

/// <summary>
/// One month's bill to one investment group for mailed cards, frozen when it is issued: the cards, what the mail
/// house cost the site, the site's fee, and how and when it was paid.
/// </summary>
public class MailInvoice
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    /// <summary>yyyy-MM. One bill per group, month and unit.</summary>
    public string Month { get; set; } = "";
    /// <summary>
    /// What part of the group this bill is for: empty for everything, "r12" for one restaurant, "s:north" for a set
    /// of restaurants. UnitName is how that reads on the bill.
    /// </summary>
    public string Unit { get; set; } = "";
    public string? UnitName { get; set; }
    public string Number { get; set; } = "";
    public int Cards { get; set; }
    public int CostCents { get; set; }
    public int FeeCents { get; set; }
    /// <summary>What the bill comes to: CostCents + FeeCents.</summary>
    public int TotalCents { get; set; }
    /// <summary>How much of the group's credit has been used on this bill. What is still owed is TotalCents less this.</summary>
    public int CreditCents { get; set; }
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
/// <summary>
/// One press of "Process" on the AI assistant: see AiLedger. Kept whether or not a card was ever saved from it, so
/// every request a group is charged for can be shown with who made it, when, what was typed and what came back.
/// </summary>
public class AiRequest
{
    public int Id { get; set; }
    /// <summary>Made by the browser for each press, so the same press arriving twice is answered once and charged once.</summary>
    public string RequestKey { get; set; } = "";
    public int GroupId { get; set; }
    public int RestaurantId { get; set; }
    public int? UserId { get; set; }
    public string UserName { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
    /// <summary>The level it ran at (AiLevels) and the model behind that level then.</summary>
    public string Level { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>What the group is charged for it, in tenths of a cent. 0 for a request that failed.</summary>
    public int PriceMills { get; set; }
    public bool WantEmail { get; set; }
    /// <summary>ok or failed.</summary>
    public string Outcome { get; set; } = "";
    /// <summary>Why it failed, for the System Administrator.</summary>
    public string? Error { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    /// <summary>What the request cost the site, in millionths of a dollar.</summary>
    public int CostMicros { get; set; }
    /// <summary>What was typed, and the answer as JSON: the evidence for the charge, and what a repeated press is answered with.</summary>
    public string Input { get; set; } = "";
    public string? ResultJson { get; set; }
    public string? GuestName { get; set; }
    public string? Reason { get; set; }
    public int? Mood { get; set; }
    public string? Offer { get; set; }
    /// <summary>The card saved from this request; null while (or if) none was.</summary>
    public int? CommentId { get; set; }
    public DateTime? LinkedAt { get; set; }
}

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
    /// <summary>A System Administrator acting inside a group ("Impersonate").</summary>
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
