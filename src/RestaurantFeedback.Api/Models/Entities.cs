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
    All = ViewComments | EditComments | DeleteComments | MarkResponded | ViewAnalytics | ExportComments | ViewAudit | ManageTeamLogin
}

/// <summary>An investment / ownership group. The tenant boundary for users and restaurants.</summary>
public class OwnerGroup
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
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
    public bool IsActive { get; set; } = true;
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
