using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PdnodeVote.Data;

namespace PdnodeVote.Services;

public class AdminService : IAdminService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly IEmailNotificationService _emailNotificationService;
    private readonly IConfiguration? _configuration;

    public AdminService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IEmailNotificationService emailNotificationService,
        IConfiguration? configuration = null)
    {
        _dbContextFactory = dbContextFactory;
        _userManager = userManager;
        _roleManager = roleManager;
        _emailNotificationService = emailNotificationService;
        // Optional so the existing test harness (and any manual construction) keeps compiling; the DI
        // container always supplies the app configuration.
        _configuration = configuration;
    }

    public async Task<AdminDashboardStatsDto> GetStatsAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var totalUsers = await context.Users.CountAsync();
        var totalPolls = await context.Polls.CountAsync();
        var pendingReviewCount = await context.Polls.CountAsync(p => p.Status == PollStatus.PendingReview);
        var bannedUsersCount = await context.Users.CountAsync(u => u.IsBanned && (u.BannedUntil == null || u.BannedUntil > DateTime.UtcNow));

        return new AdminDashboardStatsDto
        {
            TotalUsers = totalUsers,
            TotalPolls = totalPolls,
            PendingReviewCount = pendingReviewCount,
            BannedUsersCount = bannedUsersCount
        };
    }

    public async Task<List<AdminUserDto>> GetUsersAsync(string? search = null, string? roleFilter = null, bool? bannedOnly = null)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var query = context.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(u => (u.UserName != null && u.UserName.ToLower().Contains(s)) ||
                                     (u.Email != null && u.Email.ToLower().Contains(s)));
        }

        if (bannedOnly == true)
        {
            var now = DateTime.UtcNow;
            query = query.Where(u => u.IsBanned && (u.BannedUntil == null || u.BannedUntil > now));
        }

        var users = await query.OrderByDescending(u => u.CreatedAt).ToListAsync();

        // Get poll stats per user
        var pollStats = await context.Polls
            .GroupBy(p => p.CreatorId)
            .Select(g => new
            {
                CreatorId = g.Key,
                TotalCount = g.Count(),
                ApprovedCount = g.Count(p => p.Status == PollStatus.Approved)
            })
            .ToDictionaryAsync(x => x.CreatorId, x => (x.TotalCount, x.ApprovedCount));

        var result = new List<AdminUserDto>();

        // One joined query for all role assignments instead of a UserManager.GetRolesAsync round-trip
        // per user (the previous loop issued an N+1 query storm against AspNetUserRoles/AspNetRoles).
        var rolesByUser = await GetRolesByUserAsync(context, users.Select(u => u.Id).ToList());

        foreach (var user in users)
        {
            var roles = rolesByUser.TryGetValue(user.Id, out var assignedRoles) ? assignedRoles : new List<string>();
            if (user.IsRootAdmin && !roles.Contains("Admin"))
            {
                roles.Add("Admin");
            }

            if (!string.IsNullOrWhiteSpace(roleFilter) && !roles.Contains(roleFilter))
            {
                continue;
            }

            pollStats.TryGetValue(user.Id, out var stats);

            result.Add(new AdminUserDto
            {
                Id = user.Id,
                UserName = user.UserName ?? user.Email ?? "User",
                Email = user.Email ?? "",
                CreatedAt = user.CreatedAt,
                Roles = roles,
                IsBanned = user.IsBanned,
                BannedUntil = user.BannedUntil,
                BanReason = user.BanReason,
                ApprovedPollsCount = stats.ApprovedCount,
                TotalPollsCount = stats.TotalCount
            });
        }

        return result;
    }

    public async Task<PdnodeVote.Client.Models.PagedResult<AdminUserDto>> GetUsersPagedAsync(int page = 1, int pageSize = 15, string? search = null, string? roleFilter = null, bool? bannedOnly = null)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 15;

        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var query = context.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(u => (u.UserName != null && u.UserName.ToLower().Contains(s)) ||
                                     (u.Email != null && u.Email.ToLower().Contains(s)));
        }

        if (bannedOnly == true)
        {
            var now = DateTime.UtcNow;
            query = query.Where(u => u.IsBanned && (u.BannedUntil == null || u.BannedUntil > now));
        }

        // If roleFilter is specified, we filter user ids by user-role table
        if (!string.IsNullOrWhiteSpace(roleFilter))
        {
            var role = await _roleManager.FindByNameAsync(roleFilter);
            if (role != null)
            {
                var userIdsInRole = await context.UserRoles.Where(ur => ur.RoleId == role.Id).Select(ur => ur.UserId).ToListAsync();
                if (roleFilter == "Admin")
                {
                    query = query.Where(u => userIdsInRole.Contains(u.Id) || u.IsRootAdmin);
                }
                else
                {
                    query = query.Where(u => userIdsInRole.Contains(u.Id));
                }
            }
        }

        var totalCount = await query.CountAsync();

        var users = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var userIds = users.Select(u => u.Id).ToList();

        var pollStats = await context.Polls
            .Where(p => userIds.Contains(p.CreatorId))
            .GroupBy(p => p.CreatorId)
            .Select(g => new
            {
                CreatorId = g.Key,
                TotalCount = g.Count(),
                ApprovedCount = g.Count(p => p.Status == PollStatus.Approved)
            })
            .ToDictionaryAsync(x => x.CreatorId, x => (x.TotalCount, x.ApprovedCount));

        var dtos = new List<AdminUserDto>();

        // Same batched role load as GetUsersAsync (one join, not one query per user).
        var rolesByUser = await GetRolesByUserAsync(context, userIds);

        foreach (var user in users)
        {
            var roles = rolesByUser.TryGetValue(user.Id, out var assignedRoles) ? assignedRoles : new List<string>();
            if (user.IsRootAdmin && !roles.Contains("Admin"))
            {
                roles.Add("Admin");
            }

            pollStats.TryGetValue(user.Id, out var stats);

            dtos.Add(new AdminUserDto
            {
                Id = user.Id,
                UserName = user.UserName ?? user.Email ?? "User",
                Email = user.Email ?? "",
                CreatedAt = user.CreatedAt,
                Roles = roles,
                IsBanned = user.IsBanned,
                BannedUntil = user.BannedUntil,
                BanReason = user.BanReason,
                ApprovedPollsCount = stats.ApprovedCount,
                TotalPollsCount = stats.TotalCount
            });
        }

        return new PdnodeVote.Client.Models.PagedResult<AdminUserDto>
        {
            Items = dtos,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// Loads the role names of every requested user in a single join over AspNetUserRoles/AspNetRoles.
    /// The per-user UserManager.GetRolesAsync loop it replaces was the N+1 hot spot in P3.
    /// </summary>
    private static async Task<Dictionary<string, List<string>>> GetRolesByUserAsync(
        ApplicationDbContext context, IReadOnlyCollection<string> userIds)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<string, List<string>>();
        }

        var rows = await (from userRole in context.UserRoles
                          join role in context.Roles on userRole.RoleId equals role.Id
                          where userIds.Contains(userRole.UserId) && role.Name != null
                          select new { userRole.UserId, RoleName = role.Name! })
                         .ToListAsync();

        // Ordered for a stable payload regardless of the database's row order.
        return rows.GroupBy(r => r.UserId)
                   .ToDictionary(
                       g => g.Key,
                       g => g.Select(r => r.RoleName).OrderBy(name => name, StringComparer.Ordinal).ToList());
    }

    public async Task<(int SuccessCount, string Message)> BanUsersAsync(IEnumerable<string> userIds, bool isPermanent, int durationDays, string reason, string? currentAdminUserId = null)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return (0, "A reason must be provided for suspending users.");
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var idList = userIds.Distinct().ToList();
        var users = await context.Users.Where(u => idList.Contains(u.Id)).ToListAsync();

        int count = 0;
        bool skippedSelf = false;
        bool skippedPrimaryAdmin = false;
        var bannedUntil = isPermanent ? (DateTime?)null : DateTime.UtcNow.AddDays(durationDays > 0 ? durationDays : 7);

        foreach (var user in users)
        {
            if (user.Id == currentAdminUserId)
            {
                skippedSelf = true;
                continue;
            }

            if (user.IsRootAdmin)
            {
                skippedPrimaryAdmin = true;
                continue;
            }

            user.IsBanned = true;
            user.BannedUntil = bannedUntil;
            user.BanReason = reason.Trim();
            user.SecurityStamp = Guid.NewGuid().ToString();
            count++;

            if (!string.IsNullOrEmpty(user.Email))
            {
                await _emailNotificationService.NotifyAccountBannedAsync(user.Email, isPermanent, bannedUntil, reason.Trim());
            }
        }

        // Persist the ban AND the rotated security stamp in a single save.
        //
        // Rotating the stamp is what invalidates an already-issued auth cookie / Blazor circuit so the
        // ban takes effect without waiting for cookie expiry (writing only IsBanned left the session
        // fully usable). It is done here, on the tracked entity, rather than via
        // UserManager.UpdateSecurityStampAsync: that call re-loads and re-saves the whole user row and
        // would revert the ban fields, and it also Attaches the instance, which throws when the caller's
        // context already tracks it.
        // The exposed API takes only a userId, so there is no in-memory UserManager instance to keep in
        // sync.
        await context.SaveChangesAsync();

        if (count == 0)
        {
            if (skippedSelf)
            {
                return (0, "Cannot suspend your own account.");
            }
            if (skippedPrimaryAdmin)
            {
                return (0, "Cannot suspend the primary system administrator account.");
            }
            return (0, "No eligible users found to suspend.");
        }

        var extra = "";
        if (skippedSelf || skippedPrimaryAdmin)
        {
            extra = " (Protected administrator accounts were skipped)";
        }

        return (count, $"Successfully suspended {count} user(s).{extra}");
    }

    public async Task<(int SuccessCount, string Message)> UnbanUsersAsync(IEnumerable<string> userIds)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var idList = userIds.Distinct().ToList();
        var users = await context.Users.Where(u => idList.Contains(u.Id)).ToListAsync();

        int count = 0;
        foreach (var user in users)
        {
            if (!user.IsBanned) continue;

            user.IsBanned = false;
            user.BannedUntil = null;
            user.BanReason = null;
            // Same single-save stamp rotation as BanUsersAsync (see the comment there).
            user.SecurityStamp = Guid.NewGuid().ToString();
            count++;

            if (!string.IsNullOrEmpty(user.Email))
            {
                await _emailNotificationService.NotifyAccountUnbannedAsync(user.Email);
            }
        }

        await context.SaveChangesAsync();

        return (count, $"Successfully restored {count} user(s).");
    }

    public async Task<(bool Success, string Message, string? NewPassword)> ResetUserPasswordAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return (false, "User not found.", null);
        }

        // Generate a cryptographically secure random password
        var randomBytes = RandomNumberGenerator.GetBytes(6);
        var baseStr = Convert.ToHexString(randomBytes);
        var newPassword = $"Pd#{baseStr}!9";

        // Replace the password through Identity's reset-token flow instead of
        // RemovePasswordAsync + AddPasswordAsync. That pair was not atomic: the removal was persisted
        // first, so when AddPasswordAsync failed the account was left with NO password and the old
        // hash was already gone. ResetPasswordAsync validates the password policy and writes the new
        // hash in a single SaveChangesAsync (it also rotates the security stamp), so the row always
        // keeps exactly one usable password - either the new one or the previous one.
        var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
        var result = await _userManager.ResetPasswordAsync(user, resetToken, newPassword);

        if (!result.Succeeded)
        {
            // Nothing was persisted: the account still authenticates with its previous password.
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            return (false, $"Password reset failed: {errors}", null);
        }

        // The new password is deliberately kept out of the returned Message: that message is
        // serialized back to the caller over HTTP, and the tuple's NewPassword is only routed to the
        // email notification below.
        if (string.IsNullOrEmpty(user.Email))
        {
            return (true, "A new password was generated, but this account has no email address on file. Pass it to the user through another (secure) channel.", newPassword);
        }

        await _emailNotificationService.NotifyPasswordResetAsync(user.Email, newPassword);

        if (!IsSmtpConfigured())
        {
            // Reporting "emailed" here would be a lie: EmailNotificationService silently drops the
            // message when SmtpSettings:Host is empty (M10).
            return (true, $"A new password was generated, but email delivery is not configured, so it was NOT sent to {user.Email}. Pass it to the user through another (secure) channel.", newPassword);
        }

        return (true, $"Password reset successfully! Temporary credentials emailed to {user.Email}.", newPassword);
    }

    private bool IsSmtpConfigured() =>
        !string.IsNullOrWhiteSpace(_configuration?["SmtpSettings:Host"]);

    public async Task<(int SentCount, string Message)> SendBulkEmailToUsersAsync(IEnumerable<string> userIds, string subject, string message)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return (0, "Subject cannot be empty.");
        }
        if (string.IsNullOrWhiteSpace(message))
        {
            return (0, "Message content cannot be empty.");
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var idList = userIds.Distinct().ToList();
        var emails = await context.Users
            .Where(u => idList.Contains(u.Id) && !string.IsNullOrEmpty(u.Email))
            .Select(u => u.Email!)
            .ToListAsync();

        if (emails.Count == 0)
        {
            return (0, "No valid email addresses found for the selected users.");
        }

        var html = $@"<div style=""white-space: pre-wrap; font-size: 15px; line-height: 1.6;"">{System.Net.WebUtility.HtmlEncode(message)}</div>";
        await _emailNotificationService.SendBulkEmailAsync(emails, $"[Pdnode Vote] {subject}", html);

        return (emails.Count, $"Email sent to {emails.Count} user(s).");
    }

    private static readonly HashSet<string> AssignableRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Moderator"
    };

    public async Task<(bool Success, string Message)> UpdateUserRoleAsync(string userId, string targetRole, bool enable)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null) return (false, "User not found.");

        if (user.IsRootAdmin && string.Equals(targetRole, "Admin", StringComparison.OrdinalIgnoreCase) && !enable)
        {
            return (false, "Cannot remove Admin role from the primary system admin account.");
        }

        if (string.IsNullOrWhiteSpace(targetRole) || !AssignableRoles.Contains(targetRole))
        {
            return (false, "This role cannot be assigned through this action.");
        }

        if (!await _roleManager.RoleExistsAsync(targetRole))
        {
            await _roleManager.CreateAsync(new IdentityRole(targetRole));
        }

        if (enable)
        {
            if (!await _userManager.IsInRoleAsync(user, targetRole))
            {
                await _userManager.AddToRoleAsync(user, targetRole);
            }
        }
        else
        {
            if (await _userManager.IsInRoleAsync(user, targetRole))
            {
                await _userManager.RemoveFromRoleAsync(user, targetRole);
            }
        }

        return (true, $"User role '{targetRole}' {(enable ? "granted" : "revoked")} successfully.");
    }
}
