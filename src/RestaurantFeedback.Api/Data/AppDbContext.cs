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
    public DbSet<CommentActivity> CommentActivities => Set<CommentActivity>();
    public DbSet<InteractionLog> InteractionLogs => Set<InteractionLog>();
    public DbSet<SiteSetting> SiteSettings => Set<SiteSetting>();

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

        // Comments are only ever soft-deleted, so activity rows keep a plain id rather than a navigation.
        b.Entity<CommentActivity>().HasIndex(a => new { a.CommentId, a.CreatedAt });
        b.Entity<CommentActivity>().Property(a => a.Text).HasMaxLength(2000);
        b.Entity<CommentActivity>().Property(a => a.Method).HasMaxLength(100);

        b.Entity<AuditLog>().HasIndex(a => a.Timestamp);
        b.Entity<AuditLog>().HasIndex(a => new { a.GroupId, a.RestaurantId });

        b.Entity<SiteSetting>().HasKey(s => s.Key);
        b.Entity<SiteSetting>().Property(s => s.Key).HasMaxLength(100);

        var log = b.Entity<InteractionLog>();
        log.HasIndex(l => l.At);
        log.HasIndex(l => new { l.GroupId, l.At });
        log.HasIndex(l => new { l.RestaurantId, l.At });
        log.HasIndex(l => new { l.UserId, l.At });
        log.Property(l => l.Kind).HasMaxLength(20);
        log.Property(l => l.UserName).HasMaxLength(100);
        log.Property(l => l.DisplayName).HasMaxLength(200);
        log.Property(l => l.Method).HasMaxLength(10);
        log.Property(l => l.Path).HasMaxLength(400);
        log.Property(l => l.Action).HasMaxLength(200);
        log.Property(l => l.Detail).HasMaxLength(500);
        log.Property(l => l.SessionId).HasMaxLength(64);
        log.Property(l => l.IpAddress).HasMaxLength(64);
        log.Property(l => l.UserAgent).HasMaxLength(300);
    }
}
