using System.Net.Http.Json;
using PdnodeVote.Client.Models;

namespace PdnodeVote.Client.Services;

public interface IPollApiClient
{
    Task<List<PollListItemDto>> GetPollsAsync(string? search = null, string? status = "all", string? sortBy = "latest", int? categoryId = null, string? tag = null);
    Task<PollDetailDto?> GetPollDetailAsync(int pollId);
    Task<ServiceResult> CreatePollAsync(CreatePollRequest request);
    Task<ServiceResult> CastVoteAsync(int pollId, List<int> selectedOptionIds, string? turnstileToken = null);
    Task<List<PollListItemDto>> GetUserPollsAsync();
    Task<ServiceResult> DeletePollAsync(int pollId);
    Task<ServiceResult> ResubmitPollAsync(int pollId, ResubmitPollRequest request);

    Task<List<CategoryDto>> GetCategoriesAsync();
    Task<List<TagDto>> GetPopularTagsAsync();
    Task<ServiceResult> SubmitCategoryRequestAsync(SubmitCategoryRequest request);

    Task<List<PollCommentDto>> GetCommentsAsync(int pollId);
    Task<bool> GetCommentGatingAsync();
    Task<ServiceResult> AddCommentAsync(CreateCommentRequest request);
    Task<ServiceResult> DeleteCommentAsync(int commentId);
    Task<ServiceResult> UpvoteCommentAsync(int commentId);
    Task<ServiceResult> TogglePinCommentAsync(int commentId);

    Task<PagedResult<PollListItemDto>> GetPollFeedAsync(int page = 1, int pageSize = 15, string? search = null, string? status = "all", string? sortBy = "latest", int? categoryId = null, string? tag = null, string? author = null, bool onlySubscribed = false);
    Task<PagedResult<UserVotedPollDto>> GetVotedPollsAsync(int page = 1, int pageSize = 15);
    Task<PagedResult<UserCommentedPollDto>> GetCommentedPollsAsync(int page = 1, int pageSize = 15);
    Task<UserProfileDto?> GetUserProfileAsync(string username);
    Task<ServiceResult> ToggleCategorySubscriptionAsync(int categoryId);
    Task<List<int>> GetSubscribedCategoryIdsAsync();
    Task<ServiceResult> UpdatePollAsync(int pollId, UpdatePollRequest request);
    Task<ServiceResult> WithdrawToDraftAsync(int pollId);
    Task<ServiceResult> SubmitReportAsync(SubmitReportRequest request);
    Task<List<NotificationDto>> GetNotificationsAsync(int limit = 20);
    Task<int> GetUnreadNotificationCountAsync();
    Task<ServiceResult> MarkNotificationsAsReadAsync(int? notificationId = null);
    Task<UserProfileDto?> GetUserPublicProfileAsync(string username);
    Task<string?> UploadImageAsync(Stream stream, string fileName);
    Task<SystemAnnouncementDto?> GetActiveAnnouncementAsync();
    Task<ServiceResult> UpdateAnnouncementAsync(UpdateAnnouncementRequest request);
}

public class PollApiClient(HttpClient httpClient, ApiErrorState apiErrors) : IPollApiClient
{
    /// <summary>
    /// Records a failed read so the UI can show an error instead of an empty "no results" state,
    /// then returns <paramref name="fallback"/>. Behaviour (what the method returns) is unchanged so
    /// existing callers keep working.
    /// </summary>
    private T ReportReadFailure<T>(string context, Exception ex, T fallback)
    {
        apiErrors.Report(context, ex);
        return fallback;
    }

    /// <summary>
    /// As <see cref="ReportReadFailure{T}"/> but always yields null. Used by the reads whose
    /// "not found" result is null; a 404 is treated as a normal empty result because it is how the
    /// server reports an invisible poll (deleted / pending review), while transport failures and 5xx
    /// are surfaced.
    /// </summary>
    private T? ReportReadFailureUnlessNotFound<T>(string context, Exception ex) where T : class
    {
        if (ex is not HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound })
        {
            apiErrors.Report(context, ex);
        }

        return null;
    }

    public async Task<List<PollListItemDto>> GetPollsAsync(string? search = null, string? status = "all", string? sortBy = "latest", int? categoryId = null, string? tag = null)
    {
        try
        {
            var query = $"?search={Uri.EscapeDataString(search ?? "")}&status={Uri.EscapeDataString(status ?? "all")}&sortBy={Uri.EscapeDataString(sortBy ?? "latest")}";
            if (categoryId.HasValue) query += $"&categoryId={categoryId.Value}";
            if (!string.IsNullOrWhiteSpace(tag)) query += $"&tag={Uri.EscapeDataString(tag)}";
            var res = await httpClient.GetFromJsonAsync<List<PollListItemDto>>($"api/polls{query}");
            return res ?? new List<PollListItemDto>();
        }
        catch (Exception ex)
        {
            return ReportReadFailure("the poll list", ex, new List<PollListItemDto>());
        }
    }

    public async Task<PollDetailDto?> GetPollDetailAsync(int pollId)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<PollDetailDto>($"api/polls/{pollId}");
        }
        catch (Exception ex)
        {
            ReportReadFailureUnlessNotFound<PollDetailDto>("this poll", ex);
            return null;
        }
    }

    public async Task<ServiceResult> CreatePollAsync(CreatePollRequest request)
    {
        try
        {
            var response = await httpClient.PostAsJsonAsync("api/polls", request);
            return await ApiResponse.ReadAsync(response);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail($"Network error: {ex.Message}");
        }
    }

    public async Task<ServiceResult> CastVoteAsync(int pollId, List<int> selectedOptionIds, string? turnstileToken = null)
    {
        try
        {
            var response = await httpClient.PostAsJsonAsync($"api/polls/{pollId}/vote", new VoteRequest
            {
                SelectedOptionIds = selectedOptionIds,
                TurnstileToken = turnstileToken
            });
            return await ApiResponse.ReadAsync(response);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail($"Network error: {ex.Message}");
        }
    }

    public async Task<List<PollListItemDto>> GetUserPollsAsync()
    {
        try
        {
            var res = await httpClient.GetFromJsonAsync<List<PollListItemDto>>("api/polls/my");
            return res ?? new List<PollListItemDto>();
        }
        catch (Exception ex)
        {
            return ReportReadFailure("your polls", ex, new List<PollListItemDto>());
        }
    }

    public async Task<ServiceResult> DeletePollAsync(int pollId)
    {
        try
        {
            var response = await httpClient.DeleteAsync($"api/polls/{pollId}");
            return await ApiResponse.ReadAsync(response);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail($"Network error: {ex.Message}");
        }
    }

    public async Task<ServiceResult> ResubmitPollAsync(int pollId, ResubmitPollRequest request)
    {
        try
        {
            // Must hit the resubmit endpoint: PUT api/polls/{id} is the "safe in-place edit"
            // route (UpdatePollAsync) which never changes Status, so the poll would stay
            // "Revision Required" forever while the UI reported success.
            var response = await httpClient.PutAsJsonAsync($"api/polls/{pollId}/resubmit", request);
            return await ApiResponse.ReadAsync(response);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail($"Network error: {ex.Message}");
        }
    }

    public async Task<List<CategoryDto>> GetCategoriesAsync()
    {
        try
        {
            return await httpClient.GetFromJsonAsync<List<CategoryDto>>("api/categories") ?? new List<CategoryDto>();
        }
        catch (Exception ex)
        {
            return ReportReadFailure("the board list", ex, new List<CategoryDto>());
        }
    }

    public async Task<List<TagDto>> GetPopularTagsAsync()
    {
        try
        {
            return await httpClient.GetFromJsonAsync<List<TagDto>>("api/tags/popular") ?? new List<TagDto>();
        }
        catch (Exception ex)
        {
            return ReportReadFailure("popular tags", ex, new List<TagDto>());
        }
    }

    public async Task<ServiceResult> SubmitCategoryRequestAsync(SubmitCategoryRequest request)
    {
        try
        {
            var response = await httpClient.PostAsJsonAsync("api/category-requests", request);
            return await ApiResponse.ReadAsync(response);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail($"Network error: {ex.Message}");
        }
    }

    public async Task<bool> GetCommentGatingAsync()
    {
        try
        {
            var res = await httpClient.GetFromJsonAsync<CommentGatingDto>("api/comments/gating");
            return res?.RequiresModeration ?? false;
        }
        catch (Exception ex)
        {
            return ReportReadFailure("comment settings", ex, false);
        }
    }

    public async Task<List<PollCommentDto>> GetCommentsAsync(int pollId)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<List<PollCommentDto>>($"api/polls/{pollId}/comments") ?? new List<PollCommentDto>();
        }
        catch (Exception ex)
        {
            // A 404 here means the poll is not visible to the caller (deleted / pending review),
            // which is a legitimate empty result rather than an error worth surfacing.
            ReportReadFailureUnlessNotFound<List<PollCommentDto>>("the comments", ex);
            return new List<PollCommentDto>();
        }
    }

    public async Task<ServiceResult> AddCommentAsync(CreateCommentRequest request)
    {
        try
        {
            var response = await httpClient.PostAsJsonAsync($"api/polls/{request.PollId}/comments", request);
            return await ApiResponse.ReadAsync(response);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail($"Network error: {ex.Message}");
        }
    }

    public async Task<ServiceResult> DeleteCommentAsync(int commentId)
    {
        try
        {
            var response = await httpClient.DeleteAsync($"api/comments/{commentId}");
            return await ApiResponse.ReadAsync(response);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail($"Network error: {ex.Message}");
        }
    }

    public async Task<PagedResult<PollListItemDto>> GetPollFeedAsync(int page = 1, int pageSize = 15, string? search = null, string? status = "all", string? sortBy = "latest", int? categoryId = null, string? tag = null, string? author = null, bool onlySubscribed = false)
    {
        try
        {
            var query = $"?page={page}&pageSize={pageSize}&status={Uri.EscapeDataString(status ?? "all")}&sortBy={Uri.EscapeDataString(sortBy ?? "latest")}";
            if (!string.IsNullOrWhiteSpace(search)) query += $"&search={Uri.EscapeDataString(search)}";
            if (categoryId.HasValue) query += $"&categoryId={categoryId.Value}";
            if (!string.IsNullOrWhiteSpace(tag)) query += $"&tag={Uri.EscapeDataString(tag)}";
            if (!string.IsNullOrWhiteSpace(author)) query += $"&author={Uri.EscapeDataString(author)}";
            if (onlySubscribed) query += "&onlySubscribed=true";

            var res = await httpClient.GetFromJsonAsync<PagedResult<PollListItemDto>>($"api/polls/feed{query}");
            return res ?? new PagedResult<PollListItemDto>();
        }
        catch (Exception ex)
        {
            return ReportReadFailure("the poll feed", ex, new PagedResult<PollListItemDto>());
        }
    }

    public async Task<PagedResult<UserVotedPollDto>> GetVotedPollsAsync(int page = 1, int pageSize = 15)
    {
        try
        {
            var res = await httpClient.GetFromJsonAsync<PagedResult<UserVotedPollDto>>($"api/user/activity/voted?page={page}&pageSize={pageSize}");
            return res ?? new PagedResult<UserVotedPollDto>();
        }
        catch (Exception ex)
        {
            return ReportReadFailure("your voting history", ex, new PagedResult<UserVotedPollDto>());
        }
    }

    public async Task<PagedResult<UserCommentedPollDto>> GetCommentedPollsAsync(int page = 1, int pageSize = 15)
    {
        try
        {
            var res = await httpClient.GetFromJsonAsync<PagedResult<UserCommentedPollDto>>($"api/user/activity/commented?page={page}&pageSize={pageSize}");
            return res ?? new PagedResult<UserCommentedPollDto>();
        }
        catch (Exception ex)
        {
            return ReportReadFailure("your comment history", ex, new PagedResult<UserCommentedPollDto>());
        }
    }

    public async Task<UserProfileDto?> GetUserProfileAsync(string username)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<UserProfileDto>($"api/user/profile/{Uri.EscapeDataString(username)}");
        }
        catch (Exception ex)
        {
            ReportReadFailureUnlessNotFound<UserProfileDto>("your profile", ex);
            return null;
        }
    }

    public async Task<ServiceResult> ToggleCategorySubscriptionAsync(int categoryId)
    {
        try
        {
            var res = await httpClient.PostAsync($"api/categories/{categoryId}/subscribe", null);
            return await ApiResponse.ReadAsync(res);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail($"Network error: {ex.Message}");
        }
    }

    public async Task<List<int>> GetSubscribedCategoryIdsAsync()
    {
        try
        {
            return await httpClient.GetFromJsonAsync<List<int>>("api/categories/subscriptions") ?? new List<int>();
        }
        catch (Exception ex)
        {
            return ReportReadFailure("your followed boards", ex, new List<int>());
        }
    }

    public async Task<ServiceResult> UpdatePollAsync(int pollId, UpdatePollRequest request)
    {
        try
        {
            var res = await httpClient.PutAsJsonAsync($"api/polls/{pollId}", request);
            return await ApiResponse.ReadAsync(res);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail($"Network error: {ex.Message}");
        }
    }

    public async Task<ServiceResult> WithdrawToDraftAsync(int pollId)
    {
        try
        {
            var res = await httpClient.PostAsync($"api/polls/{pollId}/withdraw", null);
            return await ApiResponse.ReadAsync(res);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail($"Network error: {ex.Message}");
        }
    }

    public async Task<ServiceResult> SubmitReportAsync(SubmitReportRequest request)
    {
        try
        {
            var res = await httpClient.PostAsJsonAsync("api/reports", request);
            return await ApiResponse.ReadAsync(res);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail($"Network error: {ex.Message}");
        }
    }

    public async Task<List<NotificationDto>> GetNotificationsAsync(int limit = 20)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<List<NotificationDto>>($"api/notifications?limit={limit}") ?? new List<NotificationDto>();
        }
        catch (Exception ex)
        {
            return ReportReadFailure("your notifications", ex, new List<NotificationDto>());
        }
    }

    public async Task<int> GetUnreadNotificationCountAsync()
    {
        try
        {
            return await httpClient.GetFromJsonAsync<int>("api/notifications/unread-count");
        }
        catch (Exception ex)
        {
            return ReportReadFailure("your unread count", ex, 0);
        }
    }

    public async Task<ServiceResult> MarkNotificationsAsReadAsync(int? notificationId = null)
    {
        try
        {
            var url = notificationId.HasValue ? $"api/notifications/{notificationId.Value}/read" : "api/notifications/read-all";
            var res = await httpClient.PostAsync(url, null);
            return await ApiResponse.ReadAsync(res);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail($"Network error: {ex.Message}");
        }
    }

    public async Task<UserProfileDto?> GetUserPublicProfileAsync(string username)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<UserProfileDto>($"api/user/profile/{Uri.EscapeDataString(username)}");
        }
        catch
        {
            return null;
        }
    }

    public async Task<ServiceResult> UpvoteCommentAsync(int commentId)
    {
        try
        {
            var res = await httpClient.PostAsync($"api/comments/{commentId}/upvote", null);
            return await ApiResponse.ReadAsync(res);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail($"Network error: {ex.Message}");
        }
    }

    public async Task<ServiceResult> TogglePinCommentAsync(int commentId)
    {
        try
        {
            var res = await httpClient.PostAsync($"api/comments/{commentId}/pin", null);
            return await ApiResponse.ReadAsync(res);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail($"Network error: {ex.Message}");
        }
    }

    public async Task<string?> UploadImageAsync(Stream stream, string fileName)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            var streamContent = new StreamContent(stream);
            content.Add(streamContent, "file", fileName);
            var response = await httpClient.PostAsync("api/upload/image", content);
            if (response.IsSuccessStatusCode)
            {
                var res = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
                if (res.TryGetProperty("url", out var urlProp))
                {
                    return urlProp.GetString();
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<SystemAnnouncementDto?> GetActiveAnnouncementAsync()
    {
        try
        {
            return await httpClient.GetFromJsonAsync<SystemAnnouncementDto?>("api/announcement");
        }
        catch
        {
            return null;
        }
    }

    public async Task<ServiceResult> UpdateAnnouncementAsync(UpdateAnnouncementRequest request)
    {
        try
        {
            var res = await httpClient.PutAsJsonAsync("api/admin/announcement", request);
            return await ApiResponse.ReadAsync(res);
        }
        catch (Exception ex)
        {
            return ServiceResult.Fail($"Network error: {ex.Message}");
        }
    }
}
