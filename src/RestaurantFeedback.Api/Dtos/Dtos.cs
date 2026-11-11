using System.ComponentModel.DataAnnotations;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Dtos;

public record LoginRequest([Required] string Username, [Required] string Password);
public record LoginResponse(string Token, DateTime ExpiresAt, UserDto User, bool MustChangePassword);
public record ChangePasswordRequest([Required] string CurrentPassword, [Required, MinLength(8)] string NewPassword, [Required, RegularExpression(@"^\d{6}$")] string Code);
public record CodeSentResponse(string EmailMasked, DateTime ExpiresAt, int ResendAfterSeconds);

public record UserDto(
    int Id, string Username, string DisplayName, string? Email, string Role, int? GroupId, string? GroupName, int Permissions, string[] PermissionNames,
    int[] RestaurantIds, bool IsActive, bool MustChangePassword, DateTime CreatedAt, DateTime? LastLoginAt)
{
    public static UserDto From(AppUser u) => new(
        u.Id, u.Username, u.DisplayName, u.Email, u.Role.ToString(), u.GroupId, u.Group?.Name,
        (int)(u.Role is UserRole.Owner or UserRole.SuperAdmin ? ManagerPermission.All : u.Permissions),
        PermissionNamesOf(u.Role is UserRole.Owner or UserRole.SuperAdmin ? ManagerPermission.All : u.Permissions),
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
}

public record SetPasswordRequest([Required, MinLength(8)] string Password);

public record GroupDto(int Id, string Name, bool IsActive, DateTime CreatedAt, int RestaurantCount, int UserCount, int CommentCount);

public class GroupRequest
{
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    /// <summary>Optionally create the group's first owner at the same time.</summary>
    public UserRequest? FirstOwner { get; set; }
}

public record RestaurantDto(int Id, string Name, string? StreetAddress, string? City, string? State, string? Zip, string? Phone, bool IsActive)
{
    public static RestaurantDto From(Restaurant r) => new(r.Id, r.Name, r.StreetAddress, r.City, r.State, r.Zip, r.Phone, r.IsActive);
}

public class RestaurantRequest
{
    [Required, MaxLength(200)] public string Name { get; set; } = "";
    public string? StreetAddress { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Zip { get; set; }
    public string? Phone { get; set; }
    public bool IsActive { get; set; } = true;
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
    public static CommentSummary From(GuestComment c, int guestCommentCount) => new(
        c.Id, c.RestaurantId, c.Restaurant?.Name ?? "", c.GuestFirstName, c.GuestLastName, c.Phone,
        c.StreetAddress, c.City, c.State, c.Zip, c.CouponOffer, c.DateIssued, c.ExpiresOn,
        c.AuthorizedBy, c.VisitDate, c.VisitDate.DayOfWeek.ToString(), c.Reason, c.ReasonDetails, c.Notes,
        c.Responded, c.RespondedAt, c.RespondedByName, c.CreatedAt, c.CreatedByName, c.UpdatedAt,
        c.GuestKey, guestCommentCount);
}

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
    List<RepeatGuestItem> TopRepeatGuests);

public record CountItem(string Label, int Count, int? Responded = null);
public record RepeatGuestItem(string GuestKey, string Name, string? Phone, string? Address, int Count, DateOnly LastVisit, string[] Restaurants, string[] Reasons);
