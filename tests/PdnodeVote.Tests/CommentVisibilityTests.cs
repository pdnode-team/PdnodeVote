using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PdnodeVote.Client.Models;
using PdnodeVote.Data;
using PollStatus = PdnodeVote.Data.PollStatus;
using PdnodeVote.Services;
using Xunit;

namespace PdnodeVote.Tests;

/// <summary>
/// Regression tests for TODO-BUGS.md C7 (comments of hidden polls were readable anonymously)
/// plus contract checks for C2/C6, which are Blazor lifecycle/UI fixes (see the file-level notes).
/// </summary>
public class CommentVisibilityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _dbContext;
    private readonly TestDbContextFactory _factory;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly CommentService _commentService;

    public CommentVisibilityTests()
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
            new IUserValidator<ApplicationUser>[0], new IPasswordValidator<ApplicationUser>[0],
            new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);

        _roleManager = new RoleManager<IdentityRole>(
            new RoleStore<IdentityRole>(_dbContext),
            new IRoleValidator<IdentityRole>[0], new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), NullLogger<RoleManager<IdentityRole>>.Instance);

        var notifier = new PollEventNotifier();
        _commentService = new CommentService(_factory, _userManager, notifier, new NotificationService(_factory, notifier));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private async Task<ApplicationUser> CreateUserAsync(string prefix)
    {
        var user = new ApplicationUser { UserName = $"{prefix}{Guid.NewGuid():N}@test.com" };
        user.Email = user.UserName;
        await _userManager.CreateAsync(user, "pw123");
        return user;
    }

    private async Task<int> CreatePollWithCommentAsync(PollStatus status, string commentText)
    {
        var author = await CreateUserAsync("author");
        var commenter = await CreateUserAsync("commenter");

        var poll = new Poll
        {
            Title = "Poll with comments",
            CreatorId = author.Id,
            Status = status,
            Options =
            [
                new PollOption { Text = "A", Order = 1 },
                new PollOption { Text = "B", Order = 2 }
            ]
        };
        _dbContext.Polls.Add(poll);
        await _dbContext.SaveChangesAsync();

        _dbContext.PollComments.Add(new PollComment
        {
            PollId = poll.Id,
            UserId = commenter.Id,
            Content = commentText,
            Status = PollStatus.Approved
        });
        await _dbContext.SaveChangesAsync();

        return poll.Id;
    }

    [Fact]
    public async Task Comments_OfRemovedPoll_AreHiddenFromAnonymousCallers()
    {
        var pollId = await CreatePollWithCommentAsync(PdnodeVote.Data.PollStatus.Removed, "leaked secret text");

        var comments = await _commentService.GetPollCommentsAsync(pollId, null);

        Assert.Empty(comments);
    }

    [Fact]
    public async Task Comments_OfPendingReviewPoll_AreHiddenFromAnonymousCallers()
    {
        var pollId = await CreatePollWithCommentAsync(PdnodeVote.Data.PollStatus.PendingReview, "unpublished draft discussion");

        var comments = await _commentService.GetPollCommentsAsync(pollId, null);

        Assert.Empty(comments);
    }

    [Fact]
    public async Task Comments_OfApprovedPoll_AreVisible()
    {
        var pollId = await CreatePollWithCommentAsync(PdnodeVote.Data.PollStatus.Approved, "public comment");

        var comments = await _commentService.GetPollCommentsAsync(pollId, null);

        Assert.Single(comments);
        Assert.Equal("public comment", comments[0].Content);
    }

    [Fact]
    public async Task Comments_OfRemovedPoll_AreVisibleToModerators()
    {
        var pollId = await CreatePollWithCommentAsync(PdnodeVote.Data.PollStatus.Removed, "moderator only");

        await _roleManager.CreateAsync(new IdentityRole("Moderator"));
        var moderator = await CreateUserAsync("mod");
        await _userManager.AddToRoleAsync(moderator, "Moderator");

        var comments = await _commentService.GetPollCommentsAsync(pollId, moderator.Id);

        Assert.Single(comments);
        Assert.Equal("moderator only", comments[0].Content);
    }

    [Fact]
    public async Task Comments_OfRemovedPoll_AreVisibleToThePollAuthor()
    {
        var author = await CreateUserAsync("author");
        var commenter = await CreateUserAsync("commenter");

        var poll = new Poll
        {
            Title = "Author owned",
            CreatorId = author.Id,
            Status = PdnodeVote.Data.PollStatus.Removed,
            Options = [new PollOption { Text = "A", Order = 1 }, new PollOption { Text = "B", Order = 2 }]
        };
        _dbContext.Polls.Add(poll);
        await _dbContext.SaveChangesAsync();
        _dbContext.PollComments.Add(new PollComment
        {
            PollId = poll.Id, UserId = commenter.Id, Content = "author can see", Status = PollStatus.Approved
        });
        await _dbContext.SaveChangesAsync();

        var comments = await _commentService.GetPollCommentsAsync(poll.Id, author.Id);

        Assert.Single(comments);
        Assert.Equal("author can see", comments[0].Content);
    }

    [Fact]
    public async Task Comments_ForMissingPoll_ReturnEmpty()
    {
        var comments = await _commentService.GetPollCommentsAsync(99999, null);
        Assert.Empty(comments);
    }
}

/// <summary>
/// C2/C6 are Blazor component lifecycle/UI corrections that this suite has no renderer to exercise,
/// so they are pinned as source contracts. Each asserts the specific shape that fixes the defect, not
/// merely that some file exists.
/// </summary>
public class ClientLifecycleContractTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PdnodeVote.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ReadClient(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), "PdnodeVote.Client", .. parts]));

    [Fact]
    public void PollDetail_ReloadsWhenRouteParameterChanges()
    {
        // Previously only OnInitializedAsync loaded the poll, so /poll/A -> /poll/B kept rendering A.
        var source = ReadClient("Pages", "PollDetail.razor");

        Assert.Contains("protected override async Task OnParametersSetAsync()", source, StringComparison.Ordinal);
        Assert.Contains("_loadedPollId == Id", source, StringComparison.Ordinal);
        Assert.Contains("private void ResetPerPollState()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PollDetail_DropsStaleResponses()
    {
        // A load in flight for the previous poll must not overwrite the new one.
        var source = ReadClient("Pages", "PollDetail.razor");

        Assert.Contains("if (_loadedPollId != requestedId) return;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbedPoll_ReloadsWhenRouteParameterChanges()
    {
        var source = ReadClient("Pages", "EmbedPoll.razor");

        Assert.Contains("protected override async Task OnParametersSetAsync()", source, StringComparison.Ordinal);
        Assert.Contains("_loadedPollId == Id", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbedPoll_ClosesVotingForExpiredOrNonApprovedPolls()
    {
        // The vote button only checked HasCurrentUserVoted, so expired/archived polls still offered it.
        var source = ReadClient("Pages", "EmbedPoll.razor");

        Assert.Contains("private bool IsVotingOpen()", source, StringComparison.Ordinal);
        Assert.Contains("_poll.Status == PollStatus.Approved", source, StringComparison.Ordinal);
        Assert.Contains("!_poll.IsExpired", source, StringComparison.Ordinal);
        Assert.Contains("@if (IsVotingOpen())", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbedPoll_ShowsFinalResultsOncePollClosed()
    {
        // An expired AfterVoting poll never showed its result to a non-voter.
        var source = ReadClient("Pages", "EmbedPoll.razor");

        Assert.Contains("private bool ResultsAreVisible()", source, StringComparison.Ordinal);
        Assert.Contains("_poll.IsExpired", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PollDetail_ShowsClosedPollsAsAReadableRecord()
    {
        // Archived polls (and Removed polls viewed by author/moderator) previously rendered neither
        // results nor the vote form, contradicting the "results are locked for reference" banner.
        var source = ReadClient("Pages", "PollDetail.razor");

        Assert.Contains("_poll.Status == PollStatus.Archived", source, StringComparison.Ordinal);
        Assert.Contains("_poll.Status == PollStatus.Removed", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbedPoll_DisablesVoteButtonWhenVotingIsClosed()
    {
        var source = ReadClient("Pages", "EmbedPoll.razor");

        // The button must be driven by IsVotingOpen, not by HasCurrentUserVoted alone.
        Assert.DoesNotContain("@if (!_poll.HasCurrentUserVoted && !_votedJustNow)", source, StringComparison.Ordinal);
        Assert.Contains("if (!IsVotingOpen() || _selectedOptionIds.Count == 0 || _voting) return;", source, StringComparison.Ordinal);
    }
}
