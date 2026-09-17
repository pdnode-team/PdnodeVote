using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PdnodeVote.Client.Models;
using PdnodeVote.Data;
using PollStatus = PdnodeVote.Data.PollStatus;

namespace PdnodeVote.Services;

public class ReportService : IReportService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IPollEventNotifier _notifier;
    private readonly INotificationService _notificationService;

    public ReportService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        UserManager<ApplicationUser> userManager,
        IPollEventNotifier notifier,
        INotificationService notificationService)
    {
        _dbContextFactory = dbContextFactory;
        _userManager = userManager;
        _notifier = notifier;
        _notificationService = notificationService;
    }

    public async Task<ServiceResult> SubmitReportAsync(string reporterId, SubmitReportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return ServiceResult.Fail("Please select or provide a reason for the report.");
        }

        if (!request.PollId.HasValue && !request.CommentId.HasValue)
        {
            return ServiceResult.Fail("Target content (Poll or Comment) must be specified.");
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var reporter = await context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == reporterId);
        if (reporter == null) return ServiceResult.Fail("User not found or unauthenticated.");
        if (reporter.IsCurrentlyBanned) return ServiceResult.Fail("Your account is suspended.");

        // Prevent duplicate spam reporting by the same user
        bool alreadyReported = await context.ContentReports.AnyAsync(r =>
            r.ReporterId == reporterId &&
            r.Status == ContentReportStatus.Pending &&
            r.PollId == request.PollId &&
            r.CommentId == request.CommentId);

        if (alreadyReported)
        {
            return ServiceResult.Fail("You have already reported this content. Our moderation team is reviewing it.");
        }

        var report = new ContentReport
        {
            ReporterId = reporterId,
            PollId = request.PollId,
            CommentId = request.CommentId,
            Reason = request.Reason.Trim(),
            Details = request.Details?.Trim(),
            Status = ContentReportStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        context.ContentReports.Add(report);
        await context.SaveChangesAsync();

        return ServiceResult.Ok("Report submitted successfully. Thank you for keeping the community safe.");
    }

    public async Task<PagedResult<ContentReportDto>> GetPendingReportsAsync(int page = 1, int pageSize = 15)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 15;

        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var query = context.ContentReports
            .Include(r => r.Reporter)
            .Include(r => r.Poll!).ThenInclude(p => p.Creator)
            .Include(r => r.Comment!).ThenInclude(c => c.User)
            .Include(r => r.Comment!).ThenInclude(c => c.Poll)
            .Where(r => r.Status == ContentReportStatus.Pending);

        var total = await query.CountAsync();

        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync();

        var dtos = items.Select(r => new ContentReportDto
        {
            Id = r.Id,
            ReporterId = r.ReporterId,
            ReporterName = PollService.FormatDisplayName(r.Reporter?.UserName, r.Reporter?.Email),
            PollId = r.PollId ?? r.Comment?.PollId,
            PollTitle = r.Poll?.Title ?? r.Comment?.Poll?.Title,
            CommentId = r.CommentId,
            CommentContent = r.Comment?.Content,
            AuthorId = r.Poll?.CreatorId ?? r.Comment?.UserId,
            AuthorName = r.Poll != null 
                ? PollService.FormatDisplayName(r.Poll.Creator?.UserName, r.Poll.Creator?.Email)
                : PollService.FormatDisplayName(r.Comment?.User?.UserName, r.Comment?.User?.Email),
            Reason = r.Reason,
            Details = r.Details,
            Status = (int)r.Status,
            CreatedAt = r.CreatedAt
        }).ToList();

        return new PagedResult<ContentReportDto>
        {
            Items = dtos,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<ServiceResult> ResolveReportAsync(string moderatorId, int reportId, ResolveReportRequest request)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        // Moderation of reports is open to moderators, but permanently banning an author is an
        // administrative action (/api/admin/users/ban requires the Admin role, and the dashboard
        // only offers "Remove & Ban Author" to admins). Without this check any Moderator could
        // permanently ban any user through the report queue.
        bool callerIsAdmin = false;
        if (!string.IsNullOrEmpty(moderatorId))
        {
            var caller = await _userManager.FindByIdAsync(moderatorId);
            callerIsAdmin = caller != null
                && (caller.IsRootAdmin || await _userManager.IsInRoleAsync(caller, "Admin"));
        }

        if (request.BanAuthor && !callerIsAdmin)
        {
            return ServiceResult.Fail("Only administrators can suspend the author of a reported item.");
        }

        var report = await context.ContentReports
            .Include(r => r.Poll)
            .Include(r => r.Comment)
            .FirstOrDefaultAsync(r => r.Id == reportId);

        if (report == null) return ServiceResult.Fail("Report not found.");
        if (report.Status != ContentReportStatus.Pending) return ServiceResult.Fail("This report has already been processed.");

        report.ResolvedById = moderatorId;
        report.ResolvedAt = DateTime.UtcNow;
        report.ResolutionNotes = request.Notes?.Trim();

        if (request.Dismiss)
        {
            report.Status = ContentReportStatus.Dismissed;
            await context.SaveChangesAsync();
            return ServiceResult.Ok("Report dismissed.");
        }

        report.Status = ContentReportStatus.Resolved;

        // Content Removal
        if (request.RemoveContent)
        {
            if (report.PollId.HasValue && report.Poll != null)
            {
                report.Poll.Status = PollStatus.Removed;
                report.Poll.ModerationReason = string.IsNullOrWhiteSpace(request.Notes) ? $"Removed due to community report: {report.Reason}" : request.Notes;
                _notifier.NotifyPollUpdated(report.PollId.Value);

                await _notificationService.CreateNotificationAsync(
                    report.Poll.CreatorId,
                    NotificationType.PollRejected,
                    "Your poll was removed",
                    $"Your poll '{report.Poll.Title}' has been removed by moderators: {report.Poll.ModerationReason}",
                    $"/poll/{report.Poll.Id}");
            }
            else if (report.CommentId.HasValue && report.Comment != null)
            {
                report.Comment.Status = PollStatus.Removed;
                _notifier.NotifyCommentDeleted(report.Comment.PollId, report.Comment.Id);

                await _notificationService.CreateNotificationAsync(
                    report.Comment.UserId,
                    NotificationType.CommentModerated,
                    "Your comment was removed",
                    $"Your comment in poll #{report.Comment.PollId} was removed by moderators for violating community guidelines.",
                    $"/poll/{report.Comment.PollId}");
            }
        }

        // Author Banning
        if (request.BanAuthor)
        {
            string? authorId = report.Poll?.CreatorId ?? report.Comment?.UserId;
            if (!string.IsNullOrEmpty(authorId))
            {
                var author = await _userManager.FindByIdAsync(authorId);
                if (author != null && !author.IsRootAdmin)
                {
                    author.IsBanned = true;
                    author.BannedUntil = null; // permanent
                    author.BanReason = $"Account suspended due to report violations: {report.Reason}";

                    // Go through UserManager and rotate the security stamp: writing straight to the
                    // DbContext left the stamp untouched, so the banned user's existing auth cookie
                    // and Blazor circuit stayed valid indefinitely.
                    await _userManager.UpdateAsync(author);
                    await _userManager.UpdateSecurityStampAsync(author);
                }
                else if (author != null && author.IsRootAdmin)
                {
                    // The primary administrator is not suspendable through the report queue.
                    report.ResolutionNotes = string.IsNullOrWhiteSpace(report.ResolutionNotes)
                        ? "Author suspension skipped: primary administrator is protected."
                        : report.ResolutionNotes + " Author suspension skipped: primary administrator is protected.";
                }
            }
        }

        await context.SaveChangesAsync();
        return ServiceResult.Ok("Report resolved successfully.");
    }
}
