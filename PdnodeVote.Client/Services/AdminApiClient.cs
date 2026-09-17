using System.Net.Http.Json;
using PdnodeVote.Client.Models;

namespace PdnodeVote.Client.Services;

public interface IAdminApiClient
{
    Task<AdminDashboardStatsDto> GetStatsAsync();
    Task<List<PendingReviewPollDto>> GetPendingPollsAsync();
    Task<List<PollListItemDto>> GetAllPollsAsync(string? search = null, PollStatus? status = null);
    Task<ServiceResult> ApprovePollAsync(int pollId);
    Task<ServiceResult> ReturnPollForRevisionAsync(int pollId, string reason);
    Task<ServiceResult> RemovePollAsync(int pollId, string reason);
    Task<ServiceResult> ArchivePollAsync(int pollId, string reason);
    Task<ServiceResult> TogglePinPollAsync(int pollId);

    Task<List<AdminUserDto>> GetUsersAsync(int page = 1, int pageSize = 50, string? search = null);
    Task<ServiceResult> BanUserAsync(BanUserRequest request);
    Task<ServiceResult> UnbanUserAsync(string userId);
    Task<ServiceResult> ResetPasswordAsync(string userId);
    Task<ServiceResult> UpdateRoleAsync(UpdateRoleRequest request);
    Task<ServiceResult> SendBulkEmailAsync(BulkEmailRequest request);

    Task<List<CategoryRequestDto>> GetCategoryRequestsAsync();
    Task<ServiceResult> ReviewCategoryRequestAsync(int requestId, ReviewCategoryRequest request);
    Task<List<PendingCommentDto>> GetPendingCommentsAsync();
    Task<ServiceResult> ApproveCommentAsync(int commentId);
    Task<ServiceResult> RemoveCommentAsync(int commentId, string reason);

    Task<PagedResult<ContentReportDto>> GetPendingReportsAsync(int page = 1, int pageSize = 15);
    Task<ServiceResult> ResolveReportAsync(int reportId, ResolveReportRequest request);
    Task<PagedResult<AdminUserDto>> GetUsersPagedAsync(int page = 1, int pageSize = 15, string? search = null, string? roleFilter = null);
}

public class AdminApiClient(HttpClient httpClient) : IAdminApiClient
{
    public async Task<AdminDashboardStatsDto> GetStatsAsync()
    {
        try
        {
            var res = await httpClient.GetFromJsonAsync<AdminDashboardStatsDto>("api/admin/stats");
            return res ?? new AdminDashboardStatsDto();
        }
        catch
        {
            return new AdminDashboardStatsDto();
        }
    }

    public async Task<List<PendingReviewPollDto>> GetPendingPollsAsync()
    {
        try
        {
            var res = await httpClient.GetFromJsonAsync<List<PendingReviewPollDto>>("api/admin/pending");
            return res ?? new List<PendingReviewPollDto>();
        }
        catch
        {
            return new List<PendingReviewPollDto>();
        }
    }

    public async Task<List<PollListItemDto>> GetAllPollsAsync(string? search = null, PollStatus? status = null)
    {
        try
        {
            var query = $"?search={Uri.EscapeDataString(search ?? "")}";
            if (status.HasValue) query += $"&status={(int)status.Value}";
            var res = await httpClient.GetFromJsonAsync<List<PollListItemDto>>($"api/admin/polls{query}");
            return res ?? new List<PollListItemDto>();
        }
        catch
        {
            return new List<PollListItemDto>();
        }
    }

    public async Task<ServiceResult> ApprovePollAsync(int pollId)
    {
        try
        {
            var res = await httpClient.PostAsync($"api/admin/polls/{pollId}/approve", null);
            return await ApiResponse.ReadAsync(res);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail(ex.Message);
        }
    }

    public async Task<ServiceResult> ReturnPollForRevisionAsync(int pollId, string reason)
    {
        try
        {
            var res = await httpClient.PostAsJsonAsync($"api/admin/polls/{pollId}/return", new ModerationActionRequest { Reason = reason });
            return await ApiResponse.ReadAsync(res);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail(ex.Message);
        }
    }

    public async Task<ServiceResult> RemovePollAsync(int pollId, string reason)
    {
        try
        {
            var res = await httpClient.PostAsJsonAsync($"api/admin/polls/{pollId}/remove", new ModerationActionRequest { Reason = reason });
            return await ApiResponse.ReadAsync(res);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail(ex.Message);
        }
    }

    public async Task<ServiceResult> ArchivePollAsync(int pollId, string reason)
    {
        try
        {
            var res = await httpClient.PostAsJsonAsync($"api/admin/polls/{pollId}/archive", new ModerationActionRequest { Reason = reason });
            return await ApiResponse.ReadAsync(res);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail(ex.Message);
        }
    }

    public async Task<ServiceResult> TogglePinPollAsync(int pollId)
    {
        try
        {
            var res = await httpClient.PostAsync($"api/admin/polls/{pollId}/pin", null);
            return await ApiResponse.ReadAsync(res);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail(ex.Message);
        }
    }

    public async Task<List<AdminUserDto>> GetUsersAsync(int page = 1, int pageSize = 50, string? search = null)
    {
        try
        {
            var query = $"?page={page}&pageSize={pageSize}&search={Uri.EscapeDataString(search ?? "")}";
            var res = await httpClient.GetFromJsonAsync<List<AdminUserDto>>($"api/admin/users{query}");
            return res ?? new List<AdminUserDto>();
        }
        catch
        {
            return new List<AdminUserDto>();
        }
    }

    public async Task<ServiceResult> BanUserAsync(BanUserRequest request)
    {
        try
        {
            var res = await httpClient.PostAsJsonAsync("api/admin/users/ban", request);
            return await ApiResponse.ReadAsync(res);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail(ex.Message);
        }
    }

    public async Task<ServiceResult> UnbanUserAsync(string userId)
    {
        try
        {
            var res = await httpClient.PostAsJsonAsync("api/admin/users/unban", new { UserId = userId });
            return await ApiResponse.ReadAsync(res);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail(ex.Message);
        }
    }

    public async Task<ServiceResult> ResetPasswordAsync(string userId)
    {
        try
        {
            var res = await httpClient.PostAsJsonAsync("api/admin/users/reset-password", new ResetPasswordRequest { UserId = userId });
            return await ApiResponse.ReadAsync(res);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail(ex.Message);
        }
    }

    public async Task<ServiceResult> UpdateRoleAsync(UpdateRoleRequest request)
    {
        try
        {
            var res = await httpClient.PostAsJsonAsync("api/admin/users/role", request);
            return await ApiResponse.ReadAsync(res);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail(ex.Message);
        }
    }

    public async Task<ServiceResult> SendBulkEmailAsync(BulkEmailRequest request)
    {
        try
        {
            var res = await httpClient.PostAsJsonAsync("api/admin/users/bulk-email", request);
            return await ApiResponse.ReadAsync(res);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail(ex.Message);
        }
    }

    public async Task<List<CategoryRequestDto>> GetCategoryRequestsAsync()
    {
        try
        {
            var res = await httpClient.GetFromJsonAsync<List<CategoryRequestDto>>("api/admin/category-requests");
            return res ?? new List<CategoryRequestDto>();
        }
        catch
        {
            return new List<CategoryRequestDto>();
        }
    }

    public async Task<ServiceResult> ReviewCategoryRequestAsync(int requestId, ReviewCategoryRequest request)
    {
        try
        {
            var res = await httpClient.PostAsJsonAsync($"api/admin/category-requests/{requestId}/review", request);
            return await ApiResponse.ReadAsync(res);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail(ex.Message);
        }
    }

    public async Task<List<PendingCommentDto>> GetPendingCommentsAsync()
    {
        try
        {
            var res = await httpClient.GetFromJsonAsync<List<PendingCommentDto>>("api/admin/comments/pending");
            return res ?? new List<PendingCommentDto>();
        }
        catch
        {
            return new List<PendingCommentDto>();
        }
    }

    public async Task<ServiceResult> ApproveCommentAsync(int commentId)
    {
        try
        {
            var res = await httpClient.PostAsync($"api/admin/comments/{commentId}/approve", null);
            return await ApiResponse.ReadAsync(res);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail(ex.Message);
        }
    }

    public async Task<ServiceResult> RemoveCommentAsync(int commentId, string reason)
    {
        try
        {
            var res = await httpClient.PostAsJsonAsync($"api/admin/comments/{commentId}/remove", new ModerationActionRequest { Reason = reason });
            return await ApiResponse.ReadAsync(res);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail(ex.Message);
        }
    }

    public async Task<PagedResult<ContentReportDto>> GetPendingReportsAsync(int page = 1, int pageSize = 15)
    {
        try
        {
            var res = await httpClient.GetFromJsonAsync<PagedResult<ContentReportDto>>($"api/admin/reports?page={page}&pageSize={pageSize}");
            return res ?? new PagedResult<ContentReportDto>();
        }
        catch
        {
            return new PagedResult<ContentReportDto>();
        }
    }

    public async Task<ServiceResult> ResolveReportAsync(int reportId, ResolveReportRequest request)
    {
        try
        {
            var res = await httpClient.PostAsJsonAsync($"api/admin/reports/{reportId}/resolve", request);
            return await ApiResponse.ReadAsync(res);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail(ex.Message);
        }
    }

    public async Task<PagedResult<AdminUserDto>> GetUsersPagedAsync(int page = 1, int pageSize = 15, string? search = null, string? roleFilter = null)
    {
        try
        {
            var url = $"api/admin/users/paged?page={page}&pageSize={pageSize}";
            if (!string.IsNullOrWhiteSpace(search))
            {
                url += $"&search={Uri.EscapeDataString(search)}";
            }
            if (!string.IsNullOrWhiteSpace(roleFilter))
            {
                url += $"&roleFilter={Uri.EscapeDataString(roleFilter)}";
            }

            var res = await httpClient.GetFromJsonAsync<PagedResult<AdminUserDto>>(url);
            return res ?? new PagedResult<AdminUserDto>();
        }
        catch
        {
            return new PagedResult<AdminUserDto>();
        }
    }
}

