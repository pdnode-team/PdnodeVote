using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PdnodeVote.Data;
using PdnodeVote.Services;
using Xunit;

namespace PdnodeVote.Tests;

public class TestEmailNotificationService : IEmailNotificationService
{
    public List<string> SentEmails { get; } = new();

    public Task SendEmailAsync(string toEmail, string subject, string htmlBody)
    {
        SentEmails.Add($"{toEmail}|{subject}");
        return Task.CompletedTask;
    }

    public Task SendBulkEmailAsync(IEnumerable<string> toEmails, string subject, string htmlBody)
    {
        foreach (var email in toEmails)
        {
            SentEmails.Add($"{email}|{subject}");
        }
        return Task.CompletedTask;
    }

    public Task NotifyPollApprovedAsync(Poll poll, string authorEmail) =>
        SendEmailAsync(authorEmail, $"[Pdnode Vote] Poll Approved: {poll.Title}", "Approved");

    public Task NotifyPollReturnedForRevisionAsync(Poll poll, string authorEmail, string reason) =>
        SendEmailAsync(authorEmail, $"[Pdnode Vote] Action Required: Poll Returned for Revision - {poll.Title}", reason);

    public Task NotifyPollRemovedAsync(Poll poll, string authorEmail, string reason) =>
        SendEmailAsync(authorEmail, $"[Pdnode Vote] Poll Removed: {poll.Title}", reason);

    public Task NotifyPollArchivedAsync(Poll poll, string authorEmail, string? reason) =>
        SendEmailAsync(authorEmail, $"[Pdnode Vote] Poll Archived: {poll.Title}", reason ?? "");

    public Task NotifyPollPinnedAsync(Poll poll, string authorEmail, bool isPinned) =>
        SendEmailAsync(authorEmail, $"[Pdnode Vote] Poll {(isPinned ? "pinned" : "unpinned")}: {poll.Title}", "");

    public Task NotifyAccountBannedAsync(string email, bool isPermanent, DateTime? bannedUntil, string reason) =>
        SendEmailAsync(email, "[Pdnode Vote] Notice of Account Suspension", reason);

    public Task NotifyAccountUnbannedAsync(string email) =>
        SendEmailAsync(email, "[Pdnode Vote] Account Suspension Lifted", "Unbanned");

    public Task NotifyPasswordResetAsync(string email, string newPassword) =>
        SendEmailAsync(email, "[Pdnode Vote] Temporary Password Generated", newPassword);
}

public class TestDbContextFactory : IDbContextFactory<ApplicationDbContext>
{
    private readonly DbContextOptions<ApplicationDbContext> _options;

    public TestDbContextFactory(DbContextOptions<ApplicationDbContext> options)
    {
        _options = options;
    }

    public ApplicationDbContext CreateDbContext() => new(_options);
    public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ApplicationDbContext(_options));
}

public class ModerationWorkflowTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _dbContext;
    private readonly TestDbContextFactory _factory;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly TestEmailNotificationService _emailService;
    private readonly PollService _pollService;
    private readonly AdminService _adminService;

    public ModerationWorkflowTests()
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

        _pollService = new PollService(_factory, _emailService, _userManager);
        _adminService = new AdminService(_factory, _userManager, _roleManager, _emailService);
    }

    public void Dispose()
    {
        _connection.Dispose();
        _dbContext.Dispose();
        _userManager.Dispose();
        _roleManager.Dispose();
    }

    [Fact]
    public async Task NewUserPoll_IsGatedToPendingReview_WhenUnderThreshold()
    {
        // Arrange: User registered today with 0 approved polls (< 7 days and < 10 approved)
        var newUser = new ApplicationUser
        {
            UserName = "newuser@test.com",
            Email = "newuser@test.com",
            CreatedAt = DateTime.UtcNow
        };
        await _userManager.CreateAsync(newUser, "pw123");

        // Act
        var result = await _pollService.CreatePollAsync(
            "Gated Poll Title",
            "Description",
            newUser.Id,
            false,
            false,
            1,
            ResultVisibility.AlwaysPublic,
            null,
            new List<string> { "Option 1", "Option 2" }
        );

        // Assert
        Assert.True(result.Success);
        Assert.Contains("pending review", result.Message, StringComparison.OrdinalIgnoreCase);

        var poll = await _dbContext.Polls.FindAsync(result.PollId);
        Assert.NotNull(poll);
        Assert.Equal(PollStatus.PendingReview, poll.Status);

        // Verify public list does not show this poll
        var publicPolls = await _pollService.GetPollsAsync();
        Assert.DoesNotContain(publicPolls, p => p.Id == result.PollId);

        // Verify review queue shows this poll
        var pendingPolls = await _pollService.GetPendingReviewPollsAsync();
        Assert.Contains(pendingPolls, p => p.Id == result.PollId);
    }

    [Fact]
    public async Task AdminPoll_IsApprovedImmediately()
    {
        // Arrange: Admin user
        var admin = new ApplicationUser
        {
            Id = SystemConstants.RootAdminId,
            UserName = "admin@vote.com",
            Email = "admin@vote.com",
            CreatedAt = DateTime.UtcNow.AddMonths(-1)
        };
        await _userManager.CreateAsync(admin, "pw123");

        // Act
        var result = await _pollService.CreatePollAsync(
            "Admin Poll",
            null,
            admin.Id,
            false,
            false,
            1,
            ResultVisibility.AlwaysPublic,
            null,
            new List<string> { "Yes", "No" }
        );

        // Assert
        Assert.True(result.Success);
        var poll = await _dbContext.Polls.FindAsync(result.PollId);
        Assert.NotNull(poll);
        Assert.Equal(PollStatus.Approved, poll.Status);

        // Verify public list shows this poll
        var publicPolls = await _pollService.GetPollsAsync();
        Assert.Contains(publicPolls, p => p.Id == result.PollId);
    }

    [Fact]
    public async Task Moderator_CanApprove_ReturnForRevision_AndResubmitFlow()
    {
        if (!await _roleManager.RoleExistsAsync("Moderator"))
            await _roleManager.CreateAsync(new IdentityRole("Moderator"));

        var mod = new ApplicationUser
        {
            Id = "mod-id",
            UserName = "mod@test.com",
            Email = "mod@test.com"
        };
        await _userManager.CreateAsync(mod, "pw123");
        await _userManager.AddToRoleAsync(mod, "Moderator");

        // 1. Create a gated poll
        var author = new ApplicationUser
        {
            UserName = "author@test.com",
            Email = "author@test.com",
            CreatedAt = DateTime.UtcNow
        };
        await _userManager.CreateAsync(author, "pw123");

        var createResult = await _pollService.CreatePollAsync(
            "Needs Feedback Poll",
            "Desc",
            author.Id,
            false,
            false,
            1,
            ResultVisibility.AlwaysPublic,
            null,
            new List<string> { "Choice A", "Choice B" }
        );

        int pollId = createResult.PollId;

        // 2. Return for revision
        var returnRes = await _pollService.ReturnPollForRevisionAsync(pollId, "mod-id", "Please provide clearer options.");
        Assert.True(returnRes.Success);

        var pollAfterReturn = await _dbContext.Polls.FindAsync(pollId);
        Assert.Equal(PollStatus.ReturnedForRevision, pollAfterReturn!.Status);
        Assert.Equal("Please provide clearer options.", pollAfterReturn.ModerationReason);
        Assert.Contains(_emailService.SentEmails, e => e.Contains("author@test.com") && e.Contains("Returned for Revision"));

        // 3. Author edits and resubmits
        var resubmitRes = await _pollService.UpdateAndResubmitPollAsync(
            pollId,
            author.Id,
            "Needs Feedback Poll (Revised)",
            "Better description",
            new List<string> { "Choice A - Detailed", "Choice B - Detailed" }
        );
        Assert.True(resubmitRes.Success);

        _dbContext.Entry(pollAfterReturn).Reload();
        Assert.Equal(PollStatus.PendingReview, pollAfterReturn.Status);
        Assert.Null(pollAfterReturn.ModerationReason);

        // 4. Moderator approves
        var approveRes = await _pollService.ApprovePollAsync(pollId, "mod-id");
        Assert.True(approveRes.Success);

        _dbContext.Entry(pollAfterReturn).Reload();
        Assert.Equal(PollStatus.Approved, pollAfterReturn.Status);
        Assert.Contains(_emailService.SentEmails, e => e.Contains("author@test.com") && e.Contains("Poll Approved"));
    }

    [Fact]
    public async Task RedditStyleRemoval_HidesOptionsFromGuests_ShowsReason()
    {
        if (!await _roleManager.RoleExistsAsync("Admin"))
            await _roleManager.CreateAsync(new IdentityRole("Admin"));

        var admin = new ApplicationUser
        {
            Id = "admin-id",
            UserName = "admin@test.com",
            Email = "admin@test.com"
        };
        await _userManager.CreateAsync(admin, "pw123");
        await _userManager.AddToRoleAsync(admin, "Admin");

        var author = new ApplicationUser { UserName = "user2@test.com", Email = "user2@test.com" };
        await _userManager.CreateAsync(author, "pw123");

        var createResult = await _pollService.CreatePollAsync(
            "Removed Topic",
            "Content",
            author.Id,
            false,
            false,
            1,
            ResultVisibility.AlwaysPublic,
            null,
            new List<string> { "Opt 1", "Opt 2" }
        );

        // Remove poll
        await _pollService.RemovePollAsync(createResult.PollId, "admin-id", "Spam content violation");

        // Guest view
        var guestView = await _pollService.GetPollDetailAsync(createResult.PollId, null, "192.168.1.1");
        Assert.NotNull(guestView);
        Assert.Equal(PdnodeVote.Client.Models.PollStatus.Removed, guestView.Status);
        Assert.Equal("Spam content violation", guestView.ModerationReason);
        Assert.Empty(guestView.Options); // Reddit-style: options hidden from visitors

        // Author view
        var authorView = await _pollService.GetPollDetailAsync(createResult.PollId, author.Id, "192.168.1.1");
        Assert.NotNull(authorView);
        Assert.NotEmpty(authorView.Options); // Author can see their original poll
    }

    [Fact]
    public async Task BanWorkflow_RestrictsVotingAndPollCreation()
    {
        var user = new ApplicationUser { UserName = "badactor@test.com", Email = "badactor@test.com" };
        await _userManager.CreateAsync(user, "pw123");

        // Temporary ban
        var (count, msg) = await _adminService.BanUsersAsync(new[] { user.Id }, false, 7, "Harassment in comments");
        Assert.Equal(1, count);
        Assert.Contains(_emailService.SentEmails, e => e.Contains("badactor@test.com") && e.Contains("Notice of Account Suspension"));

        // Attempt poll creation while banned
        var pollResult = await _pollService.CreatePollAsync(
            "Banned User Poll",
            null,
            user.Id,
            false,
            false,
            1,
            ResultVisibility.AlwaysPublic,
            null,
            new List<string> { "A", "B" }
        );
        Assert.False(pollResult.Success);
        Assert.Contains("suspended", pollResult.Message, StringComparison.OrdinalIgnoreCase);

        // Create an approved poll to test voting
        var author = new ApplicationUser { UserName = "author2@test.com", Email = "author2@test.com" };
        await _userManager.CreateAsync(author, "pw123");
        var poll = new Poll
        {
            Title = "Test Poll",
            CreatorId = author.Id,
            Status = PollStatus.Approved,
            Options = new List<PollOption>
            {
                new() { Text = "A", Order = 1 },
                new() { Text = "B", Order = 2 }
            }
        };
        _dbContext.Polls.Add(poll);
        await _dbContext.SaveChangesAsync();

        // Attempt voting while banned
        var voteResult = await _pollService.CastVoteAsync(poll.Id, user.Id, "127.0.0.1", new List<int> { poll.Options[0].Id });
        Assert.False(voteResult.Success);
        Assert.Contains("suspended", voteResult.Message, StringComparison.OrdinalIgnoreCase);

        // Unban
        var (unbanCount, _) = await _adminService.UnbanUsersAsync(new[] { user.Id });
        Assert.Equal(1, unbanCount);
        Assert.Contains(_emailService.SentEmails, e => e.Contains("badactor@test.com") && e.Contains("Suspension Lifted"));
    }

    [Fact]
    public async Task ResetPassword_GeneratesCredentialsAndDispatchesEmail()
    {
        var user = new ApplicationUser { UserName = "resetme@test.com", Email = "resetme@test.com" };
        await _userManager.CreateAsync(user, "OldPassword123!");

        var (success, msg, newPw) = await _adminService.ResetUserPasswordAsync(user.Id);
        Assert.True(success);
        Assert.NotNull(newPw);
        Assert.Contains(_emailService.SentEmails, e => e.Contains("resetme@test.com") && e.Contains("Temporary Password Generated"));

        // Verify user can authenticate with the new password
        var pwCheck = await _userManager.CheckPasswordAsync(user, newPw);
        Assert.True(pwCheck);
    }

    [Fact]
    public async Task BanUsers_ProtectsSelfAndPrimaryAdmin()
    {
        var admin = new ApplicationUser { UserName = "mod@vote.com", Email = "mod@vote.com" };
        var primaryAdmin = new ApplicationUser 
        { 
            Id = SystemConstants.RootAdminId, 
            UserName = "admin@vote.com", 
            Email = "admin@vote.com" 
        };
        var regularUser = new ApplicationUser { UserName = "regular@test.com", Email = "regular@test.com" };

        await _userManager.CreateAsync(admin, "pw123");
        await _userManager.CreateAsync(primaryAdmin, "pw123");
        await _userManager.CreateAsync(regularUser, "pw123");

        Assert.True(primaryAdmin.IsRootAdmin);

        // Attempting to self-ban
        var (selfBanCount, selfBanMsg) = await _adminService.BanUsersAsync(new[] { admin.Id }, false, 7, "Testing self-ban", admin.Id);
        Assert.Equal(0, selfBanCount);
        Assert.Contains("own account", selfBanMsg, StringComparison.OrdinalIgnoreCase);

        // Attempting to ban primary admin
        var (primaryBanCount, primaryBanMsg) = await _adminService.BanUsersAsync(new[] { primaryAdmin.Id }, false, 7, "Testing primary ban", admin.Id);
        Assert.Equal(0, primaryBanCount);
        Assert.Contains("primary system administrator", primaryBanMsg, StringComparison.OrdinalIgnoreCase);

        // Even after primary admin changes their email address, RootAdminId still protects them
        primaryAdmin.Email = "newly_changed_email@company.com";
        primaryAdmin.UserName = "newly_changed_email@company.com";
        await _userManager.UpdateAsync(primaryAdmin);

        var (afterEmailChangeCount, afterEmailChangeMsg) = await _adminService.BanUsersAsync(new[] { primaryAdmin.Id }, false, 7, "Testing ban after email change", admin.Id);
        Assert.Equal(0, afterEmailChangeCount);
        Assert.Contains("primary system administrator", afterEmailChangeMsg, StringComparison.OrdinalIgnoreCase);

        // Mixed selection: self + regular user -> only regular user is banned
        var (mixedCount, mixedMsg) = await _adminService.BanUsersAsync(new[] { admin.Id, regularUser.Id }, false, 7, "Mixed ban", admin.Id);
        Assert.Equal(1, mixedCount);
        Assert.Contains("skipped", mixedMsg, StringComparison.OrdinalIgnoreCase);

        var refreshedRegular = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == regularUser.Id);
        Assert.NotNull(refreshedRegular);
        Assert.True(refreshedRegular.IsBanned);

        var refreshedAdmin = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == admin.Id);
        Assert.NotNull(refreshedAdmin);
        Assert.False(refreshedAdmin.IsBanned);

        // Cannot revoke Admin role from RootAdmin
        var (revokeSuccess, revokeMsg) = await _adminService.UpdateUserRoleAsync(primaryAdmin.Id, "Admin", false);
        Assert.False(revokeSuccess);
        Assert.Contains("Cannot remove Admin role", revokeMsg);
    }
}
