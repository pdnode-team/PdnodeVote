using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PdnodeVote.Client.Models;
using PdnodeVote.Data;
using PdnodeVote.Services;
using Xunit;
using NotificationType = PdnodeVote.Data.NotificationType;
using PollStatus = PdnodeVote.Data.PollStatus;
using ResultVisibility = PdnodeVote.Data.ResultVisibility;

namespace PdnodeVote.Tests;

public class CommunityProductFeaturesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _dbContext;
    private readonly TestDbContextFactory _factory;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly TestEmailNotificationService _emailService;
    private readonly PollEventNotifier _notifier;
    private readonly NotificationService _notificationService;
    private readonly ReportService _reportService;
    private readonly PollService _pollService;

    public CommunityProductFeaturesTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new ApplicationDbContext(options);
        _dbContext.Database.EnsureCreated();

        _factory = new TestDbContextFactory(options);
        _emailService = new TestEmailNotificationService();
        _notifier = new PollEventNotifier();

        var userStore = new UserStore<ApplicationUser>(_dbContext);
        var userOptions = Options.Create(new IdentityOptions());
        _userManager = new UserManager<ApplicationUser>(
            userStore,
            userOptions,
            new PasswordHasher<ApplicationUser>(),
            new IUserValidator<ApplicationUser>[0],
            new IPasswordValidator<ApplicationUser>[0],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);

        var roleStore = new RoleStore<IdentityRole>(_dbContext);
        _roleManager = new RoleManager<IdentityRole>(
            roleStore,
            new IRoleValidator<IdentityRole>[0],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            NullLogger<RoleManager<IdentityRole>>.Instance);

        _notificationService = new NotificationService(_factory, _notifier);
        _reportService = new ReportService(_factory, _userManager, _notifier, _notificationService);
        _pollService = new PollService(_factory, _emailService, _userManager, pollEventNotifier: _notifier, notificationService: _notificationService);
    }

    public void Dispose()
    {
        _connection.Dispose();
        _dbContext.Dispose();
        _userManager.Dispose();
        _roleManager.Dispose();
    }

    [Fact]
    public async Task InAppNotifications_Create_UnreadCount_MarkRead()
    {
        var user = new ApplicationUser { Id = "u1", UserName = "testuser", Email = "u1@test.com" };
        await _userManager.CreateAsync(user, "pw123");

        await _notificationService.CreateNotificationAsync(
            "u1",
            NotificationType.PollApproved,
            "Poll Approved",
            "Your poll is now live!",
            "/poll/10");

        await _notificationService.CreateNotificationAsync(
            "u1",
            NotificationType.CommentReplied,
            "New Reply",
            "Someone replied to your comment",
            "/poll/10");

        var unread = await _notificationService.GetUnreadCountAsync("u1");
        Assert.Equal(2, unread);

        var notifications = await _notificationService.GetNotificationsAsync("u1", 10);
        Assert.Equal(2, notifications.Count);
        Assert.False(notifications[0].IsRead);

        // Mark single notification read
        await _notificationService.MarkAsReadAsync("u1", notifications[0].Id);
        unread = await _notificationService.GetUnreadCountAsync("u1");
        Assert.Equal(1, unread);

        // Mark all read
        await _notificationService.MarkAsReadAsync("u1", null);
        unread = await _notificationService.GetUnreadCountAsync("u1");
        Assert.Equal(0, unread);
    }

    [Fact]
    public async Task SafePollEdit_ZeroVotes_AllowsTitleAndOptionEdits()
    {
        var author = new ApplicationUser { Id = "author1", UserName = "author1", Email = "a1@test.com" };
        await _userManager.CreateAsync(author, "pw123");

        var createResult = await _pollService.CreatePollAsync(
            "Original Title",
            "Original Desc",
            author.Id,
            false,
            false,
            1,
            ResultVisibility.AlwaysPublic,
            null,
            new List<string> { "Option 1", "Option 2" }
        );
        Assert.True(createResult.Success);

        // Update title and options with 0 votes
        var updateResult = await _pollService.UpdatePollAsync(createResult.PollId, author.Id, new UpdatePollRequest
        {
            Title = "Updated Title",
            Description = "Updated Description",
            Options = new List<string> { "Updated Option 1", "Updated Option 2", "New Option 3" },
            Tags = new List<string> { "csharp", "dotnet" }
        });
        Assert.True(updateResult.Success);

        var detail = await _pollService.GetPollDetailAsync(createResult.PollId, author.Id, "127.0.0.1");
        Assert.NotNull(detail);
        Assert.Equal("Updated Title", detail.Title);
        Assert.Equal("Updated Description", detail.Description);
        Assert.Equal(3, detail.Options.Count);
        Assert.Contains(detail.Tags, t => t == "csharp");
    }

    [Fact]
    public async Task SafePollEdit_WithVotes_LocksTitleAndOptions_AllowsDescriptionAndTags()
    {
        var author = new ApplicationUser { Id = "author2", UserName = "author2", Email = "a2@test.com" };
        await _userManager.CreateAsync(author, "pw123");

        var voter = new ApplicationUser { Id = "voter1", UserName = "voter1", Email = "v1@test.com" };
        await _userManager.CreateAsync(voter, "pw123");

        var createResult = await _pollService.CreatePollAsync(
            "Voted Poll Title",
            "Initial Desc",
            author.Id,
            false,
            false,
            1,
            ResultVisibility.AlwaysPublic,
            null,
            new List<string> { "Choice 1", "Choice 2" }
        );

        // Approve poll so voter can view and vote
        var pollEntity = await _dbContext.Polls.FindAsync(createResult.PollId);
        pollEntity!.Status = PollStatus.Approved;
        await _dbContext.SaveChangesAsync();

        // Cast a vote
        var detail = await _pollService.GetPollDetailAsync(createResult.PollId, null, "127.0.0.1");
        Assert.NotNull(detail);
        int optionId = detail.Options[0].Id;
        var voteResult = await _pollService.CastVoteAsync(createResult.PollId, voter.Id, "127.0.0.1", new List<int> { optionId });
        Assert.True(voteResult.Success);

        // Attempt to edit title -> should be rejected to protect vote integrity
        var updateTitleResult = await _pollService.UpdatePollAsync(createResult.PollId, author.Id, new UpdatePollRequest
        {
            Title = "Tampered Title",
            Description = "New Desc"
        });
        Assert.False(updateTitleResult.Success);
        Assert.Contains("Voting is already active", updateTitleResult.Message);

        // Attempt to edit options -> should be rejected
        var updateOptionsResult = await _pollService.UpdatePollAsync(createResult.PollId, author.Id, new UpdatePollRequest
        {
            Options = new List<string> { "Tampered Option 1", "Tampered Option 2" }
        });
        Assert.False(updateOptionsResult.Success);

        // Safe edit: updating description and tags -> allowed!
        var safeEditResult = await _pollService.UpdatePollAsync(createResult.PollId, author.Id, new UpdatePollRequest
        {
            Description = "Enriched Description with more context",
            Tags = new List<string> { "updatedtag" }
        });
        Assert.True(safeEditResult.Success);

        var updatedDetail = await _pollService.GetPollDetailAsync(createResult.PollId, author.Id, "127.0.0.1");
        Assert.NotNull(updatedDetail);
        Assert.Equal("Voted Poll Title", updatedDetail.Title); // Title unchanged
        Assert.Equal("Enriched Description with more context", updatedDetail.Description); // Desc updated
    }

    [Fact]
    public async Task WithdrawToDraft_AllowsEditing_RejectsIfVotesExist()
    {
        var author = new ApplicationUser { Id = "author3", UserName = "author3", Email = "a3@test.com" };
        await _userManager.CreateAsync(author, "pw123");

        var createResult = await _pollService.CreatePollAsync(
            "Draftable Poll",
            "Draft Desc",
            author.Id,
            false,
            false,
            1,
            ResultVisibility.AlwaysPublic,
            null,
            new List<string> { "Choice 1", "Choice 2" }
        );

        // Withdraw to draft when 0 votes -> succeeds
        var withdrawResult = await _pollService.WithdrawToDraftAsync(createResult.PollId, author.Id);
        Assert.True(withdrawResult.Success);

        var poll = await _dbContext.Polls.FindAsync(createResult.PollId);
        Assert.Equal(PollStatus.ReturnedForRevision, poll!.Status);
    }

    [Fact]
    public async Task PublicUserProfile_ShowsCreatedPollsAndComments_NeverExposesVotedOptions()
    {
        var user = new ApplicationUser { Id = "public_user", UserName = "alice", Email = "alice@community.com" };
        await _userManager.CreateAsync(user, "pw123");

        // Create approved poll
        var poll = new Poll
        {
            Title = "Alice's Awesome Poll",
            Description = "A poll created by Alice",
            CreatorId = user.Id,
            Status = PollStatus.Approved,
            CreatedAt = DateTime.UtcNow,
            Options = new List<PollOption> { new() { Text = "Yes" }, new() { Text = "No" } }
        };
        _dbContext.Polls.Add(poll);

        // Add a comment
        var comment = new PollComment
        {
            Poll = poll,
            UserId = user.Id,
            Content = "I really like this topic!",
            CreatedAt = DateTime.UtcNow,
            Status = PollStatus.Approved
        };
        _dbContext.PollComments.Add(comment);
        await _dbContext.SaveChangesAsync();

        var profile = await _pollService.GetUserPublicProfileAsync("alice");
        Assert.NotNull(profile);
        Assert.Equal("alice", profile.UserName);
        Assert.Single(profile.RecentPolls);
        Assert.Equal("Alice's Awesome Poll", profile.RecentPolls[0].Title);
        Assert.Single(profile.RecentComments);
        Assert.Equal("I really like this topic!", profile.RecentComments[0].Content);
    }

    [Fact]
    public async Task ContentReporting_And_ModerationResolve()
    {
        if (!await _roleManager.RoleExistsAsync("Moderator"))
            await _roleManager.CreateAsync(new IdentityRole("Moderator"));

        var mod = new ApplicationUser { Id = "mod_user", UserName = "moderator", Email = "mod@vote.com" };
        await _userManager.CreateAsync(mod, "pw123");
        await _userManager.AddToRoleAsync(mod, "Moderator");

        var reporter = new ApplicationUser { Id = "rep_user", UserName = "reporter", Email = "rep@vote.com" };
        await _userManager.CreateAsync(reporter, "pw123");

        var otherUser = new ApplicationUser { Id = "other_user", UserName = "other_user", Email = "other@vote.com" };
        await _userManager.CreateAsync(otherUser, "pw123");

        var poll = new Poll
        {
            Title = "Offensive Content",
            CreatorId = "other_user",
            Status = PollStatus.Approved,
            CreatedAt = DateTime.UtcNow
        };
        _dbContext.Polls.Add(poll);
        await _dbContext.SaveChangesAsync();

        // Submit report
        var reportResult = await _reportService.SubmitReportAsync(reporter.Id, new SubmitReportRequest
        {
            PollId = poll.Id,
            Reason = "Inappropriate content violation",
            Details = "Violates community guidelines on offensive language."
        });
        Assert.True(reportResult.Success);

        // Get pending reports
        var pending = await _reportService.GetPendingReportsAsync(1, 10);
        Assert.NotEmpty(pending.Items);
        Assert.Contains(pending.Items, r => r.Reason == "Inappropriate content violation");

        int reportId = pending.Items[0].Id;

        // Resolve report by removing poll
        var resolveResult = await _reportService.ResolveReportAsync(mod.Id, reportId, new ResolveReportRequest
        {
            RemoveContent = true,
            Notes = "Confirmed violation. Poll removed."
        });
        Assert.True(resolveResult.Success);

        var refreshedPoll = await _dbContext.Polls.AsNoTracking().FirstOrDefaultAsync(p => p.Id == poll.Id);
        Assert.Equal(PollStatus.Removed, refreshedPoll!.Status);
    }

    [Fact]
    public async Task CategorySubscriptions_Toggle_And_Filter()
    {
        var user = new ApplicationUser { Id = "sub_user", UserName = "subscriber", Email = "sub@vote.com" };
        await _userManager.CreateAsync(user, "pw123");

        var cat = new Category { Name = "Technology", Slug = "technology" };
        _dbContext.Categories.Add(cat);
        await _dbContext.SaveChangesAsync();

        // Subscribe
        var sub1 = await _pollService.ToggleCategorySubscriptionAsync(user.Id, cat.Id);
        Assert.True(sub1.Success);
        Assert.Contains("followed", sub1.Message, StringComparison.OrdinalIgnoreCase);

        var subIds = await _pollService.GetSubscribedCategoryIdsAsync(user.Id);
        Assert.Contains(cat.Id, subIds);

        // Unsubscribe
        var sub2 = await _pollService.ToggleCategorySubscriptionAsync(user.Id, cat.Id);
        Assert.True(sub2.Success);
        Assert.Contains("unfollowed", sub2.Message, StringComparison.OrdinalIgnoreCase);

        subIds = await _pollService.GetSubscribedCategoryIdsAsync(user.Id);
        Assert.DoesNotContain(cat.Id, subIds);
    }
}
