using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PdnodeVote.Client.Models;
using PdnodeVote.Data;
using PdnodeVote.Services;

namespace PdnodeVote.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/admin")
            .RequireAuthorization(policy => policy.RequireRole("Admin", "Moderator", "SuperModerator"));

        // GET /api/admin/stats
        group.MapGet("/stats", async (IAdminService adminService) =>
        {
            var stats = await adminService.GetStatsAsync();
            return Results.Ok(new PdnodeVote.Client.Models.AdminDashboardStatsDto
            {
                TotalUsers = stats.TotalUsers,
                TotalPolls = stats.TotalPolls,
                PendingReviewCount = stats.PendingReviewCount,
                BannedUsersCount = stats.BannedUsersCount
            });
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        // GET /api/admin/pending
        group.MapGet("/pending", async (PollService pollService) =>
        {
            var polls = await pollService.GetPendingReviewPollsAsync();
            return Results.Ok(polls);
        });

        // GET /api/admin/polls
        group.MapGet("/polls", async (
            PollService pollService,
            [FromQuery] string? search,
            [FromQuery] int? status) =>
        {
            PdnodeVote.Data.PollStatus? pollStatus = status.HasValue ? (PdnodeVote.Data.PollStatus)status.Value : null;
            var polls = await pollService.GetAllAdminPollsAsync(search, pollStatus);
            return Results.Ok(polls);
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        // POST /api/admin/polls/{id}/approve
        group.MapPost("/polls/{id:int}/approve", async (
            int id,
            PollService pollService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
            var res = await pollService.ApprovePollAsync(id, userId);
            return Results.Ok(new ServiceResult { Success = res.Success, Message = res.Message, PollId = id });
        });

        // POST /api/admin/polls/{id}/return
        group.MapPost("/polls/{id:int}/return", async (
            int id,
            ModerationActionRequest req,
            PollService pollService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
            var res = await pollService.ReturnPollForRevisionAsync(id, userId, req.Reason ?? "");
            return Results.Ok(new ServiceResult { Success = res.Success, Message = res.Message, PollId = id });
        });

        // POST /api/admin/polls/{id}/remove
        group.MapPost("/polls/{id:int}/remove", async (
            int id,
            ModerationActionRequest req,
            PollService pollService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
            var res = await pollService.RemovePollAsync(id, userId, req.Reason ?? "");
            return Results.Ok(new ServiceResult { Success = res.Success, Message = res.Message, PollId = id });
        });

        // POST /api/admin/polls/{id}/archive
        group.MapPost("/polls/{id:int}/archive", async (
            int id,
            ModerationActionRequest req,
            PollService pollService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
            var res = await pollService.ArchivePollAsync(id, userId, req.Reason ?? "Archived by Administrator");
            return Results.Ok(new ServiceResult { Success = res.Success, Message = res.Message, PollId = id });
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        // POST /api/admin/polls/{id}/pin
        group.MapPost("/polls/{id:int}/pin", async (
            int id,
            PollService pollService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
            var res = await pollService.TogglePinPollAsync(id, userId);
            return Results.Ok(new ServiceResult { Success = res.Success, Message = res.Message, PollId = id });
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        // GET /api/admin/users
        group.MapGet("/users", async (
            IAdminService adminService,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            [FromQuery] string? search = null) =>
        {
            var users = await adminService.GetUsersAsync(search);
            var dtos = users.Select(u => new PdnodeVote.Client.Models.AdminUserDto
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

            return Results.Ok(dtos);
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        // POST /api/admin/users/ban
        group.MapPost("/users/ban", async (
            BanUserRequest req,
            IAdminService adminService,
            HttpContext httpContext) =>
        {
            var adminId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
            bool isPermanent = string.Equals(req.BanType, "Permanent", StringComparison.OrdinalIgnoreCase);
            var res = await adminService.BanUsersAsync(new[] { req.UserId }, isPermanent, req.DurationDays, req.Reason, adminId);
            return Results.Ok(new ServiceResult { Success = res.SuccessCount > 0, Message = res.Message });
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        // POST /api/admin/users/unban
        group.MapPost("/users/unban", async (
            [FromBody] BanUserRequest req,
            IAdminService adminService) =>
        {
            var res = await adminService.UnbanUsersAsync(new[] { req.UserId });
            return Results.Ok(new ServiceResult { Success = res.SuccessCount > 0, Message = res.Message });
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        // POST /api/admin/users/reset-password
        group.MapPost("/users/reset-password", async (
            ResetPasswordRequest req,
            IAdminService adminService) =>
        {
            var res = await adminService.ResetUserPasswordAsync(req.UserId);
            return Results.Ok(new ServiceResult { Success = res.Success, Message = res.Message });
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        // POST /api/admin/users/role
        group.MapPost("/users/role", async (
            UpdateRoleRequest req,
            IAdminService adminService) =>
        {
            var res = await adminService.UpdateUserRoleAsync(req.UserId, req.NewRole, req.Enable);
            return Results.Ok(new ServiceResult { Success = res.Success, Message = res.Message });
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        // POST /api/admin/users/bulk-email
        group.MapPost("/users/bulk-email", async (
            BulkEmailRequest req,
            IAdminService adminService) =>
        {
            var res = await adminService.SendBulkEmailToUsersAsync(req.UserIds, req.Subject, req.Body);
            return Results.Ok(new ServiceResult { Success = res.SentCount > 0, Message = res.Message });
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));

        // GET /api/admin/category-requests
        group.MapGet("/category-requests", async (
            ICategoryService categoryService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var requests = await categoryService.GetPendingCategoryRequestsAsync(userId);
            return Results.Ok(requests);
        });

        // POST /api/admin/category-requests/{id:int}/review
        group.MapPost("/category-requests/{id:int}/review", async (
            int id,
            ReviewCategoryRequest req,
            ICategoryService categoryService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
            var res = await categoryService.ReviewCategoryRequestAsync(userId, id, req);
            return Results.Ok(res);
        });

        // GET /api/admin/comments/pending
        group.MapGet("/comments/pending", async (ICommentService commentService) =>
        {
            var pending = await commentService.GetPendingCommentsAsync();
            return Results.Ok(pending);
        });

        // POST /api/admin/comments/{id:int}/approve
        group.MapPost("/comments/{id:int}/approve", async (
            int id,
            ICommentService commentService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
            var res = await commentService.ApproveCommentAsync(id, userId);
            return Results.Ok(res);
        });

        // POST /api/admin/comments/{id:int}/remove
        group.MapPost("/comments/{id:int}/remove", async (
            int id,
            ModerationActionRequest req,
            ICommentService commentService,
            HttpContext httpContext) =>
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
            var res = await commentService.RemoveCommentAsync(id, userId, req.Reason ?? "");
            return Results.Ok(res);
        });

        // ==================== CONTENT REPORTS MODERATION ====================
        // GET /api/admin/reports
        group.MapGet("/reports", async (
            IReportService reportService,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 15) =>
        {
            var reports = await reportService.GetPendingReportsAsync(page, pageSize);
            return Results.Ok(reports);
        });

        // POST /api/admin/reports/{id:int}/resolve
        group.MapPost("/reports/{id:int}/resolve", async (
            int id,
            ResolveReportRequest req,
            IReportService reportService,
            HttpContext httpContext) =>
        {
            var adminId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "";
            var res = await reportService.ResolveReportAsync(adminId, id, req);
            if (!res.Success) return Results.BadRequest(res);
            return Results.Ok(res);
        });

        // ==================== USERS PAGED ====================
        // GET /api/admin/users/paged
        group.MapGet("/users/paged", async (
            IAdminService adminService,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 15,
            [FromQuery] string? search = null,
            [FromQuery] string? roleFilter = null) =>
        {
            var users = await adminService.GetUsersPagedAsync(page, pageSize, search, roleFilter);
            return Results.Ok(users);
        }).RequireAuthorization(policy => policy.RequireRole("Admin"));
    }
}

