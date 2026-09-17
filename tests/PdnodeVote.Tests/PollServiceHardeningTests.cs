using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PdnodeVote.Data;
using PdnodeVote.Services;
using Xunit;
using PollStatus = PdnodeVote.Data.PollStatus;
using ResultVisibility = PdnodeVote.Data.ResultVisibility;
using UpdatePollRequest = PdnodeVote.Client.Models.UpdatePollRequest;

namespace PdnodeVote.Tests;

/// <summary>
/// Regression tests for the PollService / CommentService hardening batch from TODO-BUGS.md:
/// M2 (missing state-machine guards), M3 (missing server-side length/count validation),
/// M4 (board PostPermission skipped on edit), M7 (Tag.UsageCount only ever grew),
/// P2 (comment page N+1), P3 (pending-review queue N+1) and P4 (unclamped pageSize).
/// </summary>
public class PollServiceHardeningTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _dbContext;
    private readonly TestDbContextFactory _factory;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly PollService _pollService;
    private readonly CommentService _commentService;

    public PollServiceHardeningTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Log(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _dbContext = new ApplicationDbContext(options);
        _dbContext.Database.EnsureCreated();

        _factory = new TestDbContextFactory(options);

        _userManager = new UserManager<ApplicationUser>(
            new UserStore<ApplicationUser>(_dbContext),
            Options.Create(new IdentityOptions()),
            new PasswordHasher<ApplicationUser>(),
            new IUserValidator<ApplicationUser>[0],
            new IPasswordValidator<ApplicationUser>[0],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);

        _roleManager = new RoleManager<IdentityRole>(
            new RoleStore<IdentityRole>(_dbContext),
            new IRoleValidator<IdentityRole>[0],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            NullLogger<RoleManager<IdentityRole>>.Instance);

        _pollService = new PollService(_factory, new TestEmailNotificationService(), _userManager);

        var notifier = new PollEventNotifier();
        _commentService = new CommentService(_factory, _userManager, notifier, new NotificationService(_factory, notifier));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    // ------------------------------------------------------------------ helpers

    private async Task<ApplicationUser> CreateUserAsync(string prefix, DateTime? createdAt = null)
    {
        var user = new ApplicationUser
        {
            UserName = $"{prefix}{Guid.NewGuid():N}@test.com",
            CreatedAt = createdAt ?? DateTime.UtcNow
        };
        user.Email = user.UserName;
        await _userManager.CreateAsync(user, "pw123");
        return user;
    }

    private async Task<ApplicationUser> CreateRoleUserAsync(string role)
    {
        if (!await _roleManager.RoleExistsAsync(role))
        {
            await _roleManager.CreateAsync(new IdentityRole(role));
        }

        var user = await CreateUserAsync(role.ToLowerInvariant());
        await _userManager.AddToRoleAsync(user, role);
        return user;
    }

    private async Task<int> CreatePollAsync(
        ApplicationUser author,
        string title = "Poll title",
        string? description = null,
        IEnumerable<string>? options = null,
        IEnumerable<string>? tags = null,
        int? categoryId = null,
        bool approved = false)
    {
        var optionList = (options ?? new[] { "One", "Two" }).ToList();
        var (ok, message, pollId) = await _pollService.CreatePollAsync(
            title, description, author.Id, false, false, 1, ResultVisibility.AlwaysPublic, null,
            optionList, categoryId, tags?.ToList());

        Assert.True(ok, message);

        if (approved)
        {
            var entity = await _dbContext.Polls.FirstAsync(p => p.Id == pollId);
            entity.Status = PollStatus.Approved;
            await _dbContext.SaveChangesAsync();
        }

        if (tags != null)
        {
            // Keep the shared context's identity map from serving a stale UsageCount after edits.
            _dbContext.ChangeTracker.Clear();
        }

        return pollId;
    }

    private async Task<Category> CreateCategoryAsync(CategoryPostPermission permission)
    {
        var category = new Category
        {
            Name = "Restricted board",
            Slug = $"board-{Guid.NewGuid():N}",
            PostPermission = permission
        };
        _dbContext.Categories.Add(category);
        await _dbContext.SaveChangesAsync();
        return category;
    }

    private async Task<int> TagUsageAsync(string tagName)
    {
        await using var context = _factory.CreateDbContext();
        return await context.Tags.AsNoTracking()
            .Where(t => t.Name == tagName)
            .Select(t => t.UsageCount)
            .FirstAsync();
    }

    private async Task SetStatusAsync(int pollId, PollStatus status)
    {
        var poll = await _dbContext.Polls.FirstAsync(p => p.Id == pollId);
        poll.Status = status;
        await _dbContext.SaveChangesAsync();
    }

    private async Task<PollStatus> StatusOfAsync(int pollId)
    {
        await using var context = _factory.CreateDbContext();
        return await context.Polls.AsNoTracking()
            .Where(p => p.Id == pollId)
            .Select(p => p.Status)
            .FirstAsync();
    }

    // ------------------------------------------------------------------ M2

    [Fact]
    public async Task ApprovePoll_RefusesRemovedPoll()
    {
        // A moderator could re-publish removed content just by approving it again.
        var moderator = await CreateRoleUserAsync("Moderator");
        var author = await CreateUserAsync("author");
        var pollId = await CreatePollAsync(author);

        var (removed, removeMsg) = await _pollService.RemovePollAsync(pollId, moderator.Id, "spam");
        Assert.True(removed, removeMsg);

        var (ok, message) = await _pollService.ApprovePollAsync(pollId, moderator.Id);

        Assert.False(ok);
        Assert.Contains("Removed", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PollStatus.Removed, await StatusOfAsync(pollId));
    }

    [Fact]
    public async Task ApprovePoll_AllowsReturnedForRevisionPoll()
    {
        var moderator = await CreateRoleUserAsync("Moderator");
        var author = await CreateUserAsync("author");
        var pollId = await CreatePollAsync(author);

        var (returned, returnMsg) = await _pollService.ReturnPollForRevisionAsync(pollId, moderator.Id, "please fix");
        Assert.True(returned, returnMsg);

        var (ok, message) = await _pollService.ApprovePollAsync(pollId, moderator.Id);

        Assert.True(ok, message);
        Assert.Equal(PollStatus.Approved, await StatusOfAsync(pollId));
    }

    [Fact]
    public async Task ReturnForRevision_RefusesRemovedPoll()
    {
        var moderator = await CreateRoleUserAsync("Moderator");
        var author = await CreateUserAsync("author");
        var pollId = await CreatePollAsync(author);

        await _pollService.RemovePollAsync(pollId, moderator.Id, "spam");

        var (ok, message) = await _pollService.ReturnPollForRevisionAsync(pollId, moderator.Id, "please fix");

        Assert.False(ok);
        Assert.Equal(PollStatus.Removed, await StatusOfAsync(pollId));
    }

    [Fact]
    public async Task ArchivePoll_OnlyAcceptsApprovedPolls()
    {
        var admin = await CreateRoleUserAsync("Admin");
        var author = await CreateUserAsync("author");

        var pendingPollId = await CreatePollAsync(author);
        var (pendingOk, pendingMessage) = await _pollService.ArchivePollAsync(pendingPollId, admin.Id, "cleanup");
        Assert.False(pendingOk);
        Assert.Contains("Only approved polls", pendingMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PollStatus.PendingReview, await StatusOfAsync(pendingPollId));

        await _pollService.RemovePollAsync(pendingPollId, admin.Id, "spam");
        var (removedOk, _) = await _pollService.ArchivePollAsync(pendingPollId, admin.Id, "cleanup");
        Assert.False(removedOk);
        Assert.Equal(PollStatus.Removed, await StatusOfAsync(pendingPollId));

        var approvedPollId = await CreatePollAsync(author, approved: true);
        var (approvedOk, approvedMessage) = await _pollService.ArchivePollAsync(approvedPollId, admin.Id, "done");
        Assert.True(approvedOk, approvedMessage);
        Assert.Equal(PollStatus.Archived, await StatusOfAsync(approvedPollId));
    }

    [Fact]
    public async Task TogglePin_RefusesRemovedPoll()
    {
        var admin = await CreateRoleUserAsync("Admin");
        var author = await CreateUserAsync("author");
        var pollId = await CreatePollAsync(author);

        await _pollService.RemovePollAsync(pollId, admin.Id, "spam");

        var (ok, message, isPinned) = await _pollService.TogglePinPollAsync(pollId, admin.Id);

        Assert.False(ok);
        Assert.False(isPinned);
        Assert.Contains("Removed", message, StringComparison.OrdinalIgnoreCase);

        var reloaded = await _dbContext.Polls.AsNoTracking().FirstAsync(p => p.Id == pollId);
        Assert.False(reloaded.IsPinned);
    }

    // ------------------------------------------------------------------ M3

    [Fact]
    public async Task CreatePoll_RejectsTitleLongerThanLimit()
    {
        var author = await CreateUserAsync("author");
        var tooLong = new string('x', PollService.MaxTitleLength + 1);

        var (ok, message, pollId) = await _pollService.CreatePollAsync(
            tooLong, null, author.Id, false, false, 1, ResultVisibility.AlwaysPublic, null,
            new List<string> { "A", "B" });

        Assert.False(ok);
        Assert.Equal(0, pollId);
        Assert.Contains("title", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await _dbContext.Polls.CountAsync());
    }

    [Fact]
    public async Task CreatePoll_RejectsDescriptionLongerThanLimit()
    {
        var author = await CreateUserAsync("author");
        var tooLong = new string('d', PollService.MaxDescriptionLength + 1);

        var (ok, message, _) = await _pollService.CreatePollAsync(
            "Valid title", tooLong, author.Id, false, false, 1, ResultVisibility.AlwaysPublic, null,
            new List<string> { "A", "B" });

        Assert.False(ok);
        Assert.Contains("description", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await _dbContext.Polls.CountAsync());
    }

    [Fact]
    public async Task CreatePoll_RejectsTooManyOptions()
    {
        var author = await CreateUserAsync("author");
        var options = Enumerable.Range(1, PollService.MaxOptionCount + 1).Select(i => $"Option {i}").ToList();

        var (ok, message, _) = await _pollService.CreatePollAsync(
            "Valid title", null, author.Id, false, false, 1, ResultVisibility.AlwaysPublic, null, options);

        Assert.False(ok);
        Assert.Contains($"{PollService.MaxOptionCount}", message);
        Assert.Equal(0, await _dbContext.Polls.CountAsync());
    }

    [Fact]
    public async Task CreatePoll_RejectsOverlongOptionText()
    {
        var author = await CreateUserAsync("author");
        var options = new List<string> { "A", new string('o', PollService.MaxOptionTextLength + 1) };

        var (ok, message, _) = await _pollService.CreatePollAsync(
            "Valid title", null, author.Id, false, false, 1, ResultVisibility.AlwaysPublic, null, options);

        Assert.False(ok);
        Assert.Contains("option", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await _dbContext.Polls.CountAsync());
    }

    [Fact]
    public async Task CreatePoll_AcceptsValuesOnTheBoundary()
    {
        var author = await CreateUserAsync("author");
        var title = new string('t', PollService.MaxTitleLength);
        var description = new string('d', PollService.MaxDescriptionLength);
        var options = Enumerable.Range(1, PollService.MaxOptionCount)
            .Select(i => new string((char)('a' + (i % 26)), PollService.MaxOptionTextLength))
            .ToList();

        var (ok, message, pollId) = await _pollService.CreatePollAsync(
            title, description, author.Id, false, false, 1, ResultVisibility.AlwaysPublic, null, options);

        Assert.True(ok, message);
        Assert.Equal(PollService.MaxOptionCount, await _dbContext.PollOptions.CountAsync(o => o.PollId == pollId));
    }

    [Fact]
    public async Task Resubmit_RejectsContentLongerThanLimits()
    {
        var author = await CreateUserAsync("author");
        var pollId = await CreatePollAsync(author);
        await SetStatusAsync(pollId, PollStatus.ReturnedForRevision);

        var tooLong = new string('x', PollService.MaxTitleLength + 1);
        var (ok, message) = await _pollService.UpdateAndResubmitPollAsync(
            pollId, author.Id, tooLong, null, new List<string> { "A", "B" });

        Assert.False(ok);
        Assert.Contains("title", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PollStatus.ReturnedForRevision, await StatusOfAsync(pollId));
    }

    [Fact]
    public async Task UpdatePoll_RejectsTooManyOptions()
    {
        var author = await CreateUserAsync("author");
        var pollId = await CreatePollAsync(author);
        var options = Enumerable.Range(1, PollService.MaxOptionCount + 1).Select(i => $"Option {i}").ToList();

        var result = await _pollService.UpdatePollAsync(pollId, author.Id, new UpdatePollRequest
        {
            Options = options
        });

        Assert.False(result.Success);
        Assert.Equal(2, await _dbContext.PollOptions.CountAsync(o => o.PollId == pollId));
    }

    // ------------------------------------------------------------------ M4

    [Fact]
    public async Task UpdatePoll_RefusesMovingIntoAdminOnlyBoard()
    {
        var author = await CreateUserAsync("author");
        var pollId = await CreatePollAsync(author);
        var category = await CreateCategoryAsync(CategoryPostPermission.AdminOnly);

        // Sanity: creation is already refused for the same author.
        var (createOk, _, _) = await _pollService.CreatePollAsync(
            "Sneaky", null, author.Id, false, false, 1, ResultVisibility.AlwaysPublic, null,
            new List<string> { "A", "B" }, category.Id);
        Assert.False(createOk);

        var result = await _pollService.UpdatePollAsync(pollId, author.Id, new UpdatePollRequest
        {
            CategoryId = category.Id
        });

        Assert.False(result.Success);
        Assert.Contains("administrators only", result.Message, StringComparison.OrdinalIgnoreCase);

        var poll = await _dbContext.Polls.AsNoTracking().FirstAsync(p => p.Id == pollId);
        Assert.Null(poll.CategoryId);
    }

    [Fact]
    public async Task UpdatePoll_AllowsAdministratorToMoveIntoAdminOnlyBoard()
    {
        var admin = await CreateRoleUserAsync("Admin");
        var pollId = await CreatePollAsync(admin);
        var category = await CreateCategoryAsync(CategoryPostPermission.AdminOnly);

        var result = await _pollService.UpdatePollAsync(pollId, admin.Id, new UpdatePollRequest
        {
            CategoryId = category.Id
        });

        Assert.True(result.Success, result.Message);
        var poll = await _dbContext.Polls.AsNoTracking().FirstAsync(p => p.Id == pollId);
        Assert.Equal(category.Id, poll.CategoryId);
    }

    [Fact]
    public async Task Resubmit_RefusesMovingIntoModeratorOnlyBoard()
    {
        var author = await CreateUserAsync("author");
        var pollId = await CreatePollAsync(author);
        await SetStatusAsync(pollId, PollStatus.ReturnedForRevision);
        var category = await CreateCategoryAsync(CategoryPostPermission.ModeratorOnly);

        var (ok, message) = await _pollService.UpdateAndResubmitPollAsync(
            pollId, author.Id, "Revised", null, new List<string> { "A", "B" }, category.Id);

        Assert.False(ok);
        Assert.Contains("moderators", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PollStatus.ReturnedForRevision, await StatusOfAsync(pollId));
    }

    // ------------------------------------------------------------------ M7

    [Fact]
    public async Task Resubmit_AdjustsTagUsageOnlyForAddedAndRemovedTags()
    {
        var author = await CreateUserAsync("author");
        var pollId = await CreatePollAsync(author, tags: new[] { "alpha", "beta" });
        await SetStatusAsync(pollId, PollStatus.ReturnedForRevision);

        Assert.Equal(1, await TagUsageAsync("alpha"));
        Assert.Equal(1, await TagUsageAsync("beta"));

        var (ok, message) = await _pollService.UpdateAndResubmitPollAsync(
            pollId, author.Id, "Revised", null, new List<string> { "A", "B" }, null,
            new List<string> { "alpha", "gamma" });

        Assert.True(ok, message);
        Assert.Equal(1, await TagUsageAsync("alpha")); // unchanged tag is not incremented again
        Assert.Equal(0, await TagUsageAsync("beta"));  // removed tag is decremented
        Assert.Equal(1, await TagUsageAsync("gamma")); // new tag is incremented
    }

    [Fact]
    public async Task UpdatePoll_AdjustsTagUsageOnlyForAddedAndRemovedTags()
    {
        var author = await CreateUserAsync("author");
        var pollId = await CreatePollAsync(author, tags: new[] { "keep", "drop" });

        var result = await _pollService.UpdatePollAsync(pollId, author.Id, new UpdatePollRequest
        {
            Tags = new List<string> { "keep", "fresh" }
        });

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, await TagUsageAsync("keep"));
        Assert.Equal(0, await TagUsageAsync("drop"));
        Assert.Equal(1, await TagUsageAsync("fresh"));
    }

    [Fact]
    public async Task DeletePoll_DecrementsTagUsage()
    {
        var author = await CreateUserAsync("author");
        var pollId = await CreatePollAsync(author, tags: new[] { "solo" });
        Assert.Equal(1, await TagUsageAsync("solo"));

        var (ok, message) = await _pollService.DeletePollAsync(pollId, author.Id);

        Assert.True(ok, message);
        Assert.Equal(0, await TagUsageAsync("solo"));
    }

    // ------------------------------------------------------------------ P3

    [Fact]
    public async Task GetPendingReviewPolls_ReportsApprovedCountPerCreator()
    {
        var author1 = await CreateUserAsync("author1");
        var author2 = await CreateUserAsync("author2");

        await CreatePollAsync(author1, title: "A1 approved", approved: true);
        await CreatePollAsync(author1, title: "A1 pending");
        await CreatePollAsync(author2, title: "A2 pending");

        var pending = await _pollService.GetPendingReviewPollsAsync();

        var entry1 = Assert.Single(pending, p => p.CreatorId == author1.Id);
        Assert.Equal(1, entry1.CreatorApprovedPollsCount);

        var entry2 = Assert.Single(pending, p => p.CreatorId == author2.Id);
        Assert.Equal(0, entry2.CreatorApprovedPollsCount);
    }

    // ------------------------------------------------------------------ P4

    [Fact]
    public async Task GetPollFeed_ClampsPageSizeAndPage()
    {
        var author = await CreateUserAsync("author");
        await CreatePollAsync(author, "Feed poll", approved: true);

        var huge = await _pollService.GetPollFeedAsync(page: 1, pageSize: 100000000);
        Assert.Equal(PollService.MaxFeedPageSize, huge.PageSize);

        var invalid = await _pollService.GetPollFeedAsync(page: 0, pageSize: 0);
        Assert.Equal(1, invalid.Page);
        Assert.Equal(15, invalid.PageSize);
    }

    // ------------------------------------------------------------------ P2

    [Fact]
    public async Task GetPollComments_PreservesRoleBadgesAndUserLevels()
    {
        var author = await CreateUserAsync("author");
        var pollId = await CreatePollAsync(author, approved: true);

        var admin = await CreateRoleUserAsync("Admin");
        var moderator = await CreateRoleUserAsync("Moderator");

        var rootAdmin = new ApplicationUser
        {
            Id = PdnodeVote.Data.SystemConstants.RootAdminId,
            UserName = "root@test.com",
            Email = "root@test.com"
        };
        await _userManager.CreateAsync(rootAdmin, "pw123");

        // Aged account with one approved poll -> level 2 (>= 7 days and approved >= 1).
        var veteran = await CreateUserAsync("veteran", DateTime.UtcNow.AddDays(-10));
        await CreatePollAsync(veteran, title: "Veteran approved", approved: true);

        var plain = await CreateUserAsync("plain");

        foreach (var (user, text) in new[]
                 {
                     (author, "author comment"),
                     (admin, "admin comment"),
                     (moderator, "moderator comment"),
                     (rootAdmin, "root comment"),
                     (veteran, "veteran comment"),
                     (plain, "plain comment")
                 })
        {
            _dbContext.PollComments.Add(new PollComment
            {
                PollId = pollId,
                UserId = user.Id,
                Content = text,
                Status = PollStatus.Approved,
                CreatedAt = DateTime.UtcNow
            });
        }
        await _dbContext.SaveChangesAsync();

        var comments = await _commentService.GetPollCommentsAsync(pollId, null);

        Assert.Equal(6, comments.Count);
        Assert.Equal("Admin", comments.Single(c => c.UserId == admin.Id).UserRoleBadge);
        Assert.Equal("Admin", comments.Single(c => c.UserId == rootAdmin.Id).UserRoleBadge);
        Assert.Equal("Moderator", comments.Single(c => c.UserId == moderator.Id).UserRoleBadge);
        Assert.Null(comments.Single(c => c.UserId == plain.Id).UserRoleBadge);
        Assert.Equal(1, comments.Single(c => c.UserId == plain.Id).UserLevel);
        Assert.Equal(2, comments.Single(c => c.UserId == veteran.Id).UserLevel);
        Assert.Equal(1, comments.Single(c => c.UserId == admin.Id).UserLevel);
    }

    [Fact]
    public async Task GetPollComments_KeepsReplyHierarchyAndLikeFlag()
    {
        var author = await CreateUserAsync("author");
        var pollId = await CreatePollAsync(author, approved: true);
        var commenter = await CreateUserAsync("commenter");

        var root = new PollComment
        {
            PollId = pollId,
            UserId = commenter.Id,
            Content = "root",
            Status = PollStatus.Approved,
            CreatedAt = DateTime.UtcNow
        };
        _dbContext.PollComments.Add(root);
        await _dbContext.SaveChangesAsync();

        _dbContext.PollComments.Add(new PollComment
        {
            PollId = pollId,
            UserId = author.Id,
            ParentCommentId = root.Id,
            Content = "reply",
            Status = PollStatus.Approved,
            CreatedAt = DateTime.UtcNow
        });
        _dbContext.CommentLikes.Add(new CommentLike
        {
            CommentId = root.Id,
            UserId = author.Id,
            CreatedAt = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();

        var comments = await _commentService.GetPollCommentsAsync(pollId, author.Id);

        var rootDto = Assert.Single(comments);
        Assert.Equal(root.Id, rootDto.Id);
        Assert.True(rootDto.HasUpvoted);
        Assert.Single(rootDto.Replies);
        Assert.Equal("reply", rootDto.Replies[0].Content);
    }
}
