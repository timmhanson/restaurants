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
    public DbSet<CardDesign> CardDesigns => Set<CardDesign>();
    public DbSet<CardImage> CardImages => Set<CardImage>();
    public DbSet<GroupBilling> GroupBilling => Set<GroupBilling>();
    public DbSet<MailInvoice> MailInvoices => Set<MailInvoice>();
    public DbSet<GroupCredit> GroupCredits => Set<GroupCredit>();
    public DbSet<StripeEvent> StripeEvents => Set<StripeEvent>();
    public DbSet<CardSend> CardSends => Set<CardSend>();
    public DbSet<AiRequest> AiRequests => Set<AiRequest>();
    public DbSet<AttentionDismissal> AttentionDismissals => Set<AttentionDismissal>();

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

        var design = b.Entity<CardDesign>();
        design.HasIndex(d => new { d.GroupId, d.RestaurantId });
        design.HasOne<OwnerGroup>().WithMany().HasForeignKey(d => d.GroupId).OnDelete(DeleteBehavior.Cascade);
        design.HasOne<Restaurant>().WithMany().HasForeignKey(d => d.RestaurantId).OnDelete(DeleteBehavior.Cascade);
        design.Property(d => d.Front).HasMaxLength(20);
        design.Property(d => d.Layout).HasMaxLength(20);
        design.Property(d => d.Headline).HasMaxLength(80);
        design.Property(d => d.Message).HasMaxLength(300);
        design.Property(d => d.BackMessage).HasMaxLength(200);
        design.Property(d => d.BackgroundColor).HasMaxLength(9);
        design.Property(d => d.TextColor).HasMaxLength(9);
        design.Property(d => d.BackStyle).HasMaxLength(20);
        design.Property(d => d.InkColor).HasMaxLength(9);
        design.Property(d => d.Website).HasMaxLength(80);
        design.Property(d => d.LegalText).HasMaxLength(500);
        var image = b.Entity<CardImage>();
        image.HasOne(i => i.Design).WithMany(d => d.Images).HasForeignKey(i => i.DesignId).OnDelete(DeleteBehavior.Cascade);
        image.HasIndex(i => new { i.GroupId, i.Hash });
        image.Property(i => i.Kind).HasMaxLength(20);
        image.Property(i => i.ContentType).HasMaxLength(40);
        image.Property(i => i.Hash).HasMaxLength(40);
        b.Entity<GuestComment>().HasIndex(c => c.MailId);
        b.Entity<GuestComment>().HasIndex(c => c.PublicId).IsUnique();

        var billing = b.Entity<GroupBilling>();
        billing.HasKey(g => g.GroupId);
        billing.Property(g => g.GroupId).ValueGeneratedNever();
        billing.HasOne<OwnerGroup>().WithOne().HasForeignKey<GroupBilling>(g => g.GroupId).OnDelete(DeleteBehavior.Cascade);
        billing.Property(g => g.BillingName).HasMaxLength(200);
        billing.Property(g => g.BillingEmail).HasMaxLength(200);
        var invoice = b.Entity<MailInvoice>();
        invoice.HasIndex(i => new { i.GroupId, i.Month, i.Unit }).IsUnique();
        invoice.Property(i => i.Unit).HasMaxLength(80);
        invoice.Property(i => i.UnitName).HasMaxLength(200);
        invoice.HasIndex(i => i.Status);
        invoice.HasOne<OwnerGroup>().WithMany().HasForeignKey(i => i.GroupId).OnDelete(DeleteBehavior.Cascade);
        invoice.Property(i => i.Month).HasMaxLength(7);
        invoice.Property(i => i.Number).HasMaxLength(40);
        invoice.Property(i => i.Status).HasMaxLength(20);
        b.Entity<AttentionDismissal>().HasIndex(d => new { d.UserId, d.Key }).IsUnique();
        var ai = b.Entity<AiRequest>();
        ai.HasIndex(r => r.RequestKey).IsUnique();
        ai.HasIndex(r => new { r.GroupId, r.At });
        ai.HasIndex(r => r.CommentId);
        ai.Property(r => r.RequestKey).HasMaxLength(64);
        var send = b.Entity<CardSend>();
        send.HasIndex(s => s.CommentId);
        send.HasIndex(s => s.MailId);
        send.HasOne(s => s.Comment).WithMany().HasForeignKey(s => s.CommentId).OnDelete(DeleteBehavior.Cascade);
        send.Property(s => s.Method).HasMaxLength(20);
        send.Property(s => s.Outcome).HasMaxLength(20);
        send.Property(s => s.MailId).HasMaxLength(120);
        var stripeEvent = b.Entity<StripeEvent>();
        stripeEvent.HasIndex(e => e.EventId).IsUnique();
        stripeEvent.HasIndex(e => e.InvoiceId);
        stripeEvent.HasIndex(e => e.ReceivedAt);
        stripeEvent.Property(e => e.EventId).HasMaxLength(120);
        stripeEvent.Property(e => e.Type).HasMaxLength(120);
        stripeEvent.Property(e => e.ObjectId).HasMaxLength(120);
        stripeEvent.Property(e => e.PaymentId).HasMaxLength(120);
        var credit = b.Entity<GroupCredit>();
        credit.HasIndex(c => c.GroupId);
        credit.HasIndex(c => c.Key).IsUnique();
        credit.HasOne<OwnerGroup>().WithMany().HasForeignKey(c => c.GroupId).OnDelete(DeleteBehavior.Cascade);
        credit.Property(c => c.Kind).HasMaxLength(20);
        credit.Property(c => c.Details).HasMaxLength(500);
        credit.Property(c => c.CreatedBy).HasMaxLength(200);
        credit.Property(c => c.Key).HasMaxLength(64);
        invoice.Property(i => i.PaidNote).HasMaxLength(500);

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
