using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Models;
using RestaurantFeedback.Api.Services;

namespace RestaurantFeedback.Api.Data;

public class SeedOptions
{
    /// <summary>Super admin account that administers every investment group. Created if missing.</summary>
    public string SuperAdminUsername { get; set; } = "";
    public string SuperAdminDisplayName { get; set; } = "Super Admin";
    public string? SuperAdminPassword { get; set; }

    /// <summary>Optional first investment group, restaurant and owner for a brand-new database.</summary>
    public string GroupName { get; set; } = "Investment Group";
    public string RestaurantName { get; set; } = "Culver's of Joliet";
    public string RestaurantAddress { get; set; } = "1500 Illinois Hwy 59";
    public string RestaurantCity { get; set; } = "Joliet";
    public string RestaurantState { get; set; } = "IL";
    public string RestaurantZip { get; set; } = "60431";
    public string OwnerUsername { get; set; } = "owner";
    public string OwnerDisplayName { get; set; } = "Owner";
    public string? OwnerPassword { get; set; }
}

public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Seed");
        var seed = scope.ServiceProvider.GetRequiredService<SeedOptions>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();
        var settings = scope.ServiceProvider.GetRequiredService<IGroupSettingsStore>();

        await db.Database.MigrateAsync();

        // Super admin: always ensured, so a fresh deployment can be administered.
        if (!string.IsNullOrWhiteSpace(seed.SuperAdminUsername) && !string.IsNullOrWhiteSpace(seed.SuperAdminPassword))
        {
            var username = seed.SuperAdminUsername.Trim().ToLowerInvariant();
            if (!await db.Users.AnyAsync(u => u.Username == username))
            {
                var admin = new AppUser { Username = username, DisplayName = seed.SuperAdminDisplayName, Email = username.Contains('@') ? username : null, Role = UserRole.SuperAdmin, Permissions = ManagerPermission.All };
                admin.PasswordHash = hasher.HashPassword(admin, seed.SuperAdminPassword);
                db.Users.Add(admin);
                db.AuditLogs.Add(new AuditLog { UserName = "system", Action = "Seeded", EntityType = "User", Details = $"Created super admin '{username}'." });
                await db.SaveChangesAsync();
                log.LogInformation("Seeded super admin {User}", username);
            }
        }
        else if (!await db.Users.AnyAsync(u => u.Role == UserRole.SuperAdmin))
        {
            log.LogWarning("No super admin exists and Seed:SuperAdminUsername / Seed:SuperAdminPassword are not configured.");
        }

        if (await db.Groups.AnyAsync() || string.IsNullOrWhiteSpace(seed.OwnerPassword)) return;

        var group = new OwnerGroup { Name = seed.GroupName };
        var restaurant = new Restaurant
        {
            Group = group, Name = seed.RestaurantName, StreetAddress = seed.RestaurantAddress,
            City = seed.RestaurantCity, State = seed.RestaurantState, Zip = seed.RestaurantZip
        };
        var owner = new AppUser
        {
            Group = group, Username = seed.OwnerUsername.ToLowerInvariant(), DisplayName = seed.OwnerDisplayName, Role = UserRole.Owner,
            Permissions = ManagerPermission.All
        };
        owner.PasswordHash = hasher.HashPassword(owner, seed.OwnerPassword);
        owner.UserRestaurants.Add(new UserRestaurant { Restaurant = restaurant });
        db.AddRange(group, restaurant, owner);
        await db.SaveChangesAsync();

        await settings.SaveAsync(group.Id, new GroupSettings { GroupDisplayName = seed.GroupName, UpdatedAt = DateTime.UtcNow, UpdatedBy = "seed" });
        db.AuditLogs.Add(new AuditLog { UserName = "system", GroupId = group.Id, Action = "Seeded", Details = $"Created group '{group.Name}', restaurant '{restaurant.Name}' and owner '{owner.Username}'." });
        await db.SaveChangesAsync();
        log.LogInformation("Seeded group {Group} with owner {Owner}", group.Name, owner.Username);
    }
}
