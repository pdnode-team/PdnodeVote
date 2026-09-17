using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PdnodeVote.Client.Models;
using PdnodeVote.Data;
using PdnodeVote.Endpoints;
using PdnodeVote.Services;
using Xunit;

namespace PdnodeVote.Tests;

/// <summary>
/// Regression tests for the hardening batch: report-target validation (M18), the in-process upload
/// limit (M20), the QR-code origin (M11) and the in-process admin role gate (M19).
/// </summary>
public class ServiceHardeningTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _dbContext;
    private readonly TestDbContextFactory _factory;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ReportService _reportService;
    private readonly PollService _pollService;

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

    private sealed class FixedAuthenticationStateProvider(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(user));
    }

    private sealed class FakeWebHostEnvironment(string webRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PdnodeVote.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = webRoot;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = webRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>A stream that cannot report its length, like the browser-supplied upload streams.</summary>
    private sealed class NonSeekableStream(byte[] payload) : Stream
    {
        private readonly MemoryStream _inner = new(payload);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public ServiceHardeningTests()
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

        var notifier = new PollEventNotifier();
        _pollService = new PollService(_factory, new TestEmailNotificationService(), _userManager, notifier);
        _reportService = new ReportService(_factory, _userManager, notifier, new NoOpNotificationService());
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    // ------------------------------------------------------------------ M18

    [Fact]
    public async Task SubmitReport_RejectsTargetsThatDoNotExist()
    {
        var reporter = new ApplicationUser { UserName = $"r{Guid.NewGuid():N}@test.com" };
        reporter.Email = reporter.UserName;
        await _userManager.CreateAsync(reporter, "pw123");

        var missingPoll = await _reportService.SubmitReportAsync(reporter.Id, new SubmitReportRequest
        {
            PollId = 987654,
            Reason = "spam"
        });
        Assert.False(missingPoll.Success, "A report against a non-existent poll must be rejected.");

        var missingComment = await _reportService.SubmitReportAsync(reporter.Id, new SubmitReportRequest
        {
            CommentId = 987654,
            Reason = "spam"
        });
        Assert.False(missingComment.Success, "A report against a non-existent comment must be rejected.");

        Assert.Equal(0, await _dbContext.ContentReports.CountAsync());
    }

    [Fact]
    public async Task SubmitReport_StillAcceptsARealTarget()
    {
        var author = new ApplicationUser { UserName = $"a{Guid.NewGuid():N}@test.com" };
        author.Email = author.UserName;
        await _userManager.CreateAsync(author, "pw123");

        var (ok, msg, pollId) = await _pollService.CreatePollAsync(
            "Reportable", null, author.Id, false, false, 1,
            PdnodeVote.Data.ResultVisibility.AlwaysPublic, null, new List<string> { "A", "B" });
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
        Assert.Equal(1, await _dbContext.ContentReports.CountAsync());
    }

    // ------------------------------------------------------------------ M20

    private static ServerPollApiClient BuildPollApiClient(string userId, string webRoot) =>
        new(
            null!, null!, null!, null!, null!,
            new HttpContextAccessor(),
            new FixedAuthenticationStateProvider(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"))),
            null!,
            new FakeWebHostEnvironment(webRoot));

    [Fact]
    public async Task UploadImage_RejectsFilesOverFiveMegabytes()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), $"pdnodevote-upload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(webRoot);
        try
        {
            var client = BuildPollApiClient("user-1", webRoot);
            var oversized = new byte[5 * 1024 * 1024 + 1];

            var fromSeekable = await client.UploadImageAsync(new MemoryStream(oversized), "big.png");
            Assert.Null(fromSeekable);

            // Non-seekable streams cannot be measured up front; the chunked copy must stop at the ceiling.
            var fromStreaming = await client.UploadImageAsync(new NonSeekableStream(oversized), "big.png");
            Assert.Null(fromStreaming);

            Assert.False(Directory.Exists(Path.Combine(webRoot, "uploads"))
                         && Directory.EnumerateFiles(Path.Combine(webRoot, "uploads")).Any(),
                "No partial upload may be left on disk.");
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [Fact]
    public async Task UploadImage_StillAcceptsASmallAllowedImage()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), $"pdnodevote-upload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(webRoot);
        try
        {
            var client = BuildPollApiClient("user-1", webRoot);

            var url = await client.UploadImageAsync(new MemoryStream(new byte[64]), "ok.png");

            Assert.NotNull(url);
            Assert.StartsWith("/uploads/", url);
            Assert.Single(Directory.EnumerateFiles(Path.Combine(webRoot, "uploads")));
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    [Fact]
    public async Task UploadImage_RejectsDisallowedExtensions()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), $"pdnodevote-upload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(webRoot);
        try
        {
            var client = BuildPollApiClient("user-1", webRoot);
            Assert.Null(await client.UploadImageAsync(new MemoryStream(new byte[16]), "payload.exe"));
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    // ------------------------------------------------------------------ M11

    [Fact]
    public void QrCodeUrl_UsesConfiguredPublicBaseUrlInsteadOfTheHostHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("evil.example.com");

        var configured = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["PublicBaseUrl"] = "https://vote.example.com/" }).Build();

        Assert.Equal("https://vote.example.com/poll/7", PollEndpoints.BuildPublicPollUrl(configured, context, 7));

        // Without configuration the request origin is still the documented fallback.
        var unconfigured = new ConfigurationBuilder().Build();
        Assert.Equal("http://evil.example.com/poll/7", PollEndpoints.BuildPublicPollUrl(unconfigured, context, 7));

        // A non-http(s) value must not be reflected either.
        var bogus = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["PublicBaseUrl"] = "not a url" }).Build();
        Assert.Equal("http://evil.example.com/poll/7", PollEndpoints.BuildPublicPollUrl(bogus, context, 7));
    }

    // ------------------------------------------------------------------ M19

    [Fact]
    public async Task ServerAdminClient_DeniesCallersWithoutTheRequiredRole()
    {
        var plainUser = new FixedAuthenticationStateProvider(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "u1")], "test")));

        var client = new ServerAdminApiClient(
            null!, null!, null!, null!, null!, plainUser, NullLogger<ServerAdminApiClient>.Instance);

        // Admin-only data must not be served...
        Assert.Equal(0, (await client.GetStatsAsync()).TotalUsers);
        Assert.False((await client.ResetPasswordAsync("someone")).Success);

        // ...and a moderation queue must not read as "nothing to moderate" silently: it returns empty
        // but logs a warning (the services behind it are null, so reaching them would throw).
        Assert.Empty(await client.GetPendingCommentsAsync());
        Assert.Empty(await client.GetUsersAsync());
    }

    // ------------------------------------------------------------------ L3

    [Fact]
    public async Task BoardApproval_ResolvesSlugCollisionsDeterministically()
    {
        // The root account is an administrator by id, so no role setup is needed.
        var admin = new ApplicationUser
        {
            Id = PdnodeVote.Data.SystemConstants.RootAdminId,
            UserName = "root@test.com",
            Email = "root@test.com"
        };
        await _userManager.CreateAsync(admin, "pw123");

        var service = new CategoryService(_factory, _userManager);

        // Both names slugify to "dupe"; only the punctuation differs, so the duplicate-name guard lets
        // both requests through and the unique Slug index would otherwise reject the second board.
        var first = await service.SubmitCategoryRequestAsync(admin.Id, new SubmitCategoryRequest { Name = "Dupe!" });
        Assert.True(first.Success, first.Message);
        var second = await service.SubmitCategoryRequestAsync(admin.Id, new SubmitCategoryRequest { Name = "Dupe?" });
        Assert.True(second.Success, second.Message);

        var requestIds = await _dbContext.CategoryRequests.AsNoTracking().OrderBy(r => r.Id).Select(r => r.Id).ToListAsync();
        Assert.Equal(2, requestIds.Count);

        foreach (var requestId in requestIds)
        {
            var reviewed = await service.ReviewCategoryRequestAsync(admin.Id, requestId, new ReviewCategoryRequest { Approve = true });
            Assert.True(reviewed.Success, reviewed.Message);
        }

        var slugs = await _dbContext.Categories.AsNoTracking().Select(c => c.Slug).ToListAsync();
        Assert.Contains("dupe", slugs);
        Assert.Contains("dupe-2", slugs);
    }

    // ------------------------------------------------------------------ L6

    [Fact]
    public async Task MarkingNotificationsRead_DoesNotBroadcastAPlaceholderNotification()
    {
        var notifier = new PollEventNotifier();
        var broadcasts = 0;
        notifier.UserNotificationReceived += (_, _, _) => broadcasts++;

        var service = new NotificationService(_factory, notifier);
        var user = new ApplicationUser { UserName = $"n{Guid.NewGuid():N}@test.com" };
        user.Email = user.UserName;
        await _userManager.CreateAsync(user, "pw123");

        await service.CreateNotificationAsync(user.Id, NotificationType.SystemNotice, "title", "message");
        Assert.Equal(1, broadcasts);

        await service.MarkAsReadAsync(user.Id, null);

        // Reading is not "a notification arrived"; broadcasting anything here made the bell insert an
        // empty (Id = 0) row into its list.
        Assert.Equal(1, broadcasts);
        Assert.Equal(0, await service.GetUnreadCountAsync(user.Id));
    }
}
