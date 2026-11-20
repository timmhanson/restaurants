using System.ComponentModel.DataAnnotations;
using RestaurantFeedback.Api.Models;
using RestaurantFeedback.Api.Services;

namespace RestaurantFeedback.Api.Dtos;

public record LoginRequest([Required] string Username, [Required] string Password);
public record LoginResponse(string Token, DateTime ExpiresAt, UserDto User, bool MustChangePassword);
public record ChangePasswordRequest([Required] string CurrentPassword, [Required, MinLength(8)] string NewPassword, [Required, RegularExpression(@"^\d{6}$")] string Code);
public record CodeSentResponse(string EmailMasked, DateTime ExpiresAt, int ResendAfterSeconds);

public record UserDto(
    int Id, string Username, string DisplayName, string? Email, string Role, string RoleTitle, int? GroupId, string? GroupName, int Permissions, string[] PermissionNames,
    int[] RestaurantIds, bool IsActive, bool MustChangePassword, DateTime CreatedAt, DateTime? LastLoginAt)
{
    /// <summary>
    /// An investment group administrator who is also an owner of one or more restaurants: two roles at once. For
    /// them RestaurantIds are the restaurants they own (they still see every restaurant in the group).
    /// </summary>
    public bool AlsoOwner { get; init; }

    public static UserDto From(AppUser u)
    {
        var alsoOwner = u.Role == UserRole.GroupAdmin && u.UserRestaurants.Count > 0;
        // What they hold once their group's rules are counted (the group is loaded wherever this is called for a member).
        var holds = Services.PermissionPolicy.Effective(u.Role, u.Permissions, u.Group);
        return new(
            u.Id, u.Username, u.DisplayName, u.Email, u.Role.ToString(), alsoOwner ? $"{RoleTitles.Of(u.Role)} and {RoleTitles.Of(UserRole.Owner)}" : RoleTitles.Of(u.Role), u.GroupId, u.Group?.Name,
            (int)holds,
            PermissionNamesOf(holds),
            u.UserRestaurants.Select(x => x.RestaurantId).OrderBy(x => x).ToArray(),
            u.IsActive, u.MustChangePassword, u.CreatedAt, u.LastLoginAt) { AlsoOwner = alsoOwner };
    }

    public static string[] PermissionNamesOf(ManagerPermission p) =>
        Enum.GetValues<ManagerPermission>()
            .Where(v => v != ManagerPermission.None && v != ManagerPermission.All && p.HasFlag(v))
            .Select(v => v.ToString()).ToArray();
}

public class UserRequest
{
    [Required, MinLength(3), MaxLength(100)] public string Username { get; set; } = "";
    [Required, MaxLength(200)] public string DisplayName { get; set; } = "";
    [EmailAddress] public string? Email { get; set; }
    /// <summary>Required on create, optional on update.</summary>
    [MinLength(8)] public string? Password { get; set; }
    [Required] public string Role { get; set; } = "Manager";
    /// <summary>Permission names (see ManagerPermission). Ignored for owners and team members.</summary>
    public string[] Permissions { get; set; } = [];
    public int[] RestaurantIds { get; set; } = [];
    public bool IsActive { get; set; } = true;
    /// <summary>
    /// When a password is set for someone else, make them choose a new one at next sign-in (the default).
    /// Only System and Investment Group Administrators may turn this off; team logins never have to.
    /// </summary>
    public bool RequirePasswordChange { get; set; } = true;
}

/// <param name="RequirePasswordChange">See <see cref="UserRequest.RequirePasswordChange"/>.</param>
public record SetPasswordRequest([Required, MinLength(8)] string Password, bool RequirePasswordChange = true);
/// <summary>Suspend (Active = false) or restore someone's access to every restaurant they work at.</summary>
public record UserStatusRequest(bool Active);
public record UserRestaurantsRequest(int[]? RestaurantIds);

public record GroupDto(int Id, string Name, bool IsActive, DateTime CreatedAt, int RestaurantCount, int UserCount, int CommentCount)
{
    public List<string> OwnerNames { get; init; } = [];
    public List<string> AdminNames { get; init; } = [];
}

public class GroupRequest
{
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    /// <summary>Optionally create the group's first owner at the same time (kept for older clients; prefer Owners).</summary>
    public UserRequest? FirstOwner { get; set; }
    /// <summary>Investment Group administrators to create with the group. A new group needs at least one.</summary>
    public List<UserRequest> Admins { get; set; } = [];
    /// <summary>Restaurant owners to create with the group. Which restaurants each owns is set on each restaurant.</summary>
    public List<UserRequest> Owners { get; set; } = [];
    /// <summary>Restaurants to create with the group.</summary>
    public List<RestaurantRequest> Restaurants { get; set; } = [];
}

public record PersonRef(int Id, string DisplayName, string Username);

/// <summary>
/// A restaurant. The staffing fields (owners, counts, ownedByMe, canManage) are filled in only on the
/// admin listing (GET /api/restaurants?all=true).
/// </summary>
public record RestaurantDto(int Id, string Name, string? StreetAddress, string? City, string? State, string? Zip, string? Phone, bool IsActive)
{
    public List<PersonRef>? Owners { get; init; }
    public int? ManagerCount { get; init; }
    public int? TeamLoginCount { get; init; }
    public bool? OwnedByMe { get; init; }
    public bool? CanManage { get; init; }
    /// <summary>May change its details and coupon card settings: an administrator, or an owner where owners were left that.</summary>
    public bool? CanEdit { get; init; }
    /// <summary>An owner may mark this restaurant as theirs (no owner yet, and no investment group administrator to decide).</summary>
    public bool? CanClaim { get; init; }

    /// <summary>The name that signs cards entered with this restaurant's shared team login.</summary>
    public string? SignatureName { get; init; }
    /// <summary>On Print cards, "Mailed for you" is chosen to begin with for this restaurant's cards.</summary>
    public bool MailByDefault { get; init; }
    /// <summary>The restaurant's own choices for whether cards are mailed for it and whether for real; each counts only when its group leaves that to the restaurant.</summary>
    public bool MailOn { get; init; }
    public bool MailLive { get; init; }
    /// <summary>What actually applies to the restaurant once its group's (and the site's) settings are counted, and who decided each.</summary>
    public Services.RestaurantRules? Applies { get; init; }

    public static RestaurantDto From(Restaurant r) =>
        new(r.Id, r.Name, r.StreetAddress, r.City, r.State, r.Zip, r.Phone, r.IsActive)
        {
            SignatureName = r.SignatureName, MailByDefault = r.MailByDefault, MailOn = r.MailOn, MailLive = r.MailLive
        };

    public static RestaurantDto From(Restaurant r, Services.BillingSettings site, GroupBilling? group) => From(r) with { Applies = Services.GroupPolicy.For(site, group, r) };
}

public record StaffMember(int Id, string DisplayName, string Username, string? Email, string Role, string[] PermissionNames, bool Assigned, bool IsActive, string[] OtherRestaurants);
public record StaffResponse(RestaurantDto Restaurant, bool CanManage, List<StaffMember> Owners, List<StaffMember> Managers, List<StaffMember> TeamLogins)
{
    /// <summary>Investment Group administrators see every restaurant without being assigned; listed for information.</summary>
    public List<PersonRef> GroupAdmins { get; init; } = [];
    /// <summary>The group's administrators, each marked as assigned when they are also an owner of this restaurant.</summary>
    public List<StaffMember> AdminOwners { get; init; } = [];
    /// <summary>False when the group has an investment group administrator, so a restaurant may have no specific owner.</summary>
    public bool OwnerRequired { get; init; } = true;
}
public record StaffRequest(int[] UserIds);
public record SignatureNameRequest([MaxLength(60)] string? Name);
public record OwnershipRequest(bool Owned);

public class RestaurantRequest
{
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    public string? StreetAddress { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip { get; set; }
    public string? Phone { get; set; }
    [MaxLength(60)] public string? SignatureName { get; set; }
    public bool MailByDefault { get; set; }
    /// <summary>The restaurant's own choices. Left out, they stay as they are; each counts only when the group leaves it to the restaurant.</summary>
    public bool? MailOn { get; set; }
    public bool? MailLive { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Group creation only: usernames of the new owners who own this restaurant. Null means all of them.</summary>
    public string[]? OwnerUsernames { get; set; }
}

public class CommentRequest
{
    [Required] public int RestaurantId { get; set; }
    [Required, MaxLength(100)] public string GuestFirstName { get; set; } = "";
    [Required, MaxLength(100)] public string GuestLastName { get; set; } = "";
    [MaxLength(200)] public string? StreetAddress { get; set; }
    [MaxLength(100)] public string? City { get; set; }
    [MaxLength(50)] public string? State { get; set; }
    [MaxLength(20)] public string? Zip { get; set; }
    [MaxLength(30)] public string? Phone { get; set; }
    /// <summary>How the comment came in: "SMG survey", "Culvers.com Contact Us" or "Phone call". Phone calls need the guest's phone number.</summary>
    [MaxLength(50)] public string? Source { get; set; }
    [MaxLength(200)] public string? CouponOffer { get; set; }
    public DateOnly? DateIssued { get; set; }
    [MaxLength(100)] public string? AuthorizedBy { get; set; }
    [Required] public DateOnly VisitDate { get; set; }
    [Required, MaxLength(100)] public string Reason { get; set; } = "";
    [MaxLength(2000)] public string? ReasonDetails { get; set; }
    [MaxLength(2000)] public string? Notes { get; set; }
    /// <summary>Set true to save even though a duplicate / repeat guest was detected.</summary>
    public bool Force { get; set; }
    /// <summary>
    /// What the person did when USPS offered its version of the address: "UsedUsps" (took it) or "KeptOwn" (kept
    /// what they typed). Recorded in the audit log; it does not change what is saved.
    /// </summary>
    [MaxLength(20)] public string? AddressChoice { get; set; }
    /// <summary>The address as first typed, when the USPS version replaced it. For the audit log only.</summary>
    [MaxLength(400)] public string? AddressAsTyped { get; set; }
}

public record CommentSummary(
    int Id, int RestaurantId, string RestaurantName, string GuestFirstName, string GuestLastName, string? Phone,
    string? StreetAddress, string? City, string? State, string? Zip, string? CouponOffer, DateOnly DateIssued, DateOnly ExpiresOn,
    string? AuthorizedBy, DateOnly VisitDate, string DayOfWeek, string Reason, string? ReasonDetails, string? Notes,
    bool Responded, DateTime? RespondedAt, string? RespondedByName, DateTime CreatedAt, string CreatedByName, DateTime? UpdatedAt,
    string GuestKey, int GuestCommentCount)
{
    public string? Source { get; init; }
    /// <summary>USPS's verdict on the mailing address when the card was saved: Verified, Corrected, NeedsUnit, UnitNotConfirmed or NotFound.</summary>
    public string? AddressStatus { get; init; }
    // Printing, postage, redemption and the after-sending follow-up
    public DateTime? PrintedAt { get; init; }
    public string? PrintedByName { get; init; }
    public int PrintCount { get; init; }
    public int? PostageCents { get; init; }
    /// <summary>The fee the group was charged because this card was printed in the restaurant for a stamp by hand; null when none was.</summary>
    public int? PrintFeeCents { get; init; }
    public string? PostageLabelUrl { get; init; }
    public string? PostageTrackingCode { get; init; }
    public bool PostageTest { get; init; }
    /// <summary>The card's own identifier, carried (signed) by the QR code printed on it.</summary>
    public Guid CardId { get; init; }
    /// <summary>Printed and mailed for the group by the site's mail house: its status (ready, printing, processed_for_delivery, completed, cancelled).</summary>
    public string? MailStatus { get; init; }
    public DateTime? MailedAt { get; init; }
    public bool MailTest { get; init; }
    public DateOnly? RedeemedOn { get; init; }
    public string? RedeemedByName { get; init; }
    public DateOnly? FollowedUpOn { get; init; }
    public string? FollowedUpByName { get; init; }
    /// <summary>The day the guest should be followed up with; null when none is needed (not sent by mail, already done, redeemed, or turned off).</summary>
    public DateOnly? FollowUpDueOn { get; init; }
    public string? ReplyMethod { get; init; }
    public DateOnly? RepliedOn { get; init; }
    public DateTime? LastActivityAt { get; init; }
    /// <summary>Follow-up counts and the latest note; filled in on lists and single reads.</summary>
    public int NoteCount { get; init; }
    public int AttemptCount { get; init; }
    public ActivityDto? LatestNote { get; init; }

    public static CommentSummary From(GuestComment c, int guestCommentCount) => new(
        c.Id, c.RestaurantId, c.Restaurant?.Name ?? "", c.GuestFirstName, c.GuestLastName, c.Phone,
        c.StreetAddress, c.City, c.State, c.Zip, c.CouponOffer, c.DateIssued, c.ExpiresOn,
        c.AuthorizedBy, c.VisitDate, c.VisitDate.DayOfWeek.ToString(), c.Reason, c.ReasonDetails, c.Notes,
        c.Responded, c.RespondedAt, c.RespondedByName, c.CreatedAt, c.CreatedByName, c.UpdatedAt,
        c.GuestKey, guestCommentCount)
    {
        Source = c.Source, AddressStatus = c.AddressStatus, PrintedAt = c.PrintedAt, PrintedByName = c.PrintedByName, PrintCount = c.PrintCount, PostageCents = c.PostageCents,
        PostageLabelUrl = c.PostageLabelUrl, PostageTrackingCode = c.PostageTrackingCode, PostageTest = c.PostageTest,
        PrintFeeCents = c.PrintFeeCents, CardId = c.PublicId, MailStatus = c.MailId == null ? null : c.MailStatus, MailedAt = c.MailedAt, MailTest = c.MailTest, RedeemedOn = c.RedeemedOn,
        RedeemedByName = c.RedeemedByName, FollowedUpOn = c.FollowedUpOn, FollowedUpByName = c.FollowedUpByName,
        ReplyMethod = c.ReplyMethod, RepliedOn = c.RepliedOn, LastActivityAt = c.LastActivityAt
    };
}

public record ActivityDto(int Id, string Kind, string? Method, DateOnly? ContactedOn, string? Text, DateTime CreatedAt, string CreatedByName, string CreatedByRole)
{
    public static ActivityDto From(CommentActivity a) =>
        new(a.Id, a.Kind.ToString(), a.Method, a.ContactedOn, a.Text, a.CreatedAt, a.CreatedByName, RoleTitles.Of(a.CreatedByRole));
}

/// <summary>
/// Add to a comment's follow-up history.
///  - Note: text for the other managers.
///  - Attempt: tried to reach the guest (method, date, what happened).
///  - Replied: confirms the reply was sent; needs a method, the date and Confirmed = true.
///  - Reopened: the reply did not happen or needs redoing; needs a reason.
/// </summary>
public class FollowUpRequest
{
    [Required] public string Kind { get; set; } = "Note";
    [MaxLength(100)] public string? Method { get; set; }
    public DateOnly? ContactedOn { get; set; }
    [MaxLength(2000)] public string? Text { get; set; }
    public bool Confirmed { get; set; }
}

public record FollowUpResponse(CommentSummary Comment, List<ActivityDto> Activity);

public record DuplicateCheckResult(
    bool ExactDuplicate, bool LikelyDuplicate, bool RepeatGuest, int PriorCount, int Threshold, string GuestKey,
    List<CommentSummary> PriorComments, string? Message);

public record PagedResult<T>(List<T> Items, int Total, int Page, int PageSize);

public record RespondRequest(bool Responded);

public record AuditDto(long Id, DateTime Timestamp, int? UserId, string UserName, string? UserRole, int? RestaurantId, string? RestaurantName,
    string Action, string? EntityType, string? EntityId, string? Details, string? IpAddress);

public record AnalyticsDto(
    int TotalComments, int Responded, int Unresponded, int DistinctGuests, int RepeatGuests, double AvgPerWeek,
    List<CountItem> ByDayOfWeek, List<CountItem> ByReason, List<CountItem> ByCouponOffer, List<CountItem> ByRestaurant,
    List<CountItem> ByWeek, List<CountItem> ByMonth, List<CountItem> ByEnteredBy,
    List<RepeatGuestItem> TopRepeatGuests)
{
    /// <summary>Restaurants included in these figures.</summary>
    public List<NamedRef>? Scope { get; init; }
    /// <summary>Side-by-side comparison, one row per restaurant in scope (including ones with no cards).</summary>
    public List<RestaurantRollup> Restaurants { get; init; } = [];
    /// <summary>Cards per month per restaurant, for the trend chart.</summary>
    public List<RestaurantMonth> MonthlyByRestaurant { get; init; } = [];
    /// <summary>Guests with cards at more than one restaurant in scope.</summary>
    public int CrossRestaurantGuests { get; init; }
    /// <summary>Average days from the guest's visit to the reply, over replied cards with a reply date.</summary>
    public double? AvgDaysToReply { get; init; }
    /// <summary>Unreplied cards whose visit was more than a week ago.</summary>
    public int WaitingOverAWeek { get; init; }
    public List<CountItem> ByReplyMethod { get; init; } = [];
    /// <summary>How the comments came in (SMG survey, Culvers.com, phone), with how many were replied to.</summary>
    public List<CountItem> BySource { get; init; } = [];
    /// <summary>Coupon cards printed here, coupons the guests came back and used, and postage bought (cents).</summary>
    public int Printed { get; init; }
    public int Redeemed { get; init; }
    public int PostageCents { get; init; }
    public int FollowedUp { get; init; }
}

public record RestaurantRollup(
    int RestaurantId, string Name, int Total, int Responded, int Waiting, double ResponseRate, int Guests, int RepeatGuests,
    double AvgPerWeek, double? AvgDaysToReply, int WaitingOverAWeek, int? OldestWaitingDays, string? TopReason, DateOnly? LastVisit);

public record NamedRef(int Id, string Name);
public record RestaurantMonth(string Month, int RestaurantId, string Restaurant, int Count);

public record CountItem(string Label, int Count, int? Responded = null);
public record RepeatGuestItem(string GuestKey, string Name, string? Phone, string? Address, int Count, DateOnly LastVisit, string[] Restaurants, string[] Reasons);

// ---- Interaction log and System Administrator reports ----------------------------------------------------

/// <summary>A browser event: Page (a page was opened), Click (a button or link), Choice (a select, checkbox or radio), SignOut.</summary>
public record ActivityEvent(string Kind, string Path, string? Label, string? Detail, DateTime? At, int? RestaurantId);
public record ActivityBatch(string? SessionId, List<ActivityEvent> Events);

public record InteractionDto(long Id, DateTime At, string Kind, int? UserId, string? UserName, string? DisplayName, string? RoleTitle,
    int? GroupId, string? GroupName, int? RestaurantId, string? RestaurantName, bool ViewingAs, string? Method, string Path,
    string? Action, string? Detail, int? Status, int? DurationMs, string? SessionId, string? IpAddress, string? UserAgent);
public record InteractionPage(int Total, int Page, int PageSize, List<InteractionDto> Items);

public record CatalogGroup(int Id, string Name, bool IsActive);
public record CatalogRestaurant(int Id, string Name, int GroupId, bool IsActive);
public record CatalogUser(int Id, string Username, string DisplayName, string RoleTitle, int? GroupId);
public record SystemCatalog(List<CatalogGroup> Groups, List<CatalogRestaurant> Restaurants, List<CatalogUser> Users);

/// <summary>Card and reply figures shared by the group and restaurant rows.</summary>
public record CardStats(int Cards, int Replied, int Waiting, int WaitingOverAWeek, double? AvgDaysToReply, int Smg, int Website, int Phone, int SourceNotRecorded)
{
    /// <summary>Coupon cards printed, coupons redeemed, guests followed up with after sending, and postage bought (cents).</summary>
    public int Printed { get; init; }
    public int Redeemed { get; init; }
    public int FollowedUp { get; init; }
    public int PostageCents { get; init; }
    /// <summary>Cards printed and mailed by PostGrid (not test orders or cancelled ones), their expected cost and the site's fee, in cents.</summary>
    public int MailedByPostGrid { get; init; }
    public int MailEstimateCents { get; init; }
    public int MailFeeCents { get; init; }
}

public record GroupReportRow(int Id, string Name, bool IsActive, int Restaurants, int Users, int ActiveUsers, int SignIns, int Interactions,
    DateTime? LastActivity, CardStats Stats);
public record RestaurantReportRow(int Id, string Name, int GroupId, string GroupName, bool IsActive, int Staff, int ActiveUsers, int Interactions,
    DateTime? LastActivity, DateOnly? LastVisit, CardStats Stats);
public record UserReportRow(int Id, string DisplayName, string Username, string RoleTitle, int? GroupId, string? GroupName, int Interactions,
    int SignIns, int PageViews, int CardsEntered, DateTime? LastSeen);
public record DailyReportRow(DateOnly Date, int? GroupId, int Interactions, int ActiveUsers, int Cards);

public record SystemReport(DateOnly From, DateOnly To, CardStats Totals, int Groups, int Restaurants, int Users, int ActiveUsers, int SignIns,
    int FailedSignIns, int Interactions, List<GroupReportRow> ByGroup, List<RestaurantReportRow> ByRestaurant, List<UserReportRow> TopUsers,
    List<DailyReportRow> Daily, List<CountItem> ByKind);

// ---- System administration: overview, security, status, directories -------------------------------------

/// <summary>Something the System Administrator should look at. Severity: high, medium or info. Link is a page in the site.</summary>
public record AttentionItem(string Severity, string Title, string Detail, string? Link);
public record GroupSnapshot(int Id, string Name, bool IsActive, int Restaurants, int People, int Admins, int ActivePeople7d, int Cards7d, int Waiting,
    int WaitingOverAWeek, DateTime? LastActivity);
public record SignInRow(DateTime At, string? UserName, string? DisplayName, string? RoleTitle, string? GroupName, bool Succeeded, string? IpAddress, string? UserAgent);
public record SystemOverview(DateTime GeneratedAt, int Groups, int ActiveGroups, int Restaurants, int People, int SystemAdmins, int ActiveToday, int Active7d,
    int SignInsToday, int FailedSignIns24h, int CardsToday, int Cards7d, int Waiting, int WaitingOverAWeek, int Errors24h,
    List<AttentionItem> Attention, List<GroupSnapshot> GroupSnapshots, List<SignInRow> RecentSignIns);

public record FailedSignInRow(string UserName, bool KnownAccount, int Attempts, DateTime Last, string[] IpAddresses);
public record RefusedRow(string? UserName, string? DisplayName, string? RoleTitle, string? GroupName, int Count, DateTime Last, string[] Paths);
public record AccountFlag(int Id, string DisplayName, string Username, string RoleTitle, int? GroupId, string? GroupName, string Issue, DateTime? Since);
public record SystemSecurity(int Days, int SignIns, int FailedSignIns, int Refused, int StaleCopies, List<FailedSignInRow> Failed, List<RefusedRow> RefusedBy,
    List<AccountFlag> Accounts, List<UserDto> SystemAdministrators, List<SignInRow> RecentSignIns);

public record TableCount(string Name, int Rows);
public record EndpointStat(string Endpoint, int Calls, int AvgMs, int MaxMs, int Errors);
public record ErrorRow(DateTime At, string? UserName, string? GroupName, string Method, string Path, int Status, string? Detail);
public record SystemStatus(string Version, string Environment, DateTime StartedAt, DateTime ServerTime, string Database, long DatabaseBytes,
    string? LatestMigration, List<TableCount> Tables, string SettingsStore, string Email, bool EmailConfigured, string? TestEmailTo, int LogRetentionDays,
    DateTime? OldestLogEntry, int Requests24h, int Errors24h, int Refused24h, int AvgMs24h, int P95Ms24h, List<EndpointStat> Slowest, List<ErrorRow> RecentErrors)
{
    /// <summary>The postage service for printed coupon cards, and what has been bought through it.</summary>
    /// <summary>The service that checks guests' mailing addresses, and what it has found on the cards on file.</summary>
    public string AddressChecking { get; init; } = "";
    public bool AddressCheckingConfigured { get; init; }
    public int AddressesConfirmed { get; init; }
    public int AddressesNotFound { get; init; }
    public string Postage { get; init; } = "";
    public bool PostageConfigured { get; init; }
    /// <summary>Investment groups that have added their own postage account (each pays for its own postage).</summary>
    public int PostageGroupAccounts { get; init; }
    public int PostageCardsBought { get; init; }
    public int PostageCentsSpent { get; init; }
    public int PostageTestLabels { get; init; }
}

public record DirectoryRestaurant(int Id, string Name, string? City, string? State, bool IsActive, int GroupId, string GroupName, bool GroupIsActive,
    string[] Owners, int Managers, int TeamLogins, int Cards, int Waiting, int WaitingOverAWeek, DateOnly? LastVisit, DateTime CreatedAt);
public record SystemPerson(UserDto User, string[] Restaurants, DateTime? LastSeen, int Interactions30d);

// ---- Printing coupon cards, postage, redemption and following up after sending -------------------------

public record PrintConfig(bool PostageAvailable, string PostageProvider, bool CanBuyPostage, int MaxCentsPerCard, string MailedMethod)
{
    /// <summary>The site has a mailing account, mailing is switched on for this group and its monthly limit is above zero, so cards can be printed and mailed for it.</summary>
    public bool MailAvailable { get; init; }
    /// <summary>
    /// Why this person cannot have cards mailed right now, or null when they can: "site" (the site has no mailing
    /// account yet), "group" (not switched on for this investment group), "limit" (the group's monthly limit is zero),
    /// "payment" (the group must save a payment method first) or "permission" (this person may not spend money).
    /// </summary>
    public string? MailBlockedBy { get; init; }
    /// <summary>
    /// The group has no payment method saved where one is required, and mails on its credit instead: only as many
    /// cards as MailCreditCents covers.
    /// </summary>
    public bool MailOnCredit { get; init; }
    /// <summary>The group's credit that is free to spend on more cards, in cents.</summary>
    public int MailCreditCents { get; init; }
    /// <summary>
    /// What the group is billed for each card it prints itself for a stamp by hand, in cents (0 when nothing is).
    /// Charged once per card, at its first print.
    /// </summary>
    public int PrintFeeCents { get; init; }
    /// <summary>For each restaurant this person works at: whether cards can be mailed for it, whether for real, and whether the key for that is entered.</summary>
    public List<MailRestaurantInfo> MailRestaurants { get; init; } = [];
    /// <summary>MailAvailable, and this person may spend money (the BuyPostage permission).</summary>
    public bool CanMail { get; init; }
    /// <summary>Mailing is on, but the group must first save a payment method on Billing.</summary>
    public bool MailNeedsPayMethod { get; init; }
    /// <summary>The site's mailing key is a test key: nothing is printed, mailed or charged.</summary>
    public bool MailTest { get; init; }
    /// <summary>What the group is billed for each mailed card, in cents.</summary>
    public int MailCentsPerCard { get; init; }
    public int MailMaxCards { get; init; }
    /// <summary>The group's limit for mailed cards this calendar month, and what has been billed so far, in cents.</summary>
    public int MailLimitCents { get; init; }
    public int MailUsedCents { get; init; }
}
/// <param name="ConfirmCount">The number of cards the person confirmed; the request is refused if it does not match.</param>
/// <param name="ConfirmCentsPerCard">The price per card the person was shown; the request is refused if the price has changed since.</param>
public record MailRequest(int[] CommentIds, int ConfirmCount, int ConfirmCentsPerCard);
/// <param name="Status">The mail house's status: ready, printing, processed_for_delivery, completed or cancelled.</param>
public record MailOutcome(int CommentId, bool Ok, string? Status, bool Test, bool AlreadyMailed, string? Error);
/// <param name="HasProof">The mail house has a proof of the printed card, fetched through comments/{id}/mail/proof.</param>
public record MailStatusDto(int CommentId, string? Status, bool Test, bool HasProof, string? Error);
public record MailPreview(string FrontHtml, string BackHtml);
/// <param name="Url">The address the QR code holds: this site's verify page for the card.</param>
/// <param name="Image">The QR code as a picture (an SVG in a data address), ready for an img tag.</param>
public record CardQr(int CommentId, Guid CardId, string Url, string Image);

// ---- Checking a scanned card -----------------------------------------------------------------------------

/// <param name="Genuine">The code was issued by this site and names a card that still exists.</param>
/// <param name="Card">The card as the site has it; null when the code is not genuine or the card is not this person's to see.</param>
/// <param name="CanRedeem">This person may mark the coupon as redeemed, and it has not been yet.</param>
public record CardCheck(bool Genuine, string Message, CardCheckDetails? Card, bool CanRedeem);
/// <param name="CanOpen">This person may open the card's full record on Comments.</param>
public record CardCheckDetails(int CommentId, Guid CardId, string Restaurant, string GuestName, string? Offer, DateOnly DateIssued, DateOnly ExpiresOn, bool Expired,
    DateOnly? RedeemedOn, string? RedeemedByName, bool Sent, bool CanOpen);

// ---- How mailed cards look: the group's design and each restaurant's own ---------------------------------

/// <param name="Url">Public address of the picture (the mail house downloads it from here).</param>
/// <param name="FromGroup">Borrowed from the group's card, because this restaurant has not uploaded its own.</param>
public record CardImageDto(string Kind, string Url, int? WidthPx, int? HeightPx, long Bytes, DateTime UploadedAt, string UploadedBy, bool FromGroup);
public record CardDesignFields(string? Front, string? Layout, string? Headline, string? Message, string? BackMessage, string? BackgroundColor, string? TextColor,
    string? BackStyle = null, string? InkColor = null, string? Website = null, string? LegalText = null,
    List<CardElement>? BackLayout = null, List<CardElement>? FrontLayout = null);
/// <summary>One of the fields a card's words can carry in braces, with what it prints on the sample card.</summary>
public record CardMergeField(string Key, string Label, string Sample);
/// <param name="Scope">"group" or a restaurant's id.</param>
/// <param name="HasOwn">False when a restaurant has no design of its own and uses the group's.</param>
public record CardDesignDto(string Scope, int? RestaurantId, string Name, bool CanEdit, bool HasOwn, string Front, string Layout, string? Headline, string? Message,
    string? BackMessage, string BackgroundColor, string TextColor, List<CardImageDto> Images, DateTime? UpdatedAt, string? UpdatedBy,
    string BackStyle, string InkColor, string? Website, string? LegalText, string DefaultLegalText,
    List<CardElement>? BackLayout, List<CardElement>? FrontLayout, List<CardElement> StandardBack, List<CardElement> StarterFront, List<CardMergeField> MergeFields, string SampleQr);
public record CardDesignScope(string Scope, int? RestaurantId, string Name, bool CanEdit, bool HasOwn);

// ---- Billing for mailed cards: what a group sees ------------------------------------------------------------

public record MailMonthRow(int RestaurantId, string Restaurant, int Cards, int TotalCents);
/// <param name="Month">yyyy-MM.</param>
public record MailMonth(string Month, string MonthName, int Cards, int TotalCents, List<MailMonthRow> Restaurants);
/// <param name="Kind">card or bank.</param>
/// <param name="Label">Such as "Visa credit card ending 4242"; never a full number.</param>
public record PayMethodDto(string Kind, string? Funding, string Label, DateTime? SetAt, string? SetBy);
public record BillingContact(string? Name, string? Email, string? Street, string? City, string? State, string? Zip);
/// <param name="PayFeeCents">What paying this bill with the saved payment method would add (a credit card or a bank account).</param>
public record InvoiceDto(int Id, string Number, string Month, string MonthName, int Cards, int TotalCents, DateTime IssuedAt, DateOnly DueOn, string Status, bool Overdue,
    DateTime? PaidAt, string? PaidHow, int SurchargeCents, int PayFeeCents, string? PaymentError, List<Services.InvoiceLine> Lines)
{
    /// <summary>How much of the group's credit was used on this bill, and what is still owed after it.</summary>
    public int CreditCents { get; init; }
    public int DueCents { get; init; }
    /// <summary>The restaurant or set of restaurants the bill is for, when the group's month is split into several bills.</summary>
    public string? For { get; init; }
}
/// <summary>How a group has its month billed. Split: one, restaurant or sets. Sets: each restaurant's set name (for "sets").</summary>
public record BillSplitDto(string Split, List<BillSetRow> Sets);
public record BillSetRow(int RestaurantId, string Restaurant, string? Set);
public record BillSplitRequest(string? Split, List<BillSetRequest>? Sets);
public record BillSetRequest(int RestaurantId, [MaxLength(60)] string? Set);
/// <summary>One line of a group's credit account as the group sees it: what, when and how much, without the System Administrator's notes.</summary>
public record GroupCreditLine(DateTime At, string Kind, string Label, int AmountCents, string? InvoiceNumber);
/// <param name="CanManage">A group administrator: may save a payment method, pay bills and change where bills go. Owners only see usage.</param>
public record GroupBillingDto(bool CanManage, bool MailAvailable, bool Test, int CentsPerCard, int LimitCents, int UsedCents, int CardsThisMonth,
    bool PayAvailable, bool PayRequired, bool PayTest, decimal CardFeePercent, decimal BankFeePercent, PayMethodDto? Method, bool AutoPay, BillingContact Contact,
    string BusinessName, string? BusinessEmail, string? PayInstructions, int BillDay, int DueDays, List<MailMonth> Months, List<InvoiceDto> Invoices, int OwedCents)
{
    /// <summary>The group's unused credit, which comes off its next bills by itself.</summary>
    public int CreditCents { get; init; }
    /// <summary>How the group has its month billed, and which set each restaurant is billed with. Administrators only.</summary>
    public BillSplitDto? Bills { get; init; }
    public List<GroupCreditLine> Credits { get; init; } = [];
    /// <summary>What the group is billed for each card it prints itself for a stamp by hand, in cents (0 when nothing is).</summary>
    public int PrintFeeCents { get; init; }
    /// <summary>
    /// Real or test mailing for the group (live, test, or each: each restaurant decides), and whether the site has
    /// fixed it for every group (then the group cannot change it).
    /// </summary>
    public string MailMode { get; init; } = Services.MailChoices.Test;
    public bool MailModeSetBySite { get; init; }
}
/// <param name="ConfirmCents">The amount the person was shown and agreed to; the payment is refused if it no longer matches.</param>
public record PayInvoiceRequest(int ConfirmCents);
public record PaySetupStart(string Url);
public record PaySetupFinish(string? SessionId);
public record AutoPayRequest(bool On);
public record BillingActionResult(bool Ok, string Message, GroupBillingDto Billing);

// ---- Billing for mailed cards: what the System Administrator sees ---------------------------------------------

public record SystemInvoiceDto(int Id, int GroupId, string Group, string Number, string Month, string MonthName, int Cards, int CostCents, int FeeCents, int TotalCents,
    DateTime IssuedAt, DateOnly DueOn, string Status, bool Overdue, DateTime? PaidAt, string? PaidHow, string? PaidNote, string? PaidBy, int SurchargeCents,
    string? PaymentRef, string? PaymentError, DateTime? EmailedAt, string? EmailedTo, DateTime? PrintedAt, string? DeliveryError, List<Services.InvoiceLine> Lines)
{
    public int CreditCents { get; init; }
    public int DueCents { get; init; }
    public string? For { get; init; }
    /// <summary>What Stripe has told the site about this bill's payment, newest first.</summary>
    public List<PaymentNotificationDto> Notifications { get; init; } = [];
}
/// <summary>One line of the credit log, with the System Administrator's details.</summary>
public record GroupCreditDto(int Id, int GroupId, string Group, DateTime At, string Kind, string Label, int AmountCents, string Details, string By, string? InvoiceNumber);
/// <param name="Kind">check, correction, bonus or other.</param>
/// <param name="ConfirmCents">The amount the person was shown and agreed to; refused if it differs from AmountCents.</param>
/// <param name="Key">Made by the page for this one credit, so a repeated request cannot record it twice.</param>
public record AddCreditRequest(int AmountCents, string? Kind, string? Details, int ConfirmCents, string? Key);
public record TakeBackCreditRequest(int AmountCents, string? Details, string? Key);
/// <param name="FeeCents">The group's own fee per card, or null when it uses the site's.</param>
/// <param name="LimitCents">The group's own monthly limit, or null when it uses the site's.</param>
public record GroupBillingRow(int GroupId, string Group, bool Active, int? FeeCents, int EffectiveFeeCents, int? LimitCents, int EffectiveLimitCents, int CardsThisMonth,
    int BilledThisMonthCents, int OwedCents, string? PayLabel, bool AutoPay, string? BillingEmail)
{
    /// <summary>Mailing is switched on for this group (for all its restaurants, or for at least one where each restaurant decides).</summary>
    public bool MailEnabled { get; init; }
    /// <summary>on, off or each (each restaurant decides); and live, test or each for real or test mailing.</summary>
    public string Mail { get; init; } = Services.MailChoices.Off;
    public string MailMode { get; init; } = Services.MailChoices.Test;
    /// <summary>Whether this group must save a payment method before mailing, and whether that is its own rule (null: it follows the site's).</summary>
    public bool PayRequired { get; init; }
    public bool? OwnPayRule { get; init; }
    /// <summary>The group's unused credit.</summary>
    public int CreditCents { get; init; }
}
/// <param name="UnbilledMonths">Finished months that have mailed cards with no bill yet.</param>
/// <summary>
/// One notification from Stripe, for the System Administrator's Billing page. Label says it in plain words; Type is
/// Stripe's own name for it. NeedsALook: a refund or a dispute.
/// </summary>
public record PaymentNotificationDto(int Id, string EventId, string Type, string Label, DateTime ReceivedAt, DateTime? HappenedAt, bool Test, int Deliveries, string? ObjectType, string? ObjectId,
    string? PaymentId, int? InvoiceId, string? Bill, int? GroupId, string? Group, int? AmountCents, string? Currency, string? Status, string? Reason, string? Outcome, bool NeedsALook);
/// <summary>A notification with every data point Stripe sent in it, one to a line.</summary>
public record PaymentNotificationDetail(PaymentNotificationDto Notification, List<Services.DataPoint> DataPoints);
public record SystemBillingDto(Services.BillingSettings Settings, bool MailReady, bool MailTest, bool PayReady, bool PayTest, bool EmailReady, List<GroupBillingRow> Groups,
    List<SystemInvoiceDto> Invoices, List<string> UnbilledMonths, int OwedCents, int OverdueCents)
{
    /// <summary>The credit log, newest first, for the group chosen (or every group).</summary>
    public List<GroupCreditDto> Credits { get; init; } = [];
    /// <summary>Unused credit across every group, and the most one credit can be.</summary>
    public int CreditCents { get; init; }
    public int MaxCreditCents { get; init; }
}
/// <param name="MailEnabled">Switches mailing on or off for the group. Left out, it stays as it is.</param>
/// <param name="PayRule">"site" (follow the site's setting), "required" or "notrequired": whether the group must save a payment method before mailing. Left out, it stays as it is.</param>
public record GroupBillingTermsRequest(int? FeeCents, int? LimitCents, bool? MailEnabled = null, string? PayRule = null);
public record MailRestaurantInfo(int RestaurantId, bool On, bool Live, bool Ready);

/// <summary>
/// An investment group's settings as the System Administrator sets them, with each restaurant's own choices.
/// Mail: on, off or each. MailMode: live, test or each. UsualWay: mail, stamp or each. SignatureForAll: a name for
/// all restaurants, or empty when each restaurant decides. PayRule: site, required or notrequired.
/// </summary>
public record GroupSettingsDto(int GroupId, string Group, string Mail, string MailMode, string SiteMailMode, string UsualWay, string? SignatureForAll, string PayRule,
    bool SiteRequiresPay, bool HasTestKey, bool HasLiveKey, int CreditCents, string? PayLabel, List<RestaurantSettingsDto> Restaurants)
{
    /// <summary>The settings left to the group's own administrators (mail, mode, usual, signature). The rest are the site's answer for the group.</summary>
    public string[] GroupDecides { get; init; } = [];
}
public record RestaurantSettingsDto(int Id, string Name, bool IsActive, bool MailOn, bool MailLive, bool MailByDefault, string? SignatureName, Services.RestaurantRules Applies);
public record GroupSettingsRequest(string? Mail, string? MailMode, string? UsualWay, string? SignatureForAll, string? PayRule, List<RestaurantSettingsRequest>? Restaurants)
{
    /// <summary>The settings to leave to the group's own administrators. Null leaves that as it is.</summary>
    public string[]? GroupDecides { get; init; }
}
/// <summary>The settings as the group's administrators see them. GroupDecides lists the ones they may change (mail, mode, usual, signature).</summary>
public record GroupRulesDto(string Mail, string MailMode, string SiteMailMode, string UsualWay, string? SignatureForAll, string[] GroupDecides);
/// <param name="Signature">all (one name for all restaurants, in SignatureForAll) or each.</param>
public record GroupRulesRequest(string? Mail, string? MailMode, string? UsualWay, string? Signature, string? SignatureForAll);
/// <param name="BySite">The System Administrator's view (their answers are the ones being set); otherwise the group's.</param>
public record PermissionRulesDto(int GroupId, string Group, bool BySite, List<Services.PermissionState> Permissions);
/// <param name="Owners">Site: yes, no or group. Group: yes or no.</param>
/// <param name="Managers">Site: yes, no or group. Group: yes, no or each.</param>
public record PermissionRuleChange(string Name, string? Owners, string? Managers);
public record PermissionRulesRequest(List<PermissionRuleChange>? Rows);
public record RestaurantSettingsRequest(int Id, bool MailOn, bool MailLive, bool MailByDefault, [MaxLength(60)] string? SignatureName);
public record MailModeRequest(string? Mode);
/// <param name="How">check, bank, card, cash or other.</param>
public record MarkPaidRequest(string? How, string? Note, DateOnly? PaidOn);
public record IssueBillsRequest(string? Month);
public record StripeKeyRequest(string? SecretKey);
public record StripeWebhookRequest(string? Secret);
/// <param name="Restricted">The key is a restricted key (rk_), which can only do what it was given permission for.</param>
/// <param name="WebhookEnding">The last four characters of the webhook signing secret; the secret is never returned.</param>
/// <param name="LastNotificationAt">When Stripe's last genuine notification arrived, since the site last started.</param>
public record StripeAccountDto(bool Configured, string Source, string? KeyEnding, bool Test, DateTime? SavedAt, string? SavedBy, int GroupsWithMethod, bool CanEnter,
    bool Restricted, bool WebhookConfigured, string? WebhookEnding, DateTime? LastNotificationAt, string ApiVersion);
public record CardIdsRequest(int[] CommentIds);
public record PostageOutcome(int CommentId, bool Ok, int? Cents, string? LabelUrl, bool Test, bool AlreadyBought, string? Error);
/// <param name="Postage">"bought" (postage printed on the card) or "stamp" (a stamp goes on by hand).</param>
public record PrintedRequest(int[] CommentIds, bool MarkSent = true, string Postage = "stamp");
public record RedeemRequest(DateOnly? RedeemedOn, string? Note);
public record UndoRequest(string? Reason);
/// <summary>
/// The call (or other contact) some days after a card was sent, to check it arrived. Reached = false records an
/// attempt and leaves the follow-up due. Notes hold what was said.
/// </summary>
public record FollowedUpRequest(bool Reached, string? Method, DateOnly? ContactedOn, string? Notes);
public record FollowUpRestaurant(int Id, string Name, int? Days, int EffectiveDays, bool CanSet);
public record FollowUpSummary(int Due, DateOnly? OldestDueOn, int DefaultDays, List<FollowUpRestaurant> Restaurants);
public record FollowUpDaysRequest(int? Days);

// ---- Checking a mailing address with USPS ------------------------------------------------------------------

public record AddressCheckRequest(string? StreetAddress, string? City, string? State, string? Zip);
/// <summary>What has been typed of a guest's address so far, and the restaurant the card is for.</summary>
public record AddressSuggestRequest(int RestaurantId, [MaxLength(200)] string? StreetAddress, [MaxLength(100)] string? City, [MaxLength(40)] string? State, [MaxLength(20)] string? Zip);
/// <param name="Available">Addresses are offered for this restaurant. When false, the form stops asking.</param>
public record AddressSuggestResponse(bool Available, List<Services.AddressPreview> Suggestions);
/// <param name="Available">False when address checking is not set up on this site (the form then shows nothing).</param>
public record AddressCheckResponse(bool Available, string Status, string Message, AddressSuggestion? Suggested);

// ---- An investment group's own postage (EasyPost) account ---------------------------------------------------

public record PostageAccountRequest(string? ApiKey);
/// <param name="KeyEnding">The last four characters of the saved key; the key itself is never returned.</param>
/// <param name="SiteAccountAvailable">The site has its own account, used for groups that have not added one.</param>
public record PostageAccountDto(bool HasOwnAccount, string? KeyEnding, bool LooksLikeTestKey, DateTime? AddedAt, string? AddedBy,
    bool CanAddAccount, bool SiteAccountAvailable, string Provider);

// ---- The site's USPS account for address checking (System Administrator only) ---------------------------------

public record UspsAccountRequest(string? ConsumerKey, string? ConsumerSecret, bool TestEnvironment);
/// <param name="Source">"Saved in the site", "App settings" or "None".</param>
/// <param name="KeyEnding">The last four characters of the Consumer Key; neither the key nor the secret is ever returned.</param>
public record UspsAccountDto(bool Configured, string Source, string? KeyEnding, bool TestEnvironment, DateTime? SavedAt, string? SavedBy, bool CanEnter);
public record ServiceTestResult(bool Ok, string Message);

// ---- PostGrid: the site's mailing account, prices and limits, and its address verification key -----------------

/// <param name="UseFirst">Ask PostGrid before USPS (otherwise PostGrid is the backup for when USPS cannot answer).</param>
public record PostGridAddressRequest(string? ApiKey, bool UseFirst);
public record PostGridAddressOrderRequest(bool UseFirst);
public record PostGridMailKeyRequest(string? ApiKey);
public record PostGridMailAccountDto(bool Configured, string Source, string? KeyEnding, bool Test, DateTime? SavedAt, string? SavedBy);
/// <param name="KeyEnding">The last four characters of the address verification key; the key is never returned.</param>
public record PostGridSiteDto(bool Configured, string Source, string? KeyEnding, bool UseFirst, DateTime? SavedAt, string? SavedBy, bool CanEnter,
    PostGridMailAccountDto Mail, int CardsMailed, int TestCardsMailed, int CardsThisMonth)
{
    /// <summary>The test key and the live key for printing and mailing, each entered on its own. Mail is the live one when there is one, else the test one.</summary>
    public PostGridMailAccountDto MailTestKey { get; init; } = new(false, "None", null, true, null, null);
    public PostGridMailAccountDto MailLiveKey { get; init; } = new(false, "None", null, false, null, null);
}

/// <param name="Month">yyyy-MM.</param>
public record SiteMailStatementRow(int GroupId, string Group, int RestaurantId, string Restaurant, int Cards, int CostCents, int FeeCents, int TotalCents)
{
    /// <summary>How many of the cards the restaurant printed itself (the site's fee only, no cost to the site).</summary>
    public int PrintedCards { get; init; }
}
public record SiteMailStatement(string Month, List<string> Months, List<SiteMailStatementRow> Rows, int Cards, int CostCents, int FeeCents, int TotalCents);
