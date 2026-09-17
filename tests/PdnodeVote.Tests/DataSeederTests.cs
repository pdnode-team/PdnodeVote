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
        CapturingLoggerProvider loggerProvider)
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
}
