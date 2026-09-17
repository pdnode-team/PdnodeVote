using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PdnodeVote.Data;
using Xunit;

namespace PdnodeVote.Tests;

/// <summary>
/// Regression tests for the administrator bootstrap path (TODO-BUGS.md S1/S2).
/// The seeder used to create "admin@vote.com" with the hardcoded password "admin123" on every
/// fresh database, and to re-grant the Admin role on every startup.
/// </summary>
public class DataSeederTests
{
    private sealed class FakeEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PdnodeVote.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = environmentName;
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);
        public void Dispose() { }

        private sealed class CapturingLogger(List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => sink.Add(formatter(state, exception));
        }
    }

    private sealed class OneShotFactory(DbContextOptions<ApplicationDbContext> options)
        : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(new ApplicationDbContext(options));
    }

    private static async Task<ServiceProvider> BuildProviderAsync(
        SqliteConnection connection,
        string environmentName,
        string? bootstrapPassword,
        CapturingLoggerProvider loggerProvider,
        Dictionary<string, string?>? extraSettings = null)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Log(RelationalEventId.PendingModelChangesWarning))
            .Options;

        var settings = new Dictionary<string, string?>();
        if (bootstrapPassword is not null)
        {
            settings[$"{SystemConstants.BootstrapAdminConfigKey}:Password"] = bootstrapPassword;
        }
        if (extraSettings is not null)
        {
            foreach (var (key, value) in extraSettings)
            {
                settings[key] = value;
            }
        }

        var services = new ServiceCollection();
        services.AddSingleton<IServiceProvider>(sp => sp);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddSingleton<IHostEnvironment>(new FakeEnvironment(environmentName));
        services.AddLogging(builder => builder.AddProvider(loggerProvider));
        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new OneShotFactory(options));
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(connection)
            .ConfigureWarnings(w => w.Log(RelationalEventId.PendingModelChangesWarning)));
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>();
        // Deliberately no AddDefaultTokenProviders(): the seeder never issues tokens, and the
        // providers require an IDataProtectionProvider the test host does not need to stand up.

        // Note: no EnsureCreated/CreateDbContext here — DataSeeder.SeedAsync runs Database.Migrate()
        // itself, and pre-creating the schema would make that fail with "table already exists".
        // The options above downgrade PendingModelChangesWarning, which EF Core 9+ raises even when
        // the model and the migration snapshot are schema-identical (see Program.cs).
        return services.BuildServiceProvider();
    }

    private static SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        return connection;
    }

    [Fact]
    public async Task Seeder_Production_WithoutPassword_ThrowsInsteadOfUsingADefaultPassword()
    {
        using var connection = OpenConnection();
        var logs = new CapturingLoggerProvider();
        await using var provider = await BuildProviderAsync(connection, "Production", null, logs);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DataSeeder.SeedAsync(provider));

        Assert.Contains("bootstrap password", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Seeder_WithConfiguredPassword_UsesItAndGrantsAdminRole()
    {
        using var connection = OpenConnection();
        var logs = new CapturingLoggerProvider();
        await using var provider = await BuildProviderAsync(connection, "Development", "Sup3r!Secret9", logs);

        await DataSeeder.SeedAsync(provider);

        using var scope = provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(SystemConstants.RootAdminId);

        Assert.NotNull(user);
        Assert.True(await userManager.CheckPasswordAsync(user!, "Sup3r!Secret9"));
        Assert.True(await userManager.IsInRoleAsync(user!, "Admin"));
    }

    [Fact]
    public async Task Seeder_Development_WithoutPassword_GeneratesARandomOneAndLogsIt()
    {
        using var connection = OpenConnection();
        var logs = new CapturingLoggerProvider();
        await using var provider = await BuildProviderAsync(connection, "Development", null, logs);

        await DataSeeder.SeedAsync(provider);

        // The password must not be a constant compiled into the repository.
        var generated = logs.Messages
            .FirstOrDefault(m => m.Contains("one-time administrator password", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(generated);
        Assert.DoesNotContain("admin123", generated!, StringComparison.Ordinal);

        // ...and the seeded account must actually accept it.
        var password = generated!.Split(':').Last().Split("  --")[0].Trim();
        using var scope = provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(SystemConstants.RootAdminId);
        Assert.NotNull(user);
        Assert.True(await userManager.CheckPasswordAsync(user!, password));
    }

    [Fact]
    public async Task Seeder_DoesNotReGrantAdminRoleOnLaterStartups()
    {
        using var connection = OpenConnection();
        var logs = new CapturingLoggerProvider();
        await using var provider = await BuildProviderAsync(connection, "Development", "Sup3r!Secret9", logs);

        await DataSeeder.SeedAsync(provider);

        // An operator deliberately revokes the role...
        using (var scope = provider.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByIdAsync(SystemConstants.RootAdminId);
            await userManager.RemoveFromRoleAsync(user!, "Admin");
        }

        // ...and a later startup must not silently undo that.
        await DataSeeder.SeedAsync(provider);

        using (var scope = provider.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByIdAsync(SystemConstants.RootAdminId);
            Assert.False(await userManager.IsInRoleAsync(user!, "Admin"));
        }
    }

    // ---------------------------------------------------------------- S11

    private const string LegacyAdminId = "11111111-1111-1111-1111-111111111111";

    private static async Task ExecuteSqlAsync(SqliteConnection connection, string sql)
    {
        if (connection.State != System.Data.ConnectionState.Open) connection.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ExecuteScalarAsync(SqliteConnection connection, string sql)
    {
        if (connection.State != System.Data.ConnectionState.Open) connection.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Seeder_MovesLegacyAdminKeyWithoutDanglingReferencesOrDisabledForeignKeys()
    {
        using var connection = OpenConnection();
        var logs = new CapturingLoggerProvider();
        await using var provider = await BuildProviderAsync(connection, "Development", "Sup3r!Secret9", logs);

        await DataSeeder.SeedAsync(provider);

        // Reproduce a legacy install: the administrator carries a random id and the referencing tables
        // the old seeder forgot about are populated. The rename itself needs enforcement off (that is
        // how an identity key change works in SQLite), which is exactly the window S11 was about.
        await ExecuteSqlAsync(connection,
            "PRAGMA foreign_keys = OFF;" +
            $"UPDATE AspNetUsers SET Id = '{LegacyAdminId}' WHERE Id = '{SystemConstants.RootAdminId}';" +
            $"UPDATE AspNetUserRoles SET UserId = '{LegacyAdminId}' WHERE UserId = '{SystemConstants.RootAdminId}';" +
            "PRAGMA foreign_keys = ON;");

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var category = new Category { Name = "Legacy board", Slug = "legacy-board", Depth = 0, CreatedAt = DateTime.UtcNow };
            db.Categories.Add(category);
            var poll = new Poll { Title = "Legacy poll", CreatorId = LegacyAdminId, Status = PollStatus.Approved, CreatedAt = DateTime.UtcNow };
            db.Polls.Add(poll);
            await db.SaveChangesAsync();

            var option = new PollOption { PollId = poll.Id, Text = "A", Order = 1 };
            db.PollOptions.Add(option);
            var comment = new PollComment { PollId = poll.Id, UserId = LegacyAdminId, Content = "hello", Status = PollStatus.Approved, CreatedAt = DateTime.UtcNow };
            db.PollComments.Add(comment);
            var request = new CategoryRequest { Name = "Legacy request", Description = "", ApplicantId = LegacyAdminId, Status = CategoryRequestStatus.Pending, CreatedAt = DateTime.UtcNow };
            db.CategoryRequests.Add(request);
            await db.SaveChangesAsync();

            db.VoteRecords.Add(new VoteRecord { PollId = poll.Id, PollOptionId = option.Id, UserId = LegacyAdminId, IpAddress = "198.51.100.7", VotedAt = DateTime.UtcNow });
            db.CommentLikes.Add(new CommentLike { CommentId = comment.Id, UserId = LegacyAdminId, CreatedAt = DateTime.UtcNow });
            db.Notifications.Add(new Notification { UserId = LegacyAdminId, Type = NotificationType.SystemNotice, Title = "t", Message = "m", CreatedAt = DateTime.UtcNow });
            db.ContentReports.Add(new ContentReport { ReporterId = LegacyAdminId, ResolvedById = LegacyAdminId, Reason = "r", CreatedAt = DateTime.UtcNow });
            db.CategoryModerators.Add(new CategoryModerator { CategoryId = category.Id, UserId = LegacyAdminId, AssignedAt = DateTime.UtcNow });
            db.CategorySubscriptions.Add(new CategorySubscription { UserId = LegacyAdminId, CategoryId = category.Id, SubscribedAt = DateTime.UtcNow });
            db.CategoryRequestReviews.Add(new CategoryRequestReview { RequestId = request.Id, ReviewerId = LegacyAdminId, IsApproved = true, ReviewedAt = DateTime.UtcNow });
            db.UserLogins.Add(new IdentityUserLogin<string> { LoginProvider = "Legacy", ProviderKey = "key", ProviderDisplayName = "Legacy", UserId = LegacyAdminId });
            db.UserTokens.Add(new IdentityUserToken<string> { UserId = LegacyAdminId, LoginProvider = "Legacy", Name = "token", Value = "v" });
            db.UserClaims.Add(new IdentityUserClaim<string> { UserId = LegacyAdminId, ClaimType = "legacy", ClaimValue = "1" });
            await db.SaveChangesAsync();
        }

        await ExecuteSqlAsync(connection,
            $"INSERT INTO AspNetUserPasskeys (CredentialId, UserId, Data) VALUES (x'0102030405', '{LegacyAdminId}', '{{}}');");

        // The next startup must converge every reference onto the fixed root id in one shot.
        await DataSeeder.SeedAsync(provider);

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            const string root = SystemConstants.RootAdminId;

            Assert.NotNull(await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == root));
            Assert.Null(await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == LegacyAdminId));

            Assert.Equal(1, await db.Polls.CountAsync(p => p.CreatorId == root));
            Assert.Equal(0, await db.Polls.CountAsync(p => p.CreatorId == LegacyAdminId));
            Assert.Equal(1, await db.VoteRecords.CountAsync(v => v.UserId == root));
            Assert.Equal(1, await db.PollComments.CountAsync(c => c.UserId == root));
            Assert.Equal(1, await db.CommentLikes.CountAsync(l => l.UserId == root));
            Assert.Equal(1, await db.Notifications.CountAsync(n => n.UserId == root));
            Assert.Equal(1, await db.ContentReports.CountAsync(r => r.ReporterId == root && r.ResolvedById == root));
            Assert.Equal(1, await db.CategoryModerators.CountAsync(m => m.UserId == root));
            Assert.Equal(1, await db.CategorySubscriptions.CountAsync(s => s.UserId == root));
            Assert.Equal(1, await db.CategoryRequests.CountAsync(r => r.ApplicantId == root));
            Assert.Equal(1, await db.CategoryRequestReviews.CountAsync(r => r.ReviewerId == root));
        }

        // Identity satellite tables follow too.
        Assert.Equal(1L, await ExecuteScalarAsync(connection, $"SELECT COUNT(*) FROM AspNetUserLogins WHERE UserId = '{SystemConstants.RootAdminId}';"));
        Assert.Equal(1L, await ExecuteScalarAsync(connection, $"SELECT COUNT(*) FROM AspNetUserTokens WHERE UserId = '{SystemConstants.RootAdminId}';"));
        Assert.Equal(1L, await ExecuteScalarAsync(connection, $"SELECT COUNT(*) FROM AspNetUserClaims WHERE UserId = '{SystemConstants.RootAdminId}';"));
        Assert.Equal(1L, await ExecuteScalarAsync(connection, $"SELECT COUNT(*) FROM AspNetUserPasskeys WHERE UserId = '{SystemConstants.RootAdminId}';"));
        Assert.Equal(0L, await ExecuteScalarAsync(connection, $"SELECT COUNT(*) FROM AspNetUserRoles WHERE UserId = '{LegacyAdminId}';"));

        // No dangling references anywhere...
        Assert.Equal(0L, await ExecuteScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        // ...and enforcement is guaranteed to be back on for this (pooled) connection.
        Assert.Equal(1L, await ExecuteScalarAsync(connection, "PRAGMA foreign_keys;"));
    }

    // ---------------------------------------------------------------- L7

    private static async Task<ApplicationUser> CreateModeratorAsync(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var moderator = new ApplicationUser { UserName = "mod@test.com", Email = "mod@test.com" };
        await userManager.CreateAsync(moderator, "Sup3r!Secret9");
        await userManager.AddToRoleAsync(moderator, "Moderator");
        return moderator;
    }

    [Fact]
    public async Task Seeder_DoesNotPromoteModeratorsToSuperModeratorOnEveryStartup()
    {
        using var connection = OpenConnection();
        var logs = new CapturingLoggerProvider();
        await using var provider = await BuildProviderAsync(connection, "Development", "Sup3r!Secret9", logs);

        await DataSeeder.SeedAsync(provider);
        var moderator = await CreateModeratorAsync(provider);

        // A restart must not silently escalate the account to SuperModerator.
        await DataSeeder.SeedAsync(provider);

        using (var scope = provider.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var refreshed = await userManager.FindByIdAsync(moderator.Id);
            Assert.False(await userManager.IsInRoleAsync(refreshed!, "SuperModerator"));
        }
    }

    [Fact]
    public async Task Seeder_PromotesModeratorsOnlyWhenTheSwitchIsEnabled()
    {
        using var connection = OpenConnection();
        var logs = new CapturingLoggerProvider();
        await using var provider = await BuildProviderAsync(connection, "Development", "Sup3r!Secret9", logs,
            new Dictionary<string, string?>
            {
                [$"{SystemConstants.LegacyDataMigrationConfigKey}:{SystemConstants.PromoteModeratorsToSuperModeratorsSwitch}"] = "true"
            });

        await DataSeeder.SeedAsync(provider);
        var moderator = await CreateModeratorAsync(provider);

        await DataSeeder.SeedAsync(provider);

        using (var scope = provider.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var refreshed = await userManager.FindByIdAsync(moderator.Id);
            Assert.True(await userManager.IsInRoleAsync(refreshed!, "SuperModerator"));
        }
    }
}
