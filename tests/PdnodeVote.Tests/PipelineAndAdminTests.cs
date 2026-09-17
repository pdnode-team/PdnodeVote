using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PdnodeVote.Client.Models;
using PdnodeVote.Data;
using PdnodeVote.Services;
using Xunit;

namespace PdnodeVote.Tests;

/// <summary>
/// Regression tests for the request-pipeline fixes (S14 API 401/403, P5 circuit retention) and for
/// the admin-service fixes (M5 password reset atomicity, M10 SMTP credential logging).
///
/// These deliberately avoid WebApplicationFactory: the test project does not reference
/// Microsoft.AspNetCore.Mvc.Testing and adding a package just for this was not warranted. Instead the
/// exact delegates/wiring that Program.cs installs are exercised directly.
/// </summary>
public class ApiAuthenticationResponseTests
{
    [Fact]
    public async Task Challenge_OnApiPath_ReturnsJson401InsteadOfRedirect()
    {
        // This is the wiring Program.cs installs via options.Events = CreateCookieEvents().
        var events = ApiAuthenticationResponses.CreateCookieEvents();
        var (redirectContext, http) = CreateRedirectContext("/api/polls/2/export-csv");

        await events.OnRedirectToLogin(redirectContext);

        Assert.Equal(StatusCodes.Status401Unauthorized, http.Response.StatusCode);
        Assert.True(Microsoft.Extensions.Primitives.StringValues.IsNullOrEmpty(http.Response.Headers.Location));
        Assert.True(http.Response.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true,
            $"expected a JSON content type, got '{http.Response.ContentType}'");

        var result = await ReadJsonAsync<ServiceResult>(http);
        Assert.False(result.Success);
        Assert.Equal(ApiAuthenticationResponses.UnauthorizedMessage, result.Message);
    }

    [Fact]
    public async Task AccessDenied_OnApiPath_ReturnsJson403InsteadOfRedirect()
    {
        var events = ApiAuthenticationResponses.CreateCookieEvents();
        var (redirectContext, http) = CreateRedirectContext("/api/admin/users/role");

        await events.OnRedirectToAccessDenied(redirectContext);

        Assert.Equal(StatusCodes.Status403Forbidden, http.Response.StatusCode);
        Assert.True(Microsoft.Extensions.Primitives.StringValues.IsNullOrEmpty(http.Response.Headers.Location));

        var result = await ReadJsonAsync<ServiceResult>(http);
        Assert.False(result.Success);
        Assert.Equal(ApiAuthenticationResponses.ForbiddenMessage, result.Message);
    }

    [Fact]
    public async Task Challenge_OnAccountPath_StillRedirectsToLoginPage()
    {
        var events = ApiAuthenticationResponses.CreateCookieEvents();
        var (redirectContext, http) = CreateRedirectContext("/Account/Manage");

        await events.OnRedirectToLogin(redirectContext);

        // Pages must keep the browser redirect, otherwise an expired session shows JSON to a human.
        Assert.Equal(StatusCodes.Status302Found, http.Response.StatusCode);
        Assert.Equal(redirectContext.RedirectUri, http.Response.Headers.Location.ToString());
    }

    [Fact]
    public void IsApiRequest_MatchesOnlyTheApiSegment()
    {
        // "/apiary" must not be treated as an API path (segment boundary, not prefix).
        Assert.True(ApiAuthenticationResponses.IsApiRequest(RequestFor("/api/polls/2/export-csv")));
        Assert.True(ApiAuthenticationResponses.IsApiRequest(RequestFor("/API/polls")));
        Assert.False(ApiAuthenticationResponses.IsApiRequest(RequestFor("/apiary/polls")));
        Assert.False(ApiAuthenticationResponses.IsApiRequest(RequestFor("/Account/Login")));
    }

    private static HttpContext RequestFor(string path)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        return http;
    }

    private static (RedirectContext<CookieAuthenticationOptions> Context, DefaultHttpContext Http) CreateRedirectContext(string path)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        // WriteAsJsonAsync resolves IOptions<JsonOptions> from RequestServices; an empty container is
        // enough and yields the web defaults (camelCase) the real app also uses.
        http.RequestServices = new ServiceCollection().AddOptions().BuildServiceProvider();
        http.Response.Body = new MemoryStream();

        var options = new CookieAuthenticationOptions
        {
            LoginPath = "/Account/Login",
            AccessDeniedPath = "/Account/AccessDenied"
        };
        var scheme = new AuthenticationScheme(
            IdentityConstants.ApplicationScheme,
            IdentityConstants.ApplicationScheme,
            typeof(CookieAuthenticationHandler));

        var redirectUri = $"/Account/Login?ReturnUrl={Uri.EscapeDataString(path)}";
        var context = new RedirectContext<CookieAuthenticationOptions>(
            http, scheme, options, new AuthenticationProperties(), redirectUri);
        return (context, http);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpContext http) where T : class
    {
        http.Response.Body.Position = 0;
        using var reader = new StreamReader(http.Response.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        var value = JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Assert.IsType<T>(value);
    }
}

/// <summary>
/// P5: DisconnectedCircuitMaxRetained = 0 + RetentionPeriod = Zero makes .NET 10 build the circuit
/// cache with MemoryCacheOptions.SizeLimit = 0, so a disconnected circuit is evicted immediately and
/// a SignalR reconnect can never resume it.
/// </summary>
public class CircuitRetentionTests
{
    [Fact]
    public void Configure_KeepsDisconnectedCircuitsRetained()
    {
        var options = new CircuitOptions();
        CircuitRetention.Configure(options);

        Assert.Equal(100, options.DisconnectedCircuitMaxRetained);
        Assert.Equal(TimeSpan.FromMinutes(3), options.DisconnectedCircuitRetentionPeriod);
        Assert.True(options.DisconnectedCircuitMaxRetained > 0);
        Assert.True(options.DisconnectedCircuitRetentionPeriod > TimeSpan.Zero);
    }
}

/// <summary>
/// M10: when SMTP is not configured the email body must never reach the log - password-reset mail
/// carries a plaintext temporary password and Identity mail carries single-use tokens.
/// </summary>
public class EmailNotificationLoggingTests
{
    [Fact]
    public async Task SendEmail_WhenSmtpNotConfigured_LogsMetadataButNotTheBody()
    {
        const string secret = "Pd#DEADBEEF1234!9";
        var logger = new CapturingListLogger<EmailNotificationService>();
        var service = new EmailNotificationService(new ConfigurationBuilder().Build(), logger);

        await service.NotifyPasswordResetAsync("victim@test.com", secret);

        var log = string.Join("\n", logger.Messages);
        Assert.DoesNotContain(secret, log);
        Assert.Contains("victim@test.com", log);
        Assert.Contains("Temporary Password Generated", log);
        Assert.Contains("SMTP is not configured", log);
    }

    private sealed class CapturingListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}

public class AdminPasswordResetTests
{
    /// <summary>
    /// M5 regression: the old implementation ran RemovePasswordAsync (persisted) and then
    /// AddPasswordAsync; when the second call failed the account was left with NO password and the
    /// previous hash was already destroyed. The reset-token flow validates first and writes once, so
    /// a rejected reset must leave the previous password fully intact.
    /// </summary>
    [Fact]
    public async Task ResetPassword_WhenNewPasswordFailsPolicy_KeepsPreviousPassword()
    {
        // A policy the generated temporary password cannot satisfy, so the reset must be rejected.
        await using var harness = new AdminServiceHarness(
            configureIdentity: options => options.Password.RequiredLength = 64,
            // PasswordValidator reads manager.Options.Password, i.e. the strict options above.
            passwordValidators: new IPasswordValidator<ApplicationUser>[] { new PasswordValidator<ApplicationUser>() });

        var user = new ApplicationUser { UserName = "keep@test.com", Email = "keep@test.com" };
        user.PasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(user, "OldPassword123!");
        Assert.True((await harness.Users.CreateAsync(user)).Succeeded);

        var service = harness.CreateAdminService();
        var (success, message, newPassword) = await service.ResetUserPasswordAsync(user.Id);

        Assert.False(success);
        Assert.Null(newPassword);
        Assert.False(string.IsNullOrWhiteSpace(message));

        Assert.True(await harness.Users.HasPasswordAsync(user), "the account must never be left without a password");
        Assert.True(await harness.Users.CheckPasswordAsync(user, "OldPassword123!"));
    }

    [Fact]
    public async Task ResetPassword_Success_ReplacesTheOldPassword()
    {
        await using var harness = new AdminServiceHarness();
        var user = new ApplicationUser { UserName = "swap@test.com", Email = "swap@test.com" };
        await harness.Users.CreateAsync(user, "OldPassword123!");

        var service = harness.CreateAdminService();
        var (success, _, newPassword) = await service.ResetUserPasswordAsync(user.Id);

        Assert.True(success);
        Assert.NotNull(newPassword);
        Assert.True(await harness.Users.CheckPasswordAsync(user, newPassword!));
        Assert.False(await harness.Users.CheckPasswordAsync(user, "OldPassword123!"));
    }

    /// <summary>
    /// M10: with no SmtpSettings:Host the service must not claim the credentials were emailed.
    /// </summary>
    [Fact]
    public async Task ResetPassword_WhenSmtpNotConfigured_DoesNotClaimEmailWasSent()
    {
        await using var harness = new AdminServiceHarness();
        var user = new ApplicationUser { UserName = "nomail@test.com", Email = "nomail@test.com" };
        await harness.Users.CreateAsync(user, "OldPassword123!");

        var service = harness.CreateAdminService(new ConfigurationBuilder().Build());
        var (success, message, newPassword) = await service.ResetUserPasswordAsync(user.Id);

        Assert.True(success);
        Assert.NotNull(newPassword);
        Assert.Contains("not configured", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("emailed", message, StringComparison.OrdinalIgnoreCase);
        // The plaintext password must never travel back over HTTP inside the message.
        Assert.DoesNotContain(newPassword!, message);
        Assert.True(await harness.Users.CheckPasswordAsync(user, newPassword!));
    }

    /// <summary>
    /// M10 counterpart: when SMTP *is* configured the previous wording is still accurate.
    /// </summary>
    [Fact]
    public async Task ResetPassword_WhenSmtpConfigured_ReportsEmailDelivery()
    {
        await using var harness = new AdminServiceHarness();
        var user = new ApplicationUser { UserName = "mail@test.com", Email = "mail@test.com" };
        await harness.Users.CreateAsync(user, "OldPassword123!");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SmtpSettings:Host"] = "smtp.test.local" })
            .Build();

        var service = harness.CreateAdminService(configuration);
        var (success, message, newPassword) = await service.ResetUserPasswordAsync(user.Id);

        Assert.True(success);
        Assert.Contains("emailed", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(harness.Email.SentEmails, e => e.Contains("mail@test.com"));
    }
}

/// <summary>
/// P3 regression: roles used to be fetched with one UserManager.GetRolesAsync per user (N+1). The
/// batched join must return exactly the same role sets, including the root-admin promotion and the
/// roleFilter behaviour.
/// </summary>
public class AdminUserRolesTests
{
    [Fact]
    public async Task GetUsersAsync_ReturnsBatchedRolesAndHonoursRoleFilter()
    {
        await using var harness = new AdminServiceHarness();
        Assert.True((await harness.Roles.CreateAsync(new IdentityRole("Admin"))).Succeeded);
        Assert.True((await harness.Roles.CreateAsync(new IdentityRole("Moderator"))).Succeeded);

        var alice = new ApplicationUser { UserName = "alice@test.com", Email = "alice@test.com" };
        var bob = new ApplicationUser { UserName = "bob@test.com", Email = "bob@test.com" };
        Assert.True((await harness.Users.CreateAsync(alice, "Password123!")).Succeeded);
        Assert.True((await harness.Users.CreateAsync(bob, "Password123!")).Succeeded);
        Assert.True((await harness.Users.AddToRoleAsync(alice, "Moderator")).Succeeded);
        Assert.True((await harness.Users.AddToRoleAsync(bob, "Moderator")).Succeeded);

        var service = harness.CreateAdminService();

        var all = await service.GetUsersAsync();
        Assert.Equal(new[] { "Moderator" }, all.Single(u => u.Id == alice.Id).Roles.OrderBy(r => r, StringComparer.Ordinal));
        Assert.Equal(new[] { "Moderator" }, all.Single(u => u.Id == bob.Id).Roles.OrderBy(r => r, StringComparer.Ordinal));

        var moderators = await service.GetUsersAsync(roleFilter: "Moderator");
        Assert.Contains(moderators, u => u.Id == alice.Id);
        Assert.Contains(moderators, u => u.Id == bob.Id);

        var admins = await service.GetUsersAsync(roleFilter: "Admin");
        Assert.DoesNotContain(admins, u => u.Id == alice.Id);
        Assert.DoesNotContain(admins, u => u.Id == bob.Id);
    }

    [Fact]
    public async Task GetUsersPagedAsync_ReturnsBatchedRoles()
    {
        await using var harness = new AdminServiceHarness();
        Assert.True((await harness.Roles.CreateAsync(new IdentityRole("Moderator"))).Succeeded);

        var user = new ApplicationUser { UserName = "paged@test.com", Email = "paged@test.com" };
        Assert.True((await harness.Users.CreateAsync(user, "Password123!")).Succeeded);
        Assert.True((await harness.Users.AddToRoleAsync(user, "Moderator")).Succeeded);

        var service = harness.CreateAdminService();
        var page = await service.GetUsersPagedAsync(1, 15);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(new[] { "Moderator" }, page.Items.Single().Roles.OrderBy(r => r, StringComparer.Ordinal));
    }
}

/// <summary>
/// Shared SQLite-backed harness for AdminService. The UserManager is created with a real service
/// provider so Identity's default token providers (required by the reset-token flow) resolve.
/// </summary>
internal sealed class AdminServiceHarness : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    public ApplicationDbContext Context { get; }
    public TestDbContextFactory Factory { get; }
    public UserManager<ApplicationUser> Users { get; }
    public RoleManager<IdentityRole> Roles { get; }
    public TestEmailNotificationService Email { get; } = new();

    public AdminServiceHarness(
        Action<IdentityOptions>? configureIdentity = null,
        IPasswordValidator<ApplicationUser>[]? passwordValidators = null)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Log(RelationalEventId.PendingModelChangesWarning))
            .Options;

        Context = new ApplicationDbContext(options);
        Context.Database.EnsureCreated();

        Factory = new TestDbContextFactory(options);
        Users = IdentityTestServices.CreateUserManager(Context, configureIdentity, passwordValidators);
        Roles = new RoleManager<IdentityRole>(
            new RoleStore<IdentityRole>(Context),
            new IRoleValidator<IdentityRole>[0],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            NullLogger<RoleManager<IdentityRole>>.Instance);
    }

    public AdminService CreateAdminService(IConfiguration? configuration = null) =>
        new(Factory, Users, Roles, Email, configuration);

    public async ValueTask DisposeAsync()
    {
        Users.Dispose();
        Roles.Dispose();
        await Context.DisposeAsync();
        _connection.Dispose();
    }
}

/// <summary>
/// Identity plumbing shared with ModerationWorkflowTests: its UserManager is constructed by hand, and
/// the reset-token flow resolves the token provider from UserManager.Services AND from
/// IdentityOptions.Tokens.ProviderMap - so both must come from a container that ran
/// AddDefaultTokenProviders().
/// </summary>
public static class IdentityTestServices
{
    private static readonly ServiceProvider TokenProviderContainer = BuildTokenProviderContainer();

    public static IServiceProvider TokenProviderServices => TokenProviderContainer;

    public static IOptions<IdentityOptions> TokenProviderIdentityOptions =>
        TokenProviderContainer.GetRequiredService<IOptions<IdentityOptions>>();

    public static UserManager<ApplicationUser> CreateUserManager(
        ApplicationDbContext context,
        Action<IdentityOptions>? configureIdentity = null,
        IPasswordValidator<ApplicationUser>[]? passwordValidators = null)
    {
        var provider = configureIdentity is null ? TokenProviderContainer : BuildTokenProviderContainer(configureIdentity);

        return new UserManager<ApplicationUser>(
            new UserStore<ApplicationUser>(context),
            provider.GetRequiredService<IOptions<IdentityOptions>>(),
            new PasswordHasher<ApplicationUser>(),
            new IUserValidator<ApplicationUser>[0],
            passwordValidators ?? new IPasswordValidator<ApplicationUser>[0],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            provider,
            NullLogger<UserManager<ApplicationUser>>.Instance);
    }

    private static ServiceProvider BuildTokenProviderContainer(Action<IdentityOptions>? configureIdentity = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddIdentityCore<ApplicationUser>().AddDefaultTokenProviders();
        if (configureIdentity is not null)
        {
            services.Configure(configureIdentity);
        }
        return services.BuildServiceProvider();
    }
}
