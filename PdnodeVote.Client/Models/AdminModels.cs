namespace PdnodeVote.Client.Models;

public class AdminUserDto
{
    public string Id { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool IsBanned { get; set; }
    public DateTime? BannedUntil { get; set; }
    public string? BanReason { get; set; }
    public bool IsPermanentlyBanned => IsBanned && !BannedUntil.HasValue;
    public bool IsCurrentlyBanned => IsBanned && (!BannedUntil.HasValue || BannedUntil.Value > DateTime.UtcNow);
    public List<string> Roles { get; set; } = new();
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

public class BanUserRequest
{
    public string UserId { get; set; } = string.Empty;
    public string BanType { get; set; } = "Temporary"; // "Temporary" | "Permanent"
    public int DurationDays { get; set; } = 7;
    public string Reason { get; set; } = string.Empty;
}

public class ResetPasswordRequest
{
    public string UserId { get; set; } = string.Empty;
}

public class UpdateRoleRequest
{
    public string UserId { get; set; } = string.Empty;
    public string NewRole { get; set; } = string.Empty;
    public bool Enable { get; set; } = true;
}

public class BulkEmailRequest
{
    public List<string> UserIds { get; set; } = new();
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}
