using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace PdnodeVote.Data;

public static class DataSeeder
{
    public static async Task SeedAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // Ensure target Data directory exists for SQLite database file
        var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
        if (!Directory.Exists(dataDir))
        {
            Directory.CreateDirectory(dataDir);
        }

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

        // Migrate existing "Moderator" users to "SuperModerator"
        var currentMods = await userManager.GetUsersInRoleAsync("Moderator");
        foreach (var mod in currentMods)
        {
            if (!await userManager.IsInRoleAsync(mod, "SuperModerator"))
            {
                await userManager.AddToRoleAsync(mod, "SuperModerator");
            }
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
                // Seamlessly migrate legacy seeded account to fixed RootAdminId.
                // NOTE: this rewrites an identity primary key and therefore only repairs the tables
                // listed below; see TODO-BUGS.md (S11) before extending it.
                var oldId = legacyUser.Id;
                await context.Database.ExecuteSqlRawAsync(
                    "PRAGMA foreign_keys = OFF; " +
                    "UPDATE AspNetUsers SET Id = {0} WHERE Id = {1}; " +
                    "UPDATE AspNetUserRoles SET UserId = {0} WHERE UserId = {1}; " +
                    "UPDATE AspNetUserClaims SET UserId = {0} WHERE UserId = {1}; " +
                    "UPDATE Polls SET CreatorId = {0} WHERE CreatorId = {1}; " +
                    "UPDATE VoteRecords SET UserId = {0} WHERE UserId = {1}; " +
                    "PRAGMA foreign_keys = ON;",
                    SystemConstants.RootAdminId, oldId);
                user = await userManager.FindByIdAsync(SystemConstants.RootAdminId);
            }
            else
            {
                var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
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
                    scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
                        .CreateLogger(nameof(DataSeeder))
                        .LogWarning(
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
}
