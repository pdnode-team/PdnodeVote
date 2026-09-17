namespace PdnodeVote.Services;

public class AdminUserDto
{
    public string Id { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public List<string> Roles { get; set; } = new();
    public bool IsBanned { get; set; }
    public DateTime? BannedUntil { get; set; }
    public string? BanReason { get; set; }
    public bool IsCurrentlyBanned => IsBanned && (BannedUntil == null || BannedUntil > DateTime.UtcNow);
    public bool IsPermanentlyBanned => IsBanned && BannedUntil == null;
    public int ApprovedPollsCount { get; set; }
    public int TotalPollsCount { get; set; }
}

public class AdminDashboardStatsDto
{
    public int TotalUsers { get; set; }
    public int TotalPolls { get; set; }
    public int PendingReviewCount { get; set; }
    public int BannedUsersCount { get; set; }
}

public interface IAdminService
{
    Task<AdminDashboardStatsDto> GetStatsAsync();
    Task<List<AdminUserDto>> GetUsersAsync(string? search = null, string? roleFilter = null, bool? bannedOnly = null);
    Task<PdnodeVote.Client.Models.PagedResult<AdminUserDto>> GetUsersPagedAsync(int page = 1, int pageSize = 15, string? search = null, string? roleFilter = null, bool? bannedOnly = null);
    Task<(int SuccessCount, string Message)> BanUsersAsync(IEnumerable<string> userIds, bool isPermanent, int durationDays, string reason, string? currentAdminUserId = null);
    Task<(int SuccessCount, string Message)> UnbanUsersAsync(IEnumerable<string> userIds);
    Task<(bool Success, string Message, string? NewPassword)> ResetUserPasswordAsync(string userId);
    Task<(int SentCount, string Message)> SendBulkEmailToUsersAsync(IEnumerable<string> userIds, string subject, string message);
    Task<(bool Success, string Message)> UpdateUserRoleAsync(string userId, string targetRole, bool enable);
}
