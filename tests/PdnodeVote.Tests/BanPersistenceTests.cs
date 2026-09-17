using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PdnodeVote.Data;
using Xunit;
using Xunit.Abstractions;

namespace PdnodeVote.Tests;

/// <summary>
/// Diagnostic test retained as documentation of the D7 investigation.
///
/// History: the first attempt at D7 set IsBanned on the tracked entity, saved, and then called
/// UserManager.UpdateSecurityStampAsync as a second step. Because UpdateSecurityStampAsync re-loads
/// and re-saves the whole user row, it silently reverted the ban fields: the API reported
/// "Successfully suspended 1 user(s)" while the database still had IsBanned=0.
///
/// Read the stamp in a FRESH context. UserManager is bound to a long-lived context in this suite, so
/// reading through it returns the stale identity-mapped instance even after the row has been updated.
/// </summary>
public class BanPersistenceTests
{
    private readonly ITestOutputHelper _out;
    public BanPersistenceTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Ban_PersistsFlagsAndRotatesSecurityStamp()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Log(RelationalEventId.PendingModelChangesWarning))
            .Options;

        using (var seed = new ApplicationDbContext(options))
        {
            seed.Database.EnsureCreated();
        }

        var factory = new TestDbContextFactory(options);
        using var hostContext = new ApplicationDbContext(options);
        var userManager = new UserManager<ApplicationUser>(
            new UserStore<ApplicationUser>(hostContext),
            Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
            new PasswordHasher<ApplicationUser>(),
            new IUserValidator<ApplicationUser>[0], new IPasswordValidator<ApplicationUser>[0],
            new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UserManager<ApplicationUser>>.Instance);

        var roleManager = new RoleManager<IdentityRole>(
            new RoleStore<IdentityRole>(hostContext),
            new IRoleValidator<IdentityRole>[0], new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RoleManager<IdentityRole>>.Instance);

        var admin = new ApplicationUser { UserName = "admin@t.com", Email = "admin@t.com" };
        await userManager.CreateAsync(admin, "pw123");
        var victim = new ApplicationUser { UserName = "victim@t.com", Email = "victim@t.com" };
        await userManager.CreateAsync(victim, "pw123");

        var svc = new Services.AdminService(factory, userManager, roleManager, new TestEmailNotificationService());

        string stampBefore;
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            stampBefore = (await ctx.Users.AsNoTracking().FirstAsync(u => u.Id == victim.Id)).SecurityStamp!;
        }

        var (count, message) = await svc.BanUsersAsync(new[] { victim.Id }, true, 0, "spam", admin.Id);
        _out.WriteLine($"ban -> count={count} message={message}");

        // Fresh context on purpose (see class remarks).
        await using var verify = await factory.CreateDbContextAsync();
        var row = await verify.Users.AsNoTracking().FirstAsync(u => u.Id == victim.Id);

        _out.WriteLine($"after -> IsBanned={row.IsBanned} reason={row.BanReason} stamp={row.SecurityStamp![..8]}");

        Assert.Equal(1, count);
        Assert.True(row.IsBanned, "ban flag was not persisted");
        Assert.Equal("spam", row.BanReason);
        Assert.NotNull(row.SecurityStamp);
        Assert.NotEqual(stampBefore, row.SecurityStamp);

        // Unban must rotate the stamp again so a cookie issued during the suspension cannot be reused.
        var (unbanCount, _) = await svc.UnbanUsersAsync(new[] { victim.Id });
        Assert.Equal(1, unbanCount);

        await using var afterUnban = await factory.CreateDbContextAsync();
        var rowAfterUnban = await afterUnban.Users.AsNoTracking().FirstAsync(u => u.Id == victim.Id);
        Assert.False(rowAfterUnban.IsBanned);
        Assert.NotEqual(row.SecurityStamp, rowAfterUnban.SecurityStamp);
    }
}
