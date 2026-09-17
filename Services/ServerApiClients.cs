using System.Buffers;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using PdnodeVote.Client.Models;
using PdnodeVote.Client.Services;
using PdnodeVote.Data;

namespace PdnodeVote.Services;

public class ServerPollApiClient(
    PollService pollService,
    ICategoryService categoryService,
    ICommentService commentService,
    IReportService reportService,
    INotificationService notificationService,
    IHttpContextAccessor httpContextAccessor,
    AuthenticationStateProvider authStateProvider,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IWebHostEnvironment env) : IPollApiClient
{
    /// <summary>Same ceiling as the HTTP upload endpoint, so the two paths cannot diverge.</summary>
    private const long MaxImageBytes = 5 * 1024 * 1024;

    public async Task<List<Client.Models.PollListItemDto>> GetPollsAsync(string? search = null, string? status = "all", string? sortBy = "latest", int? categoryId = null, string? tag = null)
    {
        return await pollService.GetPollsAsync(search, status, sortBy, categoryId, tag);
    }

    public async Task<Client.Models.PollDetailDto?> GetPollDetailAsync(int pollId)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var clientIp = GetClientIp();

        return await pollService.GetPollDetailAsync(pollId, userId, clientIp);
    }

    public async Task<ServiceResult> CreatePollAsync(CreatePollRequest request)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return ServiceResult.Fail("Unauthorized");

        var visibility = request.ResultVisibility == Client.Models.ResultVisibility.AfterVoting
            ? PdnodeVote.Data.ResultVisibility.AfterVoting
            : PdnodeVote.Data.ResultVisibility.AlwaysPublic;

        var (success, message, pollId) = await pollService.CreatePollAsync(
            request.Title,
            request.Description,
            userId,
            request.RequireLogin,
            request.IsMultipleChoice,
            request.MaxChoices,
            visibility,
            request.ExpiresAt,
            request.Options,
            request.CategoryId,
            request.Tags
        );

        return success ? ServiceResult.Ok(message, pollId) : ServiceResult.Fail(message);
    }

    public async Task<ServiceResult> CastVoteAsync(int pollId, List<int> selectedOptionIds, string? turnstileToken = null)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var clientIp = GetClientIp();

        var (success, message) = await pollService.CastVoteAsync(pollId, userId, clientIp, selectedOptionIds);
        return success ? ServiceResult.Ok(message, pollId) : ServiceResult.Fail(message);
    }

    public async Task<List<Client.Models.PollListItemDto>> GetUserPollsAsync()
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return new List<Client.Models.PollListItemDto>();

        return await pollService.GetUserPollsAsync(userId);
    }

    public async Task<ServiceResult> DeletePollAsync(int pollId)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return ServiceResult.Fail("Unauthorized");

        var (success, message) = await pollService.DeletePollAsync(pollId, userId);
        return success ? ServiceResult.Ok(message, pollId) : ServiceResult.Fail(message);
    }

    public async Task<ServiceResult> ResubmitPollAsync(int pollId, ResubmitPollRequest request)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return ServiceResult.Fail("Unauthorized");

        var res = await pollService.UpdateAndResubmitPollAsync(pollId, userId, request.Title, request.Description, request.Options, request.CategoryId, request.Tags);
        return res.Success ? ServiceResult.Ok(res.Message, pollId) : ServiceResult.Fail(res.Message);
    }

    public async Task<List<CategoryDto>> GetCategoriesAsync()
    {
        return await categoryService.GetCategoryTreeAsync();
    }

    public async Task<List<TagDto>> GetPopularTagsAsync()
    {
        return await categoryService.GetPopularTagsAsync();
    }

    public async Task<ServiceResult> SubmitCategoryRequestAsync(SubmitCategoryRequest request)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return ServiceResult.Fail("Unauthorized");

        return await categoryService.SubmitCategoryRequestAsync(userId, request);
    }

    public async Task<bool> GetCommentGatingAsync()
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return false;
        return await commentService.RequiresCommentModerationAsync(userId);
    }

    public async Task<List<PollCommentDto>> GetCommentsAsync(int pollId)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return await commentService.GetPollCommentsAsync(pollId, userId);
    }

    public async Task<ServiceResult> AddCommentAsync(CreateCommentRequest request)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return ServiceResult.Fail("Unauthorized");

        return await commentService.AddCommentAsync(request.PollId, userId, request.Content, request.ParentCommentId);
    }

    public async Task<ServiceResult> DeleteCommentAsync(int commentId)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return ServiceResult.Fail("Unauthorized");

        return await commentService.DeleteCommentAsync(commentId, userId);
    }

    public async Task<PagedResult<Client.Models.PollListItemDto>> GetPollFeedAsync(int page = 1, int pageSize = 15, string? search = null, string? status = "all", string? sortBy = "latest", int? categoryId = null, string? tag = null, string? author = null, bool onlySubscribed = false)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return await pollService.GetPollFeedAsync(page, pageSize, search, status, sortBy, categoryId, tag, author, onlySubscribed, userId);
    }

    public async Task<PagedResult<UserVotedPollDto>> GetVotedPollsAsync(int page = 1, int pageSize = 15)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return new PagedResult<UserVotedPollDto>();

        return await pollService.GetVotedPollsByUserAsync(userId, page, pageSize);
    }

    public async Task<PagedResult<UserCommentedPollDto>> GetCommentedPollsAsync(int page = 1, int pageSize = 15)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return new PagedResult<UserCommentedPollDto>();

        return await pollService.GetCommentedPollsByUserAsync(userId, page, pageSize);
    }

    public async Task<UserProfileDto?> GetUserProfileAsync(string username)
    {
        return await pollService.GetUserPublicProfileAsync(username);
    }

    public async Task<ServiceResult> ToggleCategorySubscriptionAsync(int categoryId)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return ServiceResult.Fail("Unauthorized");

        return await pollService.ToggleCategorySubscriptionAsync(userId, categoryId);
    }

    public async Task<List<int>> GetSubscribedCategoryIdsAsync()
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return new List<int>();

        return await pollService.GetSubscribedCategoryIdsAsync(userId);
    }

    public async Task<ServiceResult> UpdatePollAsync(int pollId, UpdatePollRequest request)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return ServiceResult.Fail("Unauthorized");

        return await pollService.UpdatePollAsync(pollId, userId, request);
    }

    public async Task<ServiceResult> WithdrawToDraftAsync(int pollId)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return ServiceResult.Fail("Unauthorized");

        return await pollService.WithdrawToDraftAsync(pollId, userId);
    }

    public async Task<ServiceResult> SubmitReportAsync(SubmitReportRequest request)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return ServiceResult.Fail("Unauthorized");

        return await reportService.SubmitReportAsync(userId, request);
    }

    public async Task<List<NotificationDto>> GetNotificationsAsync(int limit = 20)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return new List<NotificationDto>();

        return await notificationService.GetNotificationsAsync(userId, limit);
    }

    public async Task<int> GetUnreadNotificationCountAsync()
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return 0;

        return await notificationService.GetUnreadCountAsync(userId);
    }

    public async Task<ServiceResult> MarkNotificationsAsReadAsync(int? notificationId = null)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return ServiceResult.Fail("Unauthorized");

        await notificationService.MarkAsReadAsync(userId, notificationId);
        return ServiceResult.Ok("Marked as read");
    }

    public async Task<UserProfileDto?> GetUserPublicProfileAsync(string username)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var currentUserId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return await pollService.GetUserPublicProfileAsync(username, currentUserId);
    }

    public async Task<ServiceResult> UpvoteCommentAsync(int commentId)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return ServiceResult.Fail("Unauthorized");
        return await commentService.UpvoteCommentAsync(commentId, userId);
    }

    public async Task<ServiceResult> TogglePinCommentAsync(int commentId)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return ServiceResult.Fail("Unauthorized");
        return await commentService.TogglePinCommentAsync(commentId, userId);
    }

    public async Task<string?> UploadImageAsync(Stream stream, string fileName)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return null;

        var allowedExts = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!allowedExts.Contains(ext)) return null;

        // Mirror the HTTP endpoint's 5 MB ceiling (POST /api/upload/image). This path writes straight to
        // the volume without an HTTP round trip, so without the check a server-interactive client could
        // push an arbitrarily large "image".
        if (stream.CanSeek && stream.Length - stream.Position > MaxImageBytes) return null;

        var uploadsFolder = Path.Combine(env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), "uploads");
        if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

        var safeName = $"opt_{Guid.NewGuid():N}{ext}";
        var filePath = Path.Combine(uploadsFolder, safeName);
        var tooLarge = false;

        await using (var fileStream = new FileStream(filePath, FileMode.Create))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long total = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    total += read;
                    // A non-seekable stream cannot be measured up front, so stop the moment the ceiling
                    // is crossed instead of trusting the caller's declared length.
                    if (total > MaxImageBytes)
                    {
                        tooLarge = true;
                        break;
                    }

                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        if (tooLarge)
        {
            // Never leave the truncated file behind; it would be served as a broken image.
            File.Delete(filePath);
            return null;
        }

        return $"/uploads/{safeName}";
    }

    public async Task<SystemAnnouncementDto?> GetActiveAnnouncementAsync()
    {
        await using var context = await dbFactory.CreateDbContextAsync();
        var item = await context.SystemAnnouncements.AsNoTracking().FirstOrDefaultAsync(a => a.IsActive);
        if (item == null) return null;
        return new SystemAnnouncementDto
        {
            Id = item.Id,
            Message = item.Message,
            TargetUrl = item.TargetUrl,
            IsActive = item.IsActive,
            UpdatedAt = item.UpdatedAt
        };
    }

    public async Task<ServiceResult> UpdateAnnouncementAsync(UpdateAnnouncementRequest request)
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!auth.User.IsInRole("Admin") && !PdnodeVote.Data.SystemConstants.IsRootAdmin(userId))
        {
            return ServiceResult.Fail("Forbidden");
        }

        await using var context = await dbFactory.CreateDbContextAsync();
        var item = await context.SystemAnnouncements.FirstOrDefaultAsync(a => a.Id == 1);
        if (item == null)
        {
            item = new SystemAnnouncement { Id = 1 };
            context.SystemAnnouncements.Add(item);
        }

        item.Message = request.Message?.Trim() ?? string.Empty;
        item.TargetUrl = string.IsNullOrWhiteSpace(request.TargetUrl) ? null : request.TargetUrl.Trim();
        item.IsActive = request.IsActive;
        item.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync();
        return ServiceResult.Ok("Announcement updated.");
    }

    private string GetClientIp() => ClientIpAccessor.GetClientIp(httpContextAccessor);
}

public class ServerAdminApiClient(
    IAdminService adminService,
    PollService pollService,
    ICategoryService categoryService,
    ICommentService commentService,
    IReportService reportService,
    AuthenticationStateProvider authStateProvider,
    ILogger<ServerAdminApiClient> logger) : IAdminApiClient
{
    /// <summary>
    /// Resolves the caller's admin/moderation level from the authentication state.
    /// </summary>
    /// <remarks>
    /// This client resolves <c>AdminService</c> (and the moderation services) directly in-process, so
    /// the route-level policies on <c>/api/admin/*</c> never run for it. The dashboard's own role flags
    /// are a rendering convenience, not an authorization boundary — every method therefore re-checks
    /// here, mirroring the policies declared in <c>AdminEndpoints</c>: "Admin" operations require the
    /// Admin role (or the root admin), moderation queues additionally accept Moderator/SuperModerator.
    /// </remarks>
    private async Task<(bool IsAdmin, bool IsModerator)> GetCallerRolesAsync()
    {
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        bool isAdmin = auth.User.IsInRole("Admin") || PdnodeVote.Data.SystemConstants.IsRootAdmin(userId);
        bool isModerator = isAdmin
                           || auth.User.IsInRole("SuperModerator")
                           || auth.User.IsInRole("Moderator");

        return (isAdmin, isModerator);
    }

    /// <summary>
    /// Logs a denied call. Collection-returning methods have to answer with an empty collection, which
    /// would otherwise look exactly like "there is nothing to moderate" to the caller.
    /// </summary>
    private void LogDenied(string operation) =>
        logger.LogWarning("Denied in-process admin API call {Operation}: the caller lacks the required role.", operation);

    public async Task<Client.Models.AdminDashboardStatsDto> GetStatsAsync()
    {
        if (!(await GetCallerRolesAsync()).IsAdmin)
        {
            LogDenied(nameof(GetStatsAsync));
            return new Client.Models.AdminDashboardStatsDto();
        }

        var stats = await adminService.GetStatsAsync();
        return new Client.Models.AdminDashboardStatsDto
        {
            TotalUsers = stats.TotalUsers,
            TotalPolls = stats.TotalPolls,
            PendingReviewCount = stats.PendingReviewCount,
            BannedUsersCount = stats.BannedUsersCount
        };
    }

    public async Task<List<Client.Models.PendingReviewPollDto>> GetPendingPollsAsync()
    {
        if (!(await GetCallerRolesAsync()).IsModerator)
        {
            LogDenied(nameof(GetPendingPollsAsync));
            return new List<Client.Models.PendingReviewPollDto>();
        }

        var polls = await pollService.GetPendingReviewPollsAsync();
        return polls.Select(p => new Client.Models.PendingReviewPollDto
        {
            Id = p.Id,
            Title = p.Title,
            Description = p.Description,
            CreatorId = p.CreatorId,
            CreatorName = p.CreatorName,
            CreatorEmail = p.CreatorEmail,
            CreatorCreatedAt = p.CreatorCreatedAt,
            CreatorApprovedPollsCount = p.CreatorApprovedPollsCount,
            CreatedAt = p.CreatedAt,
            Options = p.Options,
            GatingReason = p.GatingReason,
            CategoryId = p.CategoryId,
            CategoryName = p.CategoryName,
            Tags = p.Tags
        }).ToList();
    }

    public async Task<List<Client.Models.PollListItemDto>> GetAllPollsAsync(string? search = null, Client.Models.PollStatus? status = null)
    {
        if (!(await GetCallerRolesAsync()).IsAdmin)
        {
            LogDenied(nameof(GetAllPollsAsync));
            return new List<Client.Models.PollListItemDto>();
        }

        PdnodeVote.Data.PollStatus? pollStatus = status.HasValue ? (PdnodeVote.Data.PollStatus)(int)status.Value : null;
        return await pollService.GetAllAdminPollsAsync(search, pollStatus);
    }

    public async Task<ServiceResult> ApprovePollAsync(int pollId)
    {
        if (!(await GetCallerRolesAsync()).IsModerator) { LogDenied(nameof(ApprovePollAsync)); return ServiceResult.Fail("Forbidden"); }
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var adminId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
        var res = await pollService.ApprovePollAsync(pollId, adminId);
        return res.Success ? ServiceResult.Ok(res.Message, pollId) : ServiceResult.Fail(res.Message);
    }

    public async Task<ServiceResult> ReturnPollForRevisionAsync(int pollId, string reason)
    {
        if (!(await GetCallerRolesAsync()).IsModerator) { LogDenied(nameof(ReturnPollForRevisionAsync)); return ServiceResult.Fail("Forbidden"); }
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var adminId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
        var res = await pollService.ReturnPollForRevisionAsync(pollId, adminId, reason);
        return res.Success ? ServiceResult.Ok(res.Message, pollId) : ServiceResult.Fail(res.Message);
    }

    public async Task<ServiceResult> RemovePollAsync(int pollId, string reason)
    {
        if (!(await GetCallerRolesAsync()).IsModerator) { LogDenied(nameof(RemovePollAsync)); return ServiceResult.Fail("Forbidden"); }
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var adminId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
        var res = await pollService.RemovePollAsync(pollId, adminId, reason);
        return res.Success ? ServiceResult.Ok(res.Message, pollId) : ServiceResult.Fail(res.Message);
    }

    public async Task<ServiceResult> ArchivePollAsync(int pollId, string reason)
    {
        if (!(await GetCallerRolesAsync()).IsAdmin) { LogDenied(nameof(ArchivePollAsync)); return ServiceResult.Fail("Forbidden"); }
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var adminId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
        var res = await pollService.ArchivePollAsync(pollId, adminId, reason);
        return res.Success ? ServiceResult.Ok(res.Message, pollId) : ServiceResult.Fail(res.Message);
    }

    public async Task<ServiceResult> TogglePinPollAsync(int pollId)
    {
        if (!(await GetCallerRolesAsync()).IsAdmin) { LogDenied(nameof(TogglePinPollAsync)); return ServiceResult.Fail("Forbidden"); }
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var adminId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
        var res = await pollService.TogglePinPollAsync(pollId, adminId);
        return res.Success ? ServiceResult.Ok(res.Message, pollId) : ServiceResult.Fail(res.Message);
    }

    public async Task<List<Client.Models.AdminUserDto>> GetUsersAsync(int page = 1, int pageSize = 50, string? search = null)
    {
        if (!(await GetCallerRolesAsync()).IsAdmin)
        {
            LogDenied(nameof(GetUsersAsync));
            return new List<Client.Models.AdminUserDto>();
        }

        var users = await adminService.GetUsersAsync(search);
        return users.Select(u => new Client.Models.AdminUserDto
        {
            Id = u.Id,
            UserName = u.UserName,
            Email = u.Email,
            CreatedAt = u.CreatedAt,
            IsBanned = u.IsBanned,
            BannedUntil = u.BannedUntil,
            BanReason = u.BanReason,
            Roles = u.Roles,
            ApprovedPollsCount = u.ApprovedPollsCount,
            TotalPollsCount = u.TotalPollsCount
        }).ToList();
    }

    public async Task<ServiceResult> BanUserAsync(BanUserRequest request)
    {
        if (!(await GetCallerRolesAsync()).IsAdmin) { LogDenied(nameof(BanUserAsync)); return ServiceResult.Fail("Forbidden"); }
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var adminId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
        bool isPermanent = string.Equals(request.BanType, "Permanent", StringComparison.OrdinalIgnoreCase);
        var (count, msg) = await adminService.BanUsersAsync(new[] { request.UserId }, isPermanent, request.DurationDays, request.Reason, adminId);
        return count > 0 ? ServiceResult.Ok(msg) : ServiceResult.Fail(msg);
    }

    public async Task<ServiceResult> UnbanUserAsync(string userId)
    {
        if (!(await GetCallerRolesAsync()).IsAdmin) { LogDenied(nameof(UnbanUserAsync)); return ServiceResult.Fail("Forbidden"); }
        var (count, msg) = await adminService.UnbanUsersAsync(new[] { userId });
        return count > 0 ? ServiceResult.Ok(msg) : ServiceResult.Fail(msg);
    }

    public async Task<ServiceResult> ResetPasswordAsync(string userId)
    {
        if (!(await GetCallerRolesAsync()).IsAdmin) { LogDenied(nameof(ResetPasswordAsync)); return ServiceResult.Fail("Forbidden"); }
        var (success, msg, _) = await adminService.ResetUserPasswordAsync(userId);
        return success ? ServiceResult.Ok(msg) : ServiceResult.Fail(msg);
    }

    public async Task<ServiceResult> UpdateRoleAsync(UpdateRoleRequest request)
    {
        if (!(await GetCallerRolesAsync()).IsAdmin) { LogDenied(nameof(UpdateRoleAsync)); return ServiceResult.Fail("Forbidden"); }
        var (success, msg) = await adminService.UpdateUserRoleAsync(request.UserId, request.NewRole, request.Enable);
        return success ? ServiceResult.Ok(msg) : ServiceResult.Fail(msg);
    }

    public async Task<ServiceResult> SendBulkEmailAsync(BulkEmailRequest request)
    {
        if (!(await GetCallerRolesAsync()).IsAdmin) { LogDenied(nameof(SendBulkEmailAsync)); return ServiceResult.Fail("Forbidden"); }
        var (count, msg) = await adminService.SendBulkEmailToUsersAsync(request.UserIds, request.Subject, request.Body);
        return count > 0 ? ServiceResult.Ok(msg) : ServiceResult.Fail(msg);
    }

    public async Task<List<CategoryRequestDto>> GetCategoryRequestsAsync()
    {
        if (!(await GetCallerRolesAsync()).IsModerator)
        {
            LogDenied(nameof(GetCategoryRequestsAsync));
            return new List<CategoryRequestDto>();
        }

        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return await categoryService.GetPendingCategoryRequestsAsync(userId);
    }

    public async Task<ServiceResult> ReviewCategoryRequestAsync(int requestId, ReviewCategoryRequest request)
    {
        if (!(await GetCallerRolesAsync()).IsModerator) { LogDenied(nameof(ReviewCategoryRequestAsync)); return ServiceResult.Fail("Forbidden"); }
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return ServiceResult.Fail("Unauthorized");

        return await categoryService.ReviewCategoryRequestAsync(userId, requestId, request);
    }

    public async Task<List<PendingCommentDto>> GetPendingCommentsAsync()
    {
        if (!(await GetCallerRolesAsync()).IsModerator)
        {
            LogDenied(nameof(GetPendingCommentsAsync));
            return new List<PendingCommentDto>();
        }

        return await commentService.GetPendingCommentsAsync();
    }

    public async Task<ServiceResult> ApproveCommentAsync(int commentId)
    {
        if (!(await GetCallerRolesAsync()).IsModerator) { LogDenied(nameof(ApproveCommentAsync)); return ServiceResult.Fail("Forbidden"); }
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return ServiceResult.Fail("Unauthorized");

        return await commentService.ApproveCommentAsync(commentId, userId);
    }

    public async Task<ServiceResult> RemoveCommentAsync(int commentId, string reason)
    {
        if (!(await GetCallerRolesAsync()).IsModerator) { LogDenied(nameof(RemoveCommentAsync)); return ServiceResult.Fail("Forbidden"); }
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var userId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return ServiceResult.Fail("Unauthorized");

        return await commentService.RemoveCommentAsync(commentId, userId, reason);
    }

    public async Task<PagedResult<ContentReportDto>> GetPendingReportsAsync(int page = 1, int pageSize = 15)
    {
        if (!(await GetCallerRolesAsync()).IsModerator)
        {
            LogDenied(nameof(GetPendingReportsAsync));
            return new PagedResult<ContentReportDto> { Page = page, PageSize = pageSize };
        }

        return await reportService.GetPendingReportsAsync(page, pageSize);
    }

    public async Task<ServiceResult> ResolveReportAsync(int reportId, ResolveReportRequest request)
    {
        if (!(await GetCallerRolesAsync()).IsModerator) { LogDenied(nameof(ResolveReportAsync)); return ServiceResult.Fail("Forbidden"); }
        var auth = await authStateProvider.GetAuthenticationStateAsync();
        var adminId = auth.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";

        return await reportService.ResolveReportAsync(adminId, reportId, request);
    }

    public async Task<PagedResult<Client.Models.AdminUserDto>> GetUsersPagedAsync(int page = 1, int pageSize = 15, string? search = null, string? roleFilter = null)
    {
        if (!(await GetCallerRolesAsync()).IsAdmin)
        {
            LogDenied(nameof(GetUsersPagedAsync));
            return new PagedResult<Client.Models.AdminUserDto> { Page = page, PageSize = pageSize };
        }

        var res = await adminService.GetUsersPagedAsync(page, pageSize, search, roleFilter);
        return new PagedResult<Client.Models.AdminUserDto>
        {
            Items = res.Items.Select(u => new Client.Models.AdminUserDto
            {
                Id = u.Id,
                UserName = u.UserName,
                Email = u.Email,
                CreatedAt = u.CreatedAt,
                IsBanned = u.IsBanned,
                BannedUntil = u.BannedUntil,
                BanReason = u.BanReason,
                Roles = u.Roles,
                ApprovedPollsCount = u.ApprovedPollsCount,
                TotalPollsCount = u.TotalPollsCount
            }).ToList(),
            TotalCount = res.TotalCount,
            Page = res.Page,
            PageSize = res.PageSize
        };
    }
}
