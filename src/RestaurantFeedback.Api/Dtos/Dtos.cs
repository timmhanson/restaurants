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
    public static UserDto From(AppUser u) => new(
        u.Id, u.Username, u.DisplayName, u.Email, u.Role.ToString(), RoleTitles.Of(u.Role), u.GroupId, u.Group?.Name,
        (int)(RoleTitles.HasAllPermissions(u.Role) ? ManagerPermission.All : u.Permissions),
        PermissionNamesOf(RoleTitles.HasAllPermissions(u.Role) ? ManagerPermission.All : u.Permissions),
        u.UserRestaurants.Select(x => x.RestaurantId).OrderBy(x => x).ToArray(),
        u.IsActive, u.MustChangePassword, u.CreatedAt, u.LastLoginAt);

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
    /// <summary>An owner may mark this restaurant as theirs (no owner yet, and no investment group administrator to decide).</summary>
    public bool? CanClaim { get; init; }

    public static RestaurantDto From(Restaurant r) => new(r.Id, r.Name, r.StreetAddress, r.City, r.State, r.Zip, r.Phone, r.IsActive);
}

public record StaffMember(int Id, string DisplayName, string Username, string? Email, string Role, string[] PermissionNames, bool Assigned, bool IsActive, string[] OtherRestaurants);
public record StaffResponse(RestaurantDto Restaurant, bool CanManage, List<StaffMember> Owners, List<StaffMember> Managers, List<StaffMember> TeamLogins)
{
    /// <summary>Investment Group administrators see every restaurant without being assigned; listed for information.</summary>
    public List<PersonRef> GroupAdmins { get; init; } = [];
    /// <summary>False when the group has an investment group administrator, so a restaurant may have no specific owner.</summary>
    public bool OwnerRequired { get; init; } = true;
}
public record StaffRequest(int[] UserIds);
public record OwnershipRequest(bool Owned);

public class RestaurantRequest
{
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    public string? StreetAddress { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip { get; set; }
    public string? Phone { get; set; }
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
    public string? PostageLabelUrl { get; init; }
    public string? PostageTrackingCode { get; init; }
    public bool PostageTest { get; init; }
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
        PostageLabelUrl = c.PostageLabelUrl, PostageTrackingCode = c.PostageTrackingCode, PostageTest = c.PostageTest, RedeemedOn = c.RedeemedOn,
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

public record PrintConfig(bool PostageAvailable, string PostageProvider, bool CanBuyPostage, int MaxCentsPerCard, string MailedMethod);
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
