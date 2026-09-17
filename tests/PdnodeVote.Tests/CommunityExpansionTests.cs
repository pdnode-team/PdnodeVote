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

public class CommunityExpansionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _dbContext;
    private readonly TestDbContextFactory _factory;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly TestEmailNotificationService _emailService;
    private readonly PollEventNotifier _notifier;
    private readonly NotificationService _notificationService;
    private readonly PollService _pollService;
    private readonly CommentService _commentService;

    public CommunityExpansionTests()
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

        _notificationService = new NotificationService(_factory, _notifier);
        _pollService = new PollService(_factory, _emailService, _userManager, pollEventNotifier: _notifier, notificationService: _notificationService);
        _commentService = new CommentService(_factory, _userManager, _notifier, _notificationService);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void UserLevelHelper_CalculatesLevels1Through6_WithoutFantasyNames()
    {
        // Level 1: Brand new user with 0 activity
        var now = DateTime.UtcNow;
        var lvl1 = UserLevelHelper.CalculateLevel(now, 0, 0, 0);
        Assert.Equal(1, lvl1);
        Assert.Equal("Level 1", UserLevelHelper.GetLevelName(lvl1));
        Assert.Equal("Lv.1", UserLevelHelper.GetLevelShort(lvl1));

        // Level 2: >= 7 days and 1 vote
        var lvl2 = UserLevelHelper.CalculateLevel(now.AddDays(-8), 2, 0, 0);
        Assert.Equal(2, lvl2);
        Assert.Equal("Level 2", UserLevelHelper.GetLevelName(lvl2));
        Assert.Equal("Lv.2", UserLevelHelper.GetLevelShort(lvl2));

        // Level 3: >= 14 days, 20 votes, 1 approved poll
        var lvl3 = UserLevelHelper.CalculateLevel(now.AddDays(-15), 25, 1, 0);
        Assert.Equal(3, lvl3);
        Assert.Equal("Level 3", UserLevelHelper.GetLevelName(lvl3));
        Assert.Equal("Lv.3", UserLevelHelper.GetLevelShort(lvl3));

        // Level 4: >= 30 days, 60 votes, 3 approved polls
        var lvl4 = UserLevelHelper.CalculateLevel(now.AddDays(-35), 70, 4, 10);
        Assert.Equal(4, lvl4);
        Assert.Equal("Level 4", UserLevelHelper.GetLevelName(lvl4));
        Assert.Equal("Lv.4", UserLevelHelper.GetLevelShort(lvl4));

        // Level 5: >= 60 days, 150 votes, 8 approved polls, 50 votes received
        var lvl5 = UserLevelHelper.CalculateLevel(now.AddDays(-65), 160, 9, 55);
        Assert.Equal(5, lvl5);
        Assert.Equal("Level 5", UserLevelHelper.GetLevelName(lvl5));
        Assert.Equal("Lv.5", UserLevelHelper.GetLevelShort(lvl5));

        // Level 6: >= 90 days, 350 votes, 15 approved polls, 200 votes received
        var lvl6 = UserLevelHelper.CalculateLevel(now.AddDays(-100), 360, 16, 210);
        Assert.Equal(6, lvl6);
        Assert.Equal("Level 6", UserLevelHelper.GetLevelName(lvl6));
        Assert.Equal("Lv.6", UserLevelHelper.GetLevelShort(lvl6));
    }

    [Fact]
    public async Task CommentUpvoting_TogglesUpvoteAndCount()
    {
        var user = new ApplicationUser { UserName = "voter@vote.com", Email = "voter@vote.com" };
        await _userManager.CreateAsync(user, "Password123!");

        var author = new ApplicationUser { UserName = "author@vote.com", Email = "author@vote.com" };
        await _userManager.CreateAsync(author, "Password123!");

        var poll = new Poll
        {
            Title = "Test Poll",
            CreatorId = author.Id,
            Status = PollStatus.Approved
        };
        _dbContext.Polls.Add(poll);
        await _dbContext.SaveChangesAsync();

        var comment = new PollComment
        {
            PollId = poll.Id,
            UserId = author.Id,
            Content = "Interesting discussion point",
            Status = PollStatus.Approved
        };
        _dbContext.PollComments.Add(comment);
        await _dbContext.SaveChangesAsync();

        // First upvote -> success, count becomes 1
        var result1 = await _commentService.UpvoteCommentAsync(comment.Id, user.Id);
        Assert.True(result1.Success);

        await using (var ctx1 = _factory.CreateDbContext())
        {
            var updated1 = await ctx1.PollComments.FindAsync(comment.Id);
            Assert.Equal(1, updated1!.Upvotes);
        }

        // Second upvote (toggle) -> success, count becomes 0
        var result2 = await _commentService.UpvoteCommentAsync(comment.Id, user.Id);
        Assert.True(result2.Success);

        await using (var ctx2 = _factory.CreateDbContext())
        {
            var updated2 = await ctx2.PollComments.FindAsync(comment.Id);
            Assert.Equal(0, updated2!.Upvotes);
        }
    }

    [Fact]
    public async Task CommentPinning_TogglesPinStatus()
    {
        var author = new ApplicationUser { UserName = "mod@vote.com", Email = "mod@vote.com" };
        await _userManager.CreateAsync(author, "Password123!");

        var poll = new Poll
        {
            Title = "Test Poll Pin",
            CreatorId = author.Id,
            Status = PollStatus.Approved
        };
        _dbContext.Polls.Add(poll);
        await _dbContext.SaveChangesAsync();

        var comment = new PollComment
        {
            PollId = poll.Id,
            UserId = author.Id,
            Content = "Crucial announcement here",
            Status = PollStatus.Approved
        };
        _dbContext.PollComments.Add(comment);
        await _dbContext.SaveChangesAsync();

        // Pin comment
        var pinRes1 = await _commentService.TogglePinCommentAsync(comment.Id, author.Id);
        Assert.True(pinRes1.Success);

        await using (var ctx1 = _factory.CreateDbContext())
        {
            var pinned = await ctx1.PollComments.FindAsync(comment.Id);
            Assert.True(pinned!.IsPinned);
        }

        // Unpin comment
        var pinRes2 = await _commentService.TogglePinCommentAsync(comment.Id, author.Id);
        Assert.True(pinRes2.Success);

        await using (var ctx2 = _factory.CreateDbContext())
        {
            var unpinned = await ctx2.PollComments.FindAsync(comment.Id);
            Assert.False(unpinned!.IsPinned);
        }
    }

    [Fact]
    public async Task VisualPollOptions_SaveAndRetrieveImageUrls()
    {
        var user = new ApplicationUser { UserName = "creator@vote.com", Email = "creator@vote.com" };
        await _userManager.CreateAsync(user, "Password123!");

        var optionItems = new List<PollOptionInputDto>
        {
            new() { Text = "Option A", ImageUrl = "/uploads/opt-a.png" },
            new() { Text = "Option B", ImageUrl = "/uploads/opt-b.png" }
        };

        var (success, _, pollId) = await _pollService.CreatePollAsync(
            title: "Visual Poll Test",
            description: "Comparing two visuals",
            creatorId: user.Id,
            requireLogin: false,
            isMultipleChoice: false,
            maxChoices: 1,
            resultVisibility: ResultVisibility.AlwaysPublic,
            expiresAt: null,
            optionTexts: new List<string> { "Option A", "Option B" },
            categoryId: null,
            tags: new List<string> { "design", "ui" },
            optionItems: optionItems
        );

        Assert.True(success);

        var detail = await _pollService.GetPollDetailAsync(pollId, user.Id, "127.0.0.1");
        Assert.NotNull(detail);
        Assert.Equal(2, detail!.Options.Count);
        Assert.Equal("/uploads/opt-a.png", detail.Options[0].ImageUrl);
        Assert.Equal("/uploads/opt-b.png", detail.Options[1].ImageUrl);
    }
}
