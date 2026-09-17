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

namespace PdnodeVote.Tests;

/// <summary>
/// Regression tests for TODO-BUGS.md D7 (ban did not invalidate the session) and
/// D1 (returning a voted poll for revision destroyed its votes).
/// </summary>
public class BanAndRevisionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _dbContext;
    private readonly TestDbContextFactory _factory;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly PollService _pollService;
    private readonly AdminService _adminService;

    public BanAndRevisionTests()
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

        var userStore = new UserStore<ApplicationUser>(_dbContext);
        _userManager = new UserManager<ApplicationUser>(
            userStore,
            Options.Create(new IdentityOptions()),
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

        _pollService = new PollService(_factory, new TestEmailNotificationService(), _userManager);
        _adminService = new AdminService(_factory, _userManager, _roleManager, new TestEmailNotificationService());
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

    // ------------------------------------------------------------------ D7



    [Fact]
    public async Task BanUsers_StillProtectsSelfAndRootAdmin()
    {
        var admin = await CreateUserAsync("admin");
        var root = new ApplicationUser { Id = SystemConstants.RootAdminId, UserName = "root@test.com" };
        root.Email = root.UserName;
        await _userManager.CreateAsync(root, "pw123");

        var (selfCount, selfMsg) = await _adminService.BanUsersAsync(new[] { admin.Id }, true, 0, "x", admin.Id);
        Assert.Equal(0, selfCount);
        Assert.Contains("own account", selfMsg, StringComparison.OrdinalIgnoreCase);

        var (rootCount, rootMsg) = await _adminService.BanUsersAsync(new[] { root.Id }, true, 0, "x", admin.Id);
        Assert.Equal(0, rootCount);
        Assert.Contains("primary system administrator", rootMsg, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ D1

    private async Task<(int pollId, int optionId)> CreateApprovedPollAsync()
    {
        var author = await CreateUserAsync("author");
        var (ok, msg, pollId) = await _pollService.CreatePollAsync(
            "Revision poll", null, author.Id, false, false, 1,
            PdnodeVote.Data.ResultVisibility.AlwaysPublic, null,
            new List<string> { "One", "Two" });
        Assert.True(ok, msg);

        var poll = await _dbContext.Polls.FirstAsync(p => p.Id == pollId);
        poll.Status = PdnodeVote.Data.PollStatus.Approved;
        await _dbContext.SaveChangesAsync();

        var optionId = (await _dbContext.PollOptions.AsNoTracking().FirstAsync(o => o.PollId == pollId)).Id;
        return (pollId, optionId);
    }

    [Fact]
    public async Task ReturnForRevision_IsRefusedWhenPollAlreadyHasVotes()
    {
        // Returning a voted poll let the author resubmit, which rewrote the options and
        // cascade-deleted every VoteRecord.
        var (pollId, optionId) = await CreateApprovedPollAsync();
        var voter = await CreateUserAsync("voter");
        var vote = await _pollService.CastVoteAsync(pollId, voter.Id, "203.0.113.20", new List<int> { optionId });
        Assert.True(vote.Success, vote.Message);

        var (ok, message) = await _pollService.ReturnPollForRevisionAsync(pollId, voter.Id, "needs work");

        // The voter is not a moderator, so use a real moderator to isolate the vote guard from the role check.
        await _roleManager.CreateAsync(new IdentityRole("Moderator"));
        var moderator = await CreateUserAsync("mod");
        await _userManager.AddToRoleAsync(moderator, "Moderator");
        (ok, message) = await _pollService.ReturnPollForRevisionAsync(pollId, moderator.Id, "needs work");

        Assert.False(ok);
        Assert.Contains("vote", message, StringComparison.OrdinalIgnoreCase);

        var poll = await _dbContext.Polls.AsNoTracking().FirstAsync(p => p.Id == pollId);
        Assert.Equal(PdnodeVote.Data.PollStatus.Approved, poll.Status);
    }

    [Fact]
    public async Task Resubmit_IsRefusedWhenPollHasVotes_AndVotesSurvive()
    {
        var (pollId, optionId) = await CreateApprovedPollAsync();
        var voter = await CreateUserAsync("voter");
        await _pollService.CastVoteAsync(pollId, voter.Id, "203.0.113.21", new List<int> { optionId });

        // Force the poll into ReturnedForRevision to reach the resubmit path directly.
        var poll = await _dbContext.Polls.FirstAsync(p => p.Id == pollId);
        poll.Status = PdnodeVote.Data.PollStatus.ReturnedForRevision;
        await _dbContext.SaveChangesAsync();

        var votesBefore = await _dbContext.VoteRecords.CountAsync(v => v.PollId == pollId);
        Assert.Equal(1, votesBefore);

        var (ok, message) = await _pollService.UpdateAndResubmitPollAsync(
            pollId, poll.CreatorId, "Renamed", null, new List<string> { "One", "Two" });

        Assert.False(ok);
        Assert.Contains("vote", message, StringComparison.OrdinalIgnoreCase);

        // The critical assertion: no vote was destroyed.
        Assert.Equal(votesBefore, await _dbContext.VoteRecords.CountAsync(v => v.PollId == pollId));
    }

    [Fact]
    public async Task Resubmit_StillWorksForPollWithoutVotes()
    {
        var (pollId, _) = await CreateApprovedPollAsync();
        var authorId = (await _dbContext.Polls.AsNoTracking().FirstAsync(p => p.Id == pollId)).CreatorId;

        var poll = await _dbContext.Polls.FirstAsync(p => p.Id == pollId);
        poll.Status = PdnodeVote.Data.PollStatus.ReturnedForRevision;
        await _dbContext.SaveChangesAsync();

        var (ok, message) = await _pollService.UpdateAndResubmitPollAsync(
            pollId, authorId, "Revised", "new description", new List<string> { "Alpha", "Beta", "Gamma" });

        Assert.True(ok, message);

        var reloaded = await _dbContext.Polls.AsNoTracking().FirstAsync(p => p.Id == pollId);
        Assert.Equal(PdnodeVote.Data.PollStatus.PendingReview, reloaded.Status);
        Assert.Equal("Revised", reloaded.Title);
        Assert.Equal(3, await _dbContext.PollOptions.CountAsync(o => o.PollId == pollId));
    }
}
