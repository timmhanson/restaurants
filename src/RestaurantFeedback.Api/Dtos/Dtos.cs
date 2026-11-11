using System.ComponentModel.DataAnnotations;
using RestaurantFeedback.Api.Models;

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
    /// Only System and Group Administrators may turn this off; team logins never have to.
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
    /// <summary>Group administrators to create with the group. A new group needs at least one.</summary>
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
    /// <summary>An owner may mark this restaurant as theirs (no owner yet, and no group administrator to decide).</summary>
    public bool? CanClaim { get; init; }

    public static RestaurantDto From(Restaurant r) => new(r.Id, r.Name, r.StreetAddress, r.City, r.State, r.Zip, r.Phone, r.IsActive);
}

public record StaffMember(int Id, string DisplayName, string Username, string? Email, string Role, string[] PermissionNames, bool Assigned, bool IsActive, string[] OtherRestaurants);
public record StaffResponse(RestaurantDto Restaurant, bool CanManage, List<StaffMember> Owners, List<StaffMember> Managers, List<StaffMember> TeamLogins)
{
    /// <summary>Group administrators see every restaurant without being assigned; listed for information.</summary>
    public List<PersonRef> GroupAdmins { get; init; } = [];
    /// <summary>False when the group has a group administrator, so a restaurant may have no specific owner.</summary>
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
}

public record CommentSummary(
    int Id, int RestaurantId, string RestaurantName, string GuestFirstName, string GuestLastName, string? Phone,
    string? StreetAddress, string? City, string? State, string? Zip, string? CouponOffer, DateOnly DateIssued, DateOnly ExpiresOn,
    string? AuthorizedBy, DateOnly VisitDate, string DayOfWeek, string Reason, string? ReasonDetails, string? Notes,
    bool Responded, DateTime? RespondedAt, string? RespondedByName, DateTime CreatedAt, string CreatedByName, DateTime? UpdatedAt,
    string GuestKey, int GuestCommentCount)
{
    public string? Source { get; init; }
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
        Source = c.Source, ReplyMethod = c.ReplyMethod, RepliedOn = c.RepliedOn, LastActivityAt = c.LastActivityAt
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
public record CardStats(int Cards, int Replied, int Waiting, int WaitingOverAWeek, double? AvgDaysToReply, int Smg, int Website, int Phone, int SourceNotRecorded);

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
