using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace PdnodeVote.Data;

public static class DataSeeder
{
    public static async Task SeedAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DataSeeder));

        // Create the directory the *connection string* points at, not one derived from
        // AppContext.BaseDirectory: the two can differ (M9), and that mismatch silently produced a
        // second, freshly seeded database whenever the process started from another working directory.
        EnsureDatabaseDirectory(configuration, logger);

        await context.Database.MigrateAsync();

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        // Seed Roles
        string[] roles = ["Admin", "SuperModerator", "Moderator", "User"];
        foreach (var role in roles)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole(role));
            }
        }

        // Promote legacy "Moderator" accounts to "SuperModerator" only when an operator explicitly asks
        // for it. This used to run on every startup, silently re-granting a role that an operator may
        // have deliberately removed — the same failure mode as S2 for the Admin role. Enable the switch,
        // start once, then turn it back off.
        if (configuration.GetValue<bool>(
                $"{SystemConstants.LegacyDataMigrationConfigKey}:{SystemConstants.PromoteModeratorsToSuperModeratorsSwitch}"))
        {
            var currentMods = await userManager.GetUsersInRoleAsync("Moderator");
            var promoted = 0;
            foreach (var mod in currentMods)
            {
                if (!await userManager.IsInRoleAsync(mod, "SuperModerator"))
                {
                    await userManager.AddToRoleAsync(mod, "SuperModerator");
                    promoted++;
                }
            }

            logger.LogInformation(
                "Legacy moderator promotion is enabled: {Promoted} of {Total} 'Moderator' account(s) were also granted 'SuperModerator'. Disable the switch now that it has run once.",
                promoted,
                currentMods.Count);
        }

        // Seed Default Categories (Announcements -> Moderator Lounge, and General Discussion)
        if (!await context.Categories.AnyAsync())
        {
            var announcement = new Category
            {
                Name = "Announcements",
                Description = "Official system updates, platform rules, and announcements (Admin only)",
                Slug = "announcements",
                Depth = 0,
                PostPermission = CategoryPostPermission.AdminOnly,
                IsSystem = true,
                CreatedAt = DateTime.UtcNow
            };
            context.Categories.Add(announcement);
            await context.SaveChangesAsync();

            var modZone = new Category
            {
                Name = "Moderator Lounge",
                Description = "Moderator discussion and platform coordination (Moderators & Admins only)",
                Slug = "moderator-zone",
                ParentId = announcement.Id,
                Depth = 1,
                PostPermission = CategoryPostPermission.ModeratorOnly,
                IsSystem = true,
                CreatedAt = DateTime.UtcNow
            };

            var general = new Category
            {
                Name = "General Discussion",
                Description = "Open community discussion, general polls, and public interest topics",
                Slug = "general",
                Depth = 0,
                PostPermission = CategoryPostPermission.Anyone,
                IsSystem = false,
                CreatedAt = DateTime.UtcNow
            };

            context.Categories.AddRange(modZone, general);
            await context.SaveChangesAsync();
        }
        else
        {
            // Rename legacy Chinese category names to English if present
            var ann = await context.Categories.FirstOrDefaultAsync(c => c.Name == "公告与通知");
            if (ann != null)
            {
                ann.Name = "Announcements";
                ann.Description = "Official system updates, platform rules, and announcements (Admin only)";
            }

            var mod = await context.Categories.FirstOrDefaultAsync(c => c.Name == "版主工作专区");
            if (mod != null)
            {
                mod.Name = "Moderator Lounge";
                mod.Description = "Moderator discussion and platform coordination (Moderators & Admins only)";
            }

            var gen = await context.Categories.FirstOrDefaultAsync(c => c.Name == "综合讨论");
            if (gen != null)
            {
                gen.Name = "General Discussion";
                gen.Description = "Open community discussion, general polls, and public interest topics";
            }

            await context.SaveChangesAsync();
        }

        // Ensure root administrator exists with immutable fixed UUID
        var user = await userManager.FindByIdAsync(SystemConstants.RootAdminId);
        if (user == null)
        {
            var legacyUser = await userManager.FindByEmailAsync(SystemConstants.DefaultAdminEmail);
            if (legacyUser != null)
            {
                // Seamlessly migrate the legacy seeded account onto the fixed RootAdminId. See
                // MigrateLegacyAdminKeyAsync for why this is transactional and verified.
                await MigrateLegacyAdminKeyAsync(context, legacyUser.Id, logger);
                user = await userManager.FindByIdAsync(SystemConstants.RootAdminId)
                       ?? throw new InvalidOperationException(
                           "The legacy administrator account could not be moved onto the fixed root id; the database was left unchanged.");
            }
            else
            {
                var environment = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();

                // The bootstrap password must never be a constant baked into the repository: it used
                // to be seeded as "admin123", giving every fresh install a publicly known admin
                // account. Supply it via configuration (e.g. BootstrapAdmin:Password, or the
                // PDNODEVOTE_ADMIN_PASSWORD environment variable / user-secrets).
                var bootstrapPassword = configuration[$"{SystemConstants.BootstrapAdminConfigKey}:Password"]
                                        ?? Environment.GetEnvironmentVariable(SystemConstants.BootstrapAdminPasswordEnvVar);

                if (string.IsNullOrWhiteSpace(bootstrapPassword))
                {
                    if (!environment.IsDevelopment())
                    {
                        throw new InvalidOperationException(
                            $"No administrator account exists and no bootstrap password was configured. " +
                            $"Set '{SystemConstants.BootstrapAdminConfigKey}:Password' (or the " +
                            $"{SystemConstants.BootstrapAdminPasswordEnvVar} environment variable) to create " +
                            $"the initial administrator, then restart.");
                    }

                    // Development convenience: generate a random password and print it once so the
                    // first login is possible without any preconfigured secret.
                    bootstrapPassword = GenerateBootstrapPassword();
                    logger.LogWarning(
                        "No '{ConfigKey}:Password' configured. Generated a one-time administrator " +
                        "password for {Email}: {Password}  -- set the configuration value to control it.",
                        SystemConstants.BootstrapAdminConfigKey,
                        SystemConstants.DefaultAdminEmail,
                        bootstrapPassword);
                }

                user = new ApplicationUser
                {
                    Id = SystemConstants.RootAdminId,
                    UserName = SystemConstants.DefaultAdminEmail,
                    Email = SystemConstants.DefaultAdminEmail,
                    EmailConfirmed = true,
                    CreatedAt = DateTime.UtcNow.AddMonths(-1)
                };

                var result = await userManager.CreateAsync(user, bootstrapPassword);
                if (!result.Succeeded)
                {
                    // Never swallow this: without the root admin the instance has no way in.
                    throw new InvalidOperationException(
                        $"Failed to create the root administrator account: " +
                        string.Join("; ", result.Errors.Select(e => e.Description)));
                }

                // Grant the role at creation time only. Re-adding it on every startup would silently
                // undo a deliberate role removal performed by an operator.
                await userManager.AddToRoleAsync(user, "Admin");
            }
        }
    }

    private static string GenerateBootstrapPassword()
    {
        // 24 hex chars + fixed punctuation/symbols satisfies the default Identity password policy.
        var random = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        return $"Pd!{random}a1";
    }

    /// <summary>
    /// Creates the directory that the configured SQLite database file lives in.
    /// </summary>
    /// <remarks>
    /// A relative <c>DataSource=Data/app.db</c> is resolved by SQLite against the process working
    /// directory, while the seeder used to create "Data" under <see cref="AppContext.BaseDirectory"/>.
    /// When the two differed (e.g. the app launched from another directory), SQLite created a brand new
    /// empty database and the seeder re-created the administrator in it. Resolving the path exactly the
    /// way SQLite will keeps directory creation and database location in sync.
    /// </remarks>
    private static void EnsureDatabaseDirectory(IConfiguration configuration, ILogger logger)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        string? dataSource;
        try
        {
            dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        }
        catch (ArgumentException ex)
        {
            // A malformed connection string will fail loudly when the context opens it; don't mask that.
            logger.LogWarning(ex, "Could not parse 'DefaultConnection'; skipping the SQLite data-directory check.");
            return;
        }

        if (string.IsNullOrWhiteSpace(dataSource) || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var fullPath = Path.IsPathRooted(dataSource)
            ? dataSource
            : Path.Combine(Directory.GetCurrentDirectory(), dataSource);

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            logger.LogInformation("Created the SQLite data directory {Directory} for '{DataSource}'.", directory, dataSource);
        }
    }

    /// <summary>
    /// User-id columns that are migrated even when SQLite does not report a foreign key for them.
    /// </summary>
    /// <remarks>
    /// Every user-id column in the current model does carry a FK, so
    /// <see cref="GetUserReferenceColumnsAsync"/> discovers them all on its own. This list is kept as a
    /// safety net for a future column that is added without a constraint — the original S11 defect was
    /// exactly such an omission (a hardcoded list of four tables).
    /// </remarks>
    private static readonly (string Table, string Column)[] KnownUserReferenceColumns =
    [
        // The principal row itself: SQLite's foreign-key metadata cannot lead us here because the table
        // references nothing, and forgetting it leaves every child pointing at a non-existent parent.
        ("AspNetUsers", "Id"),
        ("AspNetUserRoles", "UserId"),
        ("AspNetUserClaims", "UserId"),
        ("AspNetUserLogins", "UserId"),
        ("AspNetUserTokens", "UserId"),
        ("AspNetUserPasskeys", "UserId"),
        ("Polls", "CreatorId"),
        ("VoteRecords", "UserId"),
        ("PollComments", "UserId"),
        ("CommentLikes", "UserId"),
        ("Notifications", "UserId"),
        ("ContentReports", "ReporterId"),
        ("ContentReports", "ResolvedById"),
        ("CategoryModerators", "UserId"),
        ("CategorySubscriptions", "UserId"),
        ("CategoryRequests", "ApplicantId"),
        ("CategoryRequestReviews", "ReviewerId"),
    ];

    /// <summary>
    /// Moves the legacy seeded administrator account onto <see cref="SystemConstants.RootAdminId"/>.
    /// </summary>
    /// <remarks>
    /// Rewriting an identity primary key touches every table that references it; the previous
    /// implementation only updated four of them and left dangling rows in roughly a dozen others
    /// (AspNetUserLogins/Tokens/Passkeys, comments, likes, notifications, reports, board moderation),
    /// because <c>PRAGMA foreign_keys = OFF</c> also disabled the very check that would have caught it.
    ///
    /// The rewrite now runs as a single transaction over every discovered user-reference column and is
    /// rolled back if it would leave more foreign-key violations than it started with. <c>PRAGMA
    /// foreign_keys</c> is connection-scoped and is a no-op inside a transaction, so it is switched
    /// before <c>BEGIN</c> on a connection this method holds open, and restored in a <c>finally</c>
    /// block: otherwise a pooled connection would be returned with enforcement permanently disabled.
    /// </remarks>
    private static async Task MigrateLegacyAdminKeyAsync(ApplicationDbContext context, string legacyId, ILogger logger)
    {
        await context.Database.OpenConnectionAsync();
        try
        {
            var targets = await GetUserReferenceColumnsAsync(context);
            logger.LogInformation(
                "Migrating the legacy administrator {LegacyId} to {RootAdminId}; {Count} user reference column(s) will be updated: {Columns}",
                legacyId,
                SystemConstants.RootAdminId,
                targets.Count,
                string.Join(", ", targets.Select(t => $"{t.Table}.{t.Column}")));

            var violationsBefore = await GetForeignKeyViolationsAsync(context);

            await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            try
            {
                await using var transaction = await context.Database.BeginTransactionAsync();
                try
                {
                    foreach (var (table, column) in targets)
                    {
                        // Identifiers cannot be parameterized, so they are concatenated. They come from
                        // SQLite's own schema metadata (sqlite_master / PRAGMA foreign_key_list), never
                        // from user input, and are quoted. Values stay parametrized below.
                        var updateSql = "UPDATE \"" + table + "\" SET \"" + column + "\" = {0} WHERE \"" + column + "\" = {1};";
                        await context.Database.ExecuteSqlRawAsync(updateSql, SystemConstants.RootAdminId, legacyId);
                    }

                    var introduced = (await GetForeignKeyViolationsAsync(context))
                        .Where(v => !violationsBefore.Contains(v))
                        .ToList();
                    if (introduced.Count > 0)
                    {
                        // Some table still points at the old id: throwing rolls the whole update back, so
                        // the database is never left half-migrated with dangling rows.
                        throw new InvalidOperationException(
                            "Refusing to rewrite the administrator primary key: the change would leave " +
                            $"{introduced.Count} dangling foreign key reference(s), e.g. " +
                            string.Join(", ", introduced.Take(5).Select(v => $"{v.Table}#{v.RowId} -> {v.Parent}")) +
                            $". Add the missing table(s) to {nameof(KnownUserReferenceColumns)} and retry. " +
                            "No changes were applied.");
                    }

                    await transaction.CommitAsync();
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }
            finally
            {
                // Must run even on failure: this PRAGMA lives on the connection, not the transaction.
                await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Every (table, column) pair that stores a user id.
    /// </summary>
    /// <remarks>
    /// Discovered from SQLite's own foreign-key metadata (<c>PRAGMA foreign_key_list</c>) so the list
    /// cannot drift from the schema, plus <see cref="KnownUserReferenceColumns"/> for unconstrained
    /// columns.
    /// </remarks>
    private static async Task<List<(string Table, string Column)>> GetUserReferenceColumnsAsync(ApplicationDbContext context)
    {
        var results = new List<(string Table, string Column)>();
        var seen = new HashSet<(string Table, string Column)>();

        foreach (var known in KnownUserReferenceColumns)
        {
            if (seen.Add(known))
            {
                results.Add(known);
            }
        }

        var connection = context.Database.GetDbConnection();

        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA foreign_key_list(\"{table}\");";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                // Column layout: id, seq, referenced table, referencing column, referenced column, ...
                if (!string.Equals(reader.GetString(2), "AspNetUsers", StringComparison.Ordinal))
                {
                    continue;
                }

                var target = (Table: table, Column: reader.GetString(3));
                if (seen.Add(target))
                {
                    results.Add(target);
                }
            }
        }

        return results;
    }

    /// <summary>A single dangling foreign-key reference reported by <c>PRAGMA foreign_key_check</c>.</summary>
    private readonly record struct ForeignKeyViolation(string Table, long RowId, string Parent, long ForeignKeyId);

    /// <summary>
    /// Lists the foreign-key violations reported by <c>PRAGMA foreign_key_check</c>.
    /// </summary>
    /// <remarks>
    /// <c>foreign_key_check</c> inspects the data itself and works even while enforcement is disabled,
    /// which is what makes it usable as the commit gate of the rewrite above. Comparing the lists before
    /// and after isolates the violations this migration would introduce from any pre-existing damage
    /// that an old database may already carry.
    /// </remarks>
    private static async Task<List<ForeignKeyViolation>> GetForeignKeyViolationsAsync(ApplicationDbContext context)
    {
        var violations = new List<ForeignKeyViolation>();
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();

        // A command issued on a connection with an open transaction must join that transaction.
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT \"table\", rowid, \"parent\", fkid FROM pragma_foreign_key_check;";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            violations.Add(new ForeignKeyViolation(
                reader.GetString(0),
                reader.IsDBNull(1) ? -1 : reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? -1 : reader.GetInt64(3)));
        }

        return violations;
    }
}
