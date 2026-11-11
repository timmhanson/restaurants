namespace RestaurantFeedback.Api.Models;

public enum UserRole
{
    /// <summary>Administers every investment group. Not a member of any group.</summary>
    SuperAdmin = 0,
    Owner = 1,
    Manager = 2,
    TeamMember = 3
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

    // What happened
    public DateOnly VisitDate { get; set; }
    /// <summary>Reason category. The allowed list lives in the group's settings blob.</summary>
    public string Reason { get; set; } = "";
    public string? ReasonDetails { get; set; }
    public string? Notes { get; set; }

    // Follow-up
    public bool Responded { get; set; }
    public DateTime? RespondedAt { get; set; }
    public int? RespondedByUserId { get; set; }
    public string? RespondedByName { get; set; }

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
