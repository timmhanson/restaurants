using Microsoft.EntityFrameworkCore;
using RestaurantFeedback.Api.Models;

namespace RestaurantFeedback.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<OwnerGroup> Groups => Set<OwnerGroup>();
    public DbSet<Restaurant> Restaurants => Set<Restaurant>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<UserRestaurant> UserRestaurants => Set<UserRestaurant>();
    public DbSet<GuestComment> Comments => Set<GuestComment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>().HasIndex(u => u.Username).IsUnique();
        b.Entity<AppUser>().Property(u => u.Username).HasMaxLength(100);
        b.Entity<AppUser>().Property(u => u.DisplayName).HasMaxLength(200);

        b.Entity<UserRestaurant>().HasKey(ur => new { ur.UserId, ur.RestaurantId });
        b.Entity<UserRestaurant>().HasOne(ur => ur.User).WithMany(u => u.UserRestaurants).HasForeignKey(ur => ur.UserId);
        b.Entity<UserRestaurant>().HasOne(ur => ur.Restaurant).WithMany(r => r.UserRestaurants).HasForeignKey(ur => ur.RestaurantId);

        b.Entity<GuestComment>().HasIndex(c => new { c.RestaurantId, c.VisitDate });
        b.Entity<GuestComment>().HasIndex(c => c.GuestKey);
        b.Entity<GuestComment>().HasIndex(c => c.PhoneNormalized);
        b.Entity<GuestComment>().HasQueryFilter(c => !c.IsDeleted);

        b.Entity<AuditLog>().HasIndex(a => a.Timestamp);
        b.Entity<AuditLog>().HasIndex(a => new { a.GroupId, a.RestaurantId });
    }
}
