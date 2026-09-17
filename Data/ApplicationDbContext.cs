using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace PdnodeVote.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Poll> Polls => Set<Poll>();
    public DbSet<PollOption> PollOptions => Set<PollOption>();
    public DbSet<VoteRecord> VoteRecords => Set<VoteRecord>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<CategoryModerator> CategoryModerators => Set<CategoryModerator>();
    public DbSet<CategoryRequest> CategoryRequests => Set<CategoryRequest>();
    public DbSet<CategoryRequestReview> CategoryRequestReviews => Set<CategoryRequestReview>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<PollTag> PollTags => Set<PollTag>();
    public DbSet<PollComment> PollComments => Set<PollComment>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<ContentReport> ContentReports => Set<ContentReport>();
    public DbSet<CategorySubscription> CategorySubscriptions => Set<CategorySubscription>();
    public DbSet<CommentLike> CommentLikes => Set<CommentLike>();
    public DbSet<SystemAnnouncement> SystemAnnouncements => Set<SystemAnnouncement>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Configure Poll and PollOption one-to-many with cascade delete
        builder.Entity<Poll>()
            .HasMany(p => p.Options)
            .WithOne(o => o.Poll)
            .HasForeignKey(o => o.PollId)
            .OnDelete(DeleteBehavior.Cascade);

        // Configure Poll and VoteRecord one-to-many with cascade delete
        builder.Entity<Poll>()
            .HasMany(p => p.Votes)
            .WithOne(v => v.Poll)
            .HasForeignKey(v => v.PollId)
            .OnDelete(DeleteBehavior.Cascade);

        // Configure PollOption and VoteRecord one-to-many
        builder.Entity<PollOption>()
            .HasMany(o => o.Votes)
            .WithOne(v => v.PollOption)
            .HasForeignKey(v => v.PollOptionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Configure Category self-referencing hierarchy
        builder.Entity<Category>()
            .HasMany(c => c.Children)
            .WithOne(c => c.Parent)
            .HasForeignKey(c => c.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        // Configure Poll and Category
        builder.Entity<Poll>()
            .HasOne(p => p.Category)
            .WithMany(c => c.Polls)
            .HasForeignKey(p => p.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        // Configure CategoryModerator
        builder.Entity<CategoryModerator>()
            .HasOne(cm => cm.Category)
            .WithMany(c => c.Moderators)
            .HasForeignKey(cm => cm.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<CategoryModerator>()
            .HasIndex(cm => new { cm.CategoryId, cm.UserId })
            .IsUnique();

        // Configure CategoryRequest and Review
        builder.Entity<CategoryRequest>()
            .HasMany(cr => cr.Reviews)
            .WithOne(r => r.Request)
            .HasForeignKey(r => r.RequestId)
            .OnDelete(DeleteBehavior.Cascade);

        // Configure PollTag composite primary key
        builder.Entity<PollTag>()
            .HasKey(pt => new { pt.PollId, pt.TagId });

        builder.Entity<PollTag>()
            .HasOne(pt => pt.Poll)
            .WithMany(p => p.PollTags)
            .HasForeignKey(pt => pt.PollId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<PollTag>()
            .HasOne(pt => pt.Tag)
            .WithMany(t => t.PollTags)
            .HasForeignKey(pt => pt.TagId)
            .OnDelete(DeleteBehavior.Cascade);

        // Configure PollComment
        builder.Entity<PollComment>()
            .HasOne(c => c.Poll)
            .WithMany(p => p.Comments)
            .HasForeignKey(c => c.PollId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<PollComment>()
            .HasOne(c => c.ParentComment)
            .WithMany(c => c.Replies)
            .HasForeignKey(c => c.ParentCommentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Configure CategorySubscription composite primary key
        builder.Entity<CategorySubscription>()
            .HasKey(cs => new { cs.UserId, cs.CategoryId });

        builder.Entity<CategorySubscription>()
            .HasOne(cs => cs.User)
            .WithMany()
            .HasForeignKey(cs => cs.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<CategorySubscription>()
            .HasOne(cs => cs.Category)
            .WithMany()
            .HasForeignKey(cs => cs.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        // Configure Notification indexes
        builder.Entity<Notification>()
            .HasIndex(n => new { n.UserId, n.IsRead, n.CreatedAt });

        // Configure ContentReport indexes and relationships
        builder.Entity<ContentReport>()
            .HasIndex(r => new { r.Status, r.CreatedAt });

        builder.Entity<ContentReport>()
            .HasOne(r => r.Poll)
            .WithMany()
            .HasForeignKey(r => r.PollId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ContentReport>()
            .HasOne(r => r.Comment)
            .WithMany()
            .HasForeignKey(r => r.CommentId)
            .OnDelete(DeleteBehavior.Cascade);

        // Index optimizations.
        // (PollId, UserId) is UNIQUE so concurrent submits of an authenticated vote cannot both pass
        // the "already voted" check and insert duplicates. Guest votes have UserId = NULL and are
        // unaffected (SQLite treats NULLs as distinct), which is why the IP guard below exists too.
        builder.Entity<VoteRecord>()
            .HasIndex(v => new { v.PollId, v.UserId })
            .IsUnique();

        builder.Entity<VoteRecord>()
            .HasIndex(v => new { v.PollId, v.IpAddress });

        // A reviewer may only review a given board request once; the service's "already reviewed"
        // check is a read-then-write and needs the database to back it up.
        builder.Entity<CategoryRequestReview>()
            .HasIndex(r => new { r.RequestId, r.ReviewerId })
            .IsUnique();

        builder.Entity<Poll>()
            .HasIndex(p => p.Status);

        // The public feed always sorts by (IsPinned DESC, CreatedAt DESC) over the Approved subset, so
        // give that path a covering index instead of relying on three single-column indexes.
        builder.Entity<Poll>()
            .HasIndex(p => new { p.Status, p.IsPinned, p.CreatedAt });

        builder.Entity<Poll>()
            .HasIndex(p => p.IsPinned);

        builder.Entity<Poll>()
            .HasIndex(p => p.CreatorId);

        builder.Entity<Poll>()
            .HasIndex(p => p.CategoryId);

        builder.Entity<Tag>()
            .HasIndex(t => t.Name)
            .IsUnique();

        // Category slugs are generated from the name with a Ticks-suffix fallback for collisions; that
        // check is a read-then-write, so make the database enforce uniqueness too.
        builder.Entity<Category>()
            .HasIndex(c => c.Slug)
            .IsUnique();

        builder.Entity<PollComment>()
            .HasIndex(c => new { c.PollId, c.Status, c.CreatedAt });

        builder.Entity<ApplicationUser>()
            .HasIndex(u => u.CreatedAt);

        builder.Entity<ApplicationUser>()
            .HasIndex(u => u.IsBanned);

        // Configure CommentLike composite key
        builder.Entity<CommentLike>()
            .HasKey(cl => new { cl.CommentId, cl.UserId });

        builder.Entity<CommentLike>()
            .HasOne(cl => cl.Comment)
            .WithMany(c => c.Likes)
            .HasForeignKey(cl => cl.CommentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<CommentLike>()
            .HasOne(cl => cl.User)
            .WithMany()
            .HasForeignKey(cl => cl.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
