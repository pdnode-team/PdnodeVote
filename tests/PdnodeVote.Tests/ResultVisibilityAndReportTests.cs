using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PdnodeVote.Client.Models;
using PdnodeVote.Data;
using PdnodeVote.Services;
using Xunit;

namespace PdnodeVote.Tests;

/// <summary>
/// Regression tests for the result-visibility, CSV-export and report-banning fixes
/// (TODO-BUGS.md S5 / S6 / S7).
/// </summary>
public class ResultVisibilityAndReportTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _dbContext;
    private readonly TestDbContextFactory _factory;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly PollService _pollService;
    private readonly ReportService _reportService;

    private sealed class NoOpNotificationService : INotificationService
    {
        public Task<List<NotificationDto>> GetNotificationsAsync(string userId, int limit = 20) =>
            Task.FromResult(new List<NotificationDto>());
        public Task<int> GetUnreadCountAsync(string userId) => Task.FromResult(0);
        public Task<ServiceResult> MarkAsReadAsync(string userId, int? notificationId = null) =>
            Task.FromResult(ServiceResult.Ok());
        public Task CreateNotificationAsync(string userId, NotificationType type, string title,
            string message, string? targetUrl = null) => Task.CompletedTask;
    }

    public ResultVisibilityAndReportTests()
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

        var notifier = new PollEventNotifier();
        _pollService = new PollService(_factory, new TestEmailNotificationService(), _userManager, notifier);
        _reportService = new ReportService(_factory, _userManager, notifier, new NoOpNotificationService());
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private async Task<(ApplicationUser author, int pollId, int optionId)> CreateApprovedPollAsync(
        PdnodeVote.Data.ResultVisibility visibility)
    {
        var author = new ApplicationUser { UserName = $"a{Guid.NewGuid():N}@test.com" };
        author.Email = author.UserName;
        await _userManager.CreateAsync(author, "pw123");

        var (ok, msg, pollId) = await _pollService.CreatePollAsync(
            "Visibility poll", null, author.Id, false, false, 1, visibility, null,
            new List<string> { "Alpha", "Beta" });
        Assert.True(ok, msg);

        // A new author's poll is gated to PendingReview; approve it for the test.
        var poll = await _dbContext.Polls.FirstAsync(p => p.Id == pollId);
        poll.Status = PdnodeVote.Data.PollStatus.Approved;
        await _dbContext.SaveChangesAsync();

        var optionId = (await _dbContext.PollOptions.AsNoTracking().FirstAsync(o => o.PollId == pollId)).Id;
        return (author, pollId, optionId);
    }

    // ---------------------------------------------------------------- S5

    [Fact]
    public async Task AfterVoting_NonVoter_GetsNoCountsOrPercentages()
    {
        var (_, pollId, _) = await CreateApprovedPollAsync(PdnodeVote.Data.ResultVisibility.AfterVoting);

        var detail = await _pollService.GetPollDetailAsync(pollId, null, "198.51.100.1");

        Assert.NotNull(detail);
        Assert.False(detail!.ResultsVisible);
        Assert.Equal(0, detail.TotalParticipants);
        Assert.Equal(0, detail.TotalVotesCount);
        Assert.All(detail.Options, o => Assert.Equal(0, o.VoteCount));
        Assert.All(detail.Options, o => Assert.Equal(0, o.Percentage));
        // The option text must still be present so the guest can vote.
        Assert.Equal(2, detail.Options.Count);
        Assert.All(detail.Options, o => Assert.False(string.IsNullOrWhiteSpace(o.Text)));
    }

    [Fact]
    public async Task AfterVoting_Voter_SeesCounts()
    {
        var (_, pollId, optionId) = await CreateApprovedPollAsync(PdnodeVote.Data.ResultVisibility.AfterVoting);
        var voter = new ApplicationUser { UserName = $"v{Guid.NewGuid():N}@test.com" };
        voter.Email = voter.UserName;
        await _userManager.CreateAsync(voter, "pw123");

        var vote = await _pollService.CastVoteAsync(pollId, voter.Id, "198.51.100.2", new List<int> { optionId });
        Assert.True(vote.Success, vote.Message);

        var detail = await _pollService.GetPollDetailAsync(pollId, voter.Id, "198.51.100.2");

        Assert.NotNull(detail);
        Assert.True(detail!.ResultsVisible);
        Assert.Equal(1, detail.TotalParticipants);
        Assert.Equal(1, detail.Options.Single(o => o.Id == optionId).VoteCount);
    }

    [Fact]
    public async Task AlwaysPublic_NonVoter_StillSeesCounts()
    {
        var (_, pollId, optionId) = await CreateApprovedPollAsync(PdnodeVote.Data.ResultVisibility.AlwaysPublic);
        var voter = new ApplicationUser { UserName = $"v{Guid.NewGuid():N}@test.com" };
        voter.Email = voter.UserName;
        await _userManager.CreateAsync(voter, "pw123");
        await _pollService.CastVoteAsync(pollId, voter.Id, "198.51.100.3", new List<int> { optionId });

        var detail = await _pollService.GetPollDetailAsync(pollId, null, "198.51.100.9");

        Assert.NotNull(detail);
        Assert.True(detail!.ResultsVisible);
        Assert.Equal(1, detail.TotalParticipants);
        Assert.Equal(1, detail.Options.Single(o => o.Id == optionId).VoteCount);
    }

    [Fact]
    public async Task AfterVoting_AuthorStillSeesCounts()
    {
        var (author, pollId, optionId) = await CreateApprovedPollAsync(PdnodeVote.Data.ResultVisibility.AfterVoting);
        var voter = new ApplicationUser { UserName = $"v{Guid.NewGuid():N}@test.com" };
        voter.Email = voter.UserName;
        await _userManager.CreateAsync(voter, "pw123");
        await _pollService.CastVoteAsync(pollId, voter.Id, "198.51.100.4", new List<int> { optionId });

        var detail = await _pollService.GetPollDetailAsync(pollId, author.Id, "198.51.100.5");

        Assert.NotNull(detail);
        Assert.True(detail!.IsAuthor);
        Assert.True(detail.ResultsVisible);
        Assert.Equal(1, detail.Options.Single(o => o.Id == optionId).VoteCount);
    }

    // ---------------------------------------------------------------- S7

    [Fact]
    public async Task ResolveReport_ModeratorCannotBanAuthor_AdminCan()
    {
        await _roleManager.CreateAsync(new IdentityRole("Admin"));
        await _roleManager.CreateAsync(new IdentityRole("Moderator"));

        var author = new ApplicationUser { UserName = $"a{Guid.NewGuid():N}@test.com" };
        author.Email = author.UserName;
        await _userManager.CreateAsync(author, "pw123");

        var moderator = new ApplicationUser { UserName = $"m{Guid.NewGuid():N}@test.com" };
        moderator.Email = moderator.UserName;
        await _userManager.CreateAsync(moderator, "pw123");
        await _userManager.AddToRoleAsync(moderator, "Moderator");

        var admin = new ApplicationUser { UserName = $"ad{Guid.NewGuid():N}@test.com" };
        admin.Email = admin.UserName;
        await _userManager.CreateAsync(admin, "pw123");
        await _userManager.AddToRoleAsync(admin, "Admin");

        var (ok, msg, pollId) = await _pollService.CreatePollAsync(
            "Reported poll", null, author.Id, false, false, 1,
            PdnodeVote.Data.ResultVisibility.AlwaysPublic, null, new List<string> { "X", "Y" });
        Assert.True(ok, msg);

        var reporter = new ApplicationUser { UserName = $"r{Guid.NewGuid():N}@test.com" };
        reporter.Email = reporter.UserName;
        await _userManager.CreateAsync(reporter, "pw123");

        var filed = await _reportService.SubmitReportAsync(reporter.Id, new SubmitReportRequest
        {
            PollId = pollId,
            Reason = "spam"
        });
        Assert.True(filed.Success, filed.Message);

        var report = await _dbContext.ContentReports.AsNoTracking().FirstAsync(r => r.PollId == pollId);

        // A moderator must not be able to permanently ban through the report queue.
        var byModerator = await _reportService.ResolveReportAsync(moderator.Id, report.Id, new ResolveReportRequest
        {
            RemoveContent = true,
            BanAuthor = true,
            Notes = "mod attempt"
        });
        Assert.False(byModerator.Success);
        Assert.Contains("administrator", byModerator.Message, StringComparison.OrdinalIgnoreCase);

        var authorAfterMod = await _dbContext.Users.AsNoTracking().FirstAsync(u => u.Id == author.Id);
        Assert.False(authorAfterMod.IsBanned);

        // An admin can.
        var byAdmin = await _reportService.ResolveReportAsync(admin.Id, report.Id, new ResolveReportRequest
        {
            RemoveContent = true,
            BanAuthor = true,
            Notes = "admin action"
        });
        Assert.True(byAdmin.Success, byAdmin.Message);

        var authorAfterAdmin = await _dbContext.Users.AsNoTracking().FirstAsync(u => u.Id == author.Id);
        Assert.True(authorAfterAdmin.IsBanned);
        Assert.Null(authorAfterAdmin.BannedUntil); // permanent
    }

    [Fact]
    public async Task ResolveReport_RejectsAlreadyResolvedReport()
    {
        await _roleManager.CreateAsync(new IdentityRole("Admin"));

        var admin = new ApplicationUser { UserName = $"ad{Guid.NewGuid():N}@test.com" };
        admin.Email = admin.UserName;
        await _userManager.CreateAsync(admin, "pw123");
        await _userManager.AddToRoleAsync(admin, "Admin");

        var (_, _, pollId) = await CreateApprovedPollAsync(PdnodeVote.Data.ResultVisibility.AlwaysPublic);

        var reporter = new ApplicationUser { UserName = $"r{Guid.NewGuid():N}@test.com" };
        reporter.Email = reporter.UserName;
        await _userManager.CreateAsync(reporter, "pw123");
        await _reportService.SubmitReportAsync(reporter.Id, new SubmitReportRequest { PollId = pollId, Reason = "spam" });
        var report = await _dbContext.ContentReports.AsNoTracking().FirstAsync(r => r.PollId == pollId);

        var first = await _reportService.ResolveReportAsync(admin.Id, report.Id, new ResolveReportRequest { Dismiss = true });
        var second = await _reportService.ResolveReportAsync(admin.Id, report.Id, new ResolveReportRequest { Dismiss = true });

        Assert.True(first.Success, first.Message);
        Assert.False(second.Success);
    }
}
