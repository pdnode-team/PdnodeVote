using System.Net;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using PdnodeVote.Client.Models;
using PdnodeVote.Data;
using PdnodeVote.Hubs;
using PdnodeVote.RateLimiting;
using PdnodeVote.Services;
using Xunit;

namespace PdnodeVote.Tests;

/// <summary>
/// Regression tests for the first two fix batches (see TODO-BUGS.md).
/// Each test names the defect it pins down so a future refactor cannot silently reintroduce it.
/// </summary>
public class RegressionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _dbContext;
    private readonly TestDbContextFactory _factory;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly TestEmailNotificationService _emailService;
    private readonly PollService _pollService;

    public RegressionTests()
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

        _pollService = new PollService(_factory, _emailService, _userManager);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    // ---------------------------------------------------------------- helpers

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

    private sealed class FakeGroupManager : IGroupManager
    {
        public List<string> Added { get; } = new();

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Added.Add(groupName);
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeHubCallerContext(string? userIdentifier, string connectionId = "conn-1") : HubCallerContext
    {
        public override string ConnectionId { get; } = connectionId;
        public override string? UserIdentifier { get; } = userIdentifier;
        public override ClaimsPrincipal? User => UserIdentifier is null
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserIdentifier)], "test"));
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features => new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    // ------------------------------------------------- S3: spoofable client IP

    [Fact]
    public void ClientIp_IgnoresForgedXForwardedForHeader()
    {
        // Before the fix the accessor returned the raw X-Forwarded-For value, which let a guest
        // forge a new IP per request and vote unlimited times.
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        context.Request.Headers["X-Forwarded-For"] = "1.2.3.4, 5.6.7.8";

        var resolved = ClientIpAccessor.GetClientIp(context);

        Assert.Equal("203.0.113.9", resolved);
        Assert.DoesNotContain("1.2.3.4", resolved);
    }

    [Fact]
    public async Task GuestVote_SecondVoteFromSameIp_IsRejected()
    {
        // Pins the per-IP dedup itself: with the header no longer trusted, a repeated guest vote
        // from the same address must be refused.
        var creator = new ApplicationUser { UserName = "author@test.com", Email = "author@test.com" };
        await _userManager.CreateAsync(creator, "pw123");

        var (created, createMsg, pollId) = await _pollService.CreatePollAsync(
            "Dedupe poll", null, creator.Id, false, false, 1,
            PdnodeVote.Data.ResultVisibility.AlwaysPublic, null,
            new List<string> { "A", "B" });

        Assert.True(created, createMsg);

        // A brand-new author's poll is gated to PendingReview; approve it so voting is allowed.
        var poll = await _dbContext.Polls.FirstAsync(p => p.Id == pollId);
        poll.Status = PdnodeVote.Data.PollStatus.Approved;
        await _dbContext.SaveChangesAsync();

        var optionId = (await _dbContext.PollOptions.AsNoTracking().FirstAsync(o => o.PollId == pollId)).Id;

        var first = await _pollService.CastVoteAsync(pollId, null, "203.0.113.9", new List<int> { optionId });
        var second = await _pollService.CastVoteAsync(pollId, null, "203.0.113.9", new List<int> { optionId });

        Assert.True(first.Success, $"first guest vote unexpectedly rejected: {first.Message}");
        Assert.False(second.Success);
        Assert.Contains("already voted", second.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------- S4: hub notification subscription

    [Fact]
    public void PollHub_RequiresAuthorization()
    {
        // The hub was mapped without authorization, so anonymous clients could subscribe.
        var attribute = typeof(PollHub).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(attribute);
    }

    [Fact]
    public async Task JoinUserGroup_SubscribesCallerToOwnGroupOnly()
    {
        var hub = new PollHub { Context = new FakeHubCallerContext("user-self"), Groups = new FakeGroupManager() };

        await hub.JoinUserGroup();

        Assert.Equal(new[] { "user_user-self" }, ((FakeGroupManager)hub.Groups).Added);
    }

    [Fact]
    public async Task JoinUserGroup_RejectsForgedUserId()
    {
        // Previously any caller could pass a victim's id and receive their notifications.
        var groups = new FakeGroupManager();
        var hub = new PollHub { Context = new FakeHubCallerContext("user-self"), Groups = groups };

        await Assert.ThrowsAsync<HubException>(() => hub.JoinUserGroup("victim-id"));
        Assert.Empty(groups.Added);
    }

    [Fact]
    public async Task JoinUserGroup_IsNoOpWithoutAuthenticatedIdentity()
    {
        var groups = new FakeGroupManager();
        var hub = new PollHub { Context = new FakeHubCallerContext(null), Groups = groups };

        await hub.JoinUserGroup();

        Assert.Empty(groups.Added);
    }

    // --------------------------------- P1-3: notification wire event name contract

    [Fact]
    public void SignalR_NotificationEventNames_MatchBetweenServerAndClient()
    {
        // The server broadcast "NotificationReceived" while the client subscribed to
        // "UserNotificationReceived", so real-time notifications never arrived.
        var serverSource = File.ReadAllText(Path.Combine(RepoRoot(), "Hubs", "PollHub.cs"));
        var clientSource = File.ReadAllText(Path.Combine(
            RepoRoot(), "PdnodeVote.Client", "Shared", "NotificationBell.razor"));

        Assert.Contains("SendAsync(\"NotificationReceived\"", serverSource, StringComparison.Ordinal);
        Assert.Contains(".On<int, NotificationDto>(\"NotificationReceived\"", clientSource, StringComparison.Ordinal);
        Assert.DoesNotContain("UserNotificationReceived", clientSource, StringComparison.Ordinal);
    }

    // ------------------------------------ P1-2: resubmit must hit the right route

    [Fact]
    public void ClientResubmit_UsesResubmitEndpoint()
    {
        // ResubmitPollAsync used to PUT api/polls/{id}, which is the "safe edit" route that never
        // changes Status, so the poll stayed "Revision Required" while the UI reported success.
        // Scoped to the ResubmitPollAsync body: UpdatePollAsync legitimately uses api/polls/{id}.
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "PdnodeVote.Client", "Services", "PollApiClient.cs"));

        var start = source.IndexOf("public async Task<ServiceResult> ResubmitPollAsync(int pollId", StringComparison.Ordinal);
        Assert.True(start >= 0, "ResubmitPollAsync implementation not found in PollApiClient.cs");

        var nextMethod = source.IndexOf("    public async Task", start + 1, StringComparison.Ordinal);
        var body = nextMethod > start ? source[start..nextMethod] : source[start..];

        Assert.Contains("api/polls/{pollId}/resubmit", body, StringComparison.Ordinal);
    }

    // ------------------------------ P1-1: exactly one POST /Account/Logout handler

    [Fact]
    public void LogoutRoute_IsRegisteredExactlyOnce()
    {
        // The route was mapped twice; the template handler won and rejected MainLayout's form
        // (400 for a missing ReturnUrl, 500 for "~//" once one was supplied).
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "Program.cs"));
        var identity = File.ReadAllText(Path.Combine(
            RepoRoot(), "Components", "Account", "IdentityComponentsEndpointRouteBuilderExtensions.cs"));

        Assert.Contains("MapPost(\"/Account/Logout\"", program, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPost(\"/Logout\"", identity, StringComparison.Ordinal);
    }

    [Fact]
    public void LogoutForm_SubmitsReturnUrlField()
    {
        var mainLayout = File.ReadAllText(Path.Combine(
            RepoRoot(), "Components", "Layout", "MainLayout.razor"));

        Assert.Contains("name=\"ReturnUrl\"", mainLayout, StringComparison.Ordinal);
    }

    // --------------------------- C1: runtime uploads need the static files middleware

    [Fact]
    public void StaticFiles_Middleware_IsRegisteredBeforeStaticAssets()
    {
        // MapStaticAssets only serves the build-time manifest, so runtime uploads 404'd without
        // UseStaticFiles. Order matters: UseStaticFiles must come first.
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "Program.cs"));

        var staticFiles = program.IndexOf("app.UseStaticFiles()", StringComparison.Ordinal);
        var staticAssets = program.IndexOf("app.MapStaticAssets()", StringComparison.Ordinal);

        Assert.True(staticFiles >= 0, "app.UseStaticFiles() is missing from Program.cs");
        Assert.True(staticAssets >= 0, "app.MapStaticAssets() is missing from Program.cs");
        Assert.True(staticFiles < staticAssets, "UseStaticFiles must be registered before MapStaticAssets");
    }

    [Fact]
    public void ForwardedHeaders_Middleware_IsConditionalOnConfiguredTrust()
    {
        // Verified on .NET 10: clearing KnownProxies/KnownIPNetworks does NOT stop the middleware
        // from rewriting RemoteIpAddress, so it must only be mounted when trust is configured.
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "Program.cs"));

        Assert.Contains("if (trustForwardedHeaders)", program, StringComparison.Ordinal);
        Assert.DoesNotContain("\napp.UseForwardedHeaders();", program.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    // ------------------------------------------------- S8: password + lockout policy

    [Fact]
    public void SecurityPolicy_RequiresReasonablyStrongPasswords()
    {
        var options = new IdentityOptions();

        SecurityPolicy.Apply(options);

        Assert.True(options.Password.RequiredLength >= SecurityPolicy.RequiredPasswordLength);
        Assert.True(options.Password.RequireDigit);
        Assert.True(options.Password.RequireUppercase);
        Assert.True(options.Password.RequireLowercase);
    }

    [Fact]
    public void SecurityPolicy_EnablesLockout()
    {
        // Previously the only brute-force limit was the 8/minute/IP rate limiter.
        var options = new IdentityOptions();

        SecurityPolicy.Apply(options);

        Assert.True(options.Lockout.AllowedForNewUsers);
        Assert.True(options.Lockout.MaxFailedAccessAttempts is > 0 and <= 10);
        Assert.True(options.Lockout.DefaultLockoutTimeSpan > TimeSpan.Zero);
    }

    [Fact]
    public void Login_UsesLockoutOnFailure()
    {
        // lockoutOnFailure: false meant failed attempts were never counted, so the lockout policy
        // could never trigger no matter how it was configured.
        var login = File.ReadAllText(Path.Combine(
            RepoRoot(), "Components", "Account", "Pages", "Login.razor"));

        Assert.Contains("lockoutOnFailure: true", login, StringComparison.Ordinal);
        Assert.DoesNotContain("lockoutOnFailure: false", login, StringComparison.Ordinal);
    }

    // ------------------------------------------------- S9: Turnstile must fail closed

    [Fact]
    public void TurnstileVerification_FailsClosed()
    {
        // A non-success response or an unreachable siteverify used to fall through and accept the
        // guest vote, so any way of breaking the check disabled it.
        var endpoints = File.ReadAllText(Path.Combine(RepoRoot(), "Endpoints", "PollEndpoints.cs"));

        Assert.Contains("if (!verifyResponse.IsSuccessStatusCode)", endpoints, StringComparison.Ordinal);
        Assert.Contains("Verification challenge could not be validated", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("Fall back gracefully if Cloudflare service is unreachable", endpoints, StringComparison.Ordinal);
    }

    // ------------------------------------------------- S10: know what is actually enforced

    [Fact]
    public void UploadEndpoint_DoesNotFalselyClaimAntiforgeryIsDisabled()
    {
        // `.DisableAntiforgery()` was a no-op: verified on .NET 10 that app.UseAntiforgery() does
        // not guard minimal APIs at all, and there is no RequireAntiforgery() extension. The call
        // was removed so the code does not imply a protection decision that was never in effect.
        var endpoints = File.ReadAllText(Path.Combine(RepoRoot(), "Endpoints", "PollEndpoints.cs"));

        Assert.DoesNotContain(".RequireAuthorization().DisableAntiforgery()", endpoints, StringComparison.Ordinal);
        Assert.Contains("no antiforgery validation here", endpoints, StringComparison.Ordinal);
    }
}
