namespace PdnodeVote.Client.Models;

public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 15;
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
    public bool HasNextPage => Page < TotalPages;
}

public class NotificationDto
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int Type { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? TargetUrl { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class UserProfileDto
{
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? RoleBadge { get; set; } // "Admin" | "SuperModerator" | "Moderator" | null
    public int UserLevel { get; set; } = 1;
    public DateTime JoinedAt { get; set; }
    public int CreatedPollsCount { get; set; }
    public int TotalVotesReceived { get; set; }
    public int CommentsCount { get; set; }
    public List<PollListItemDto> RecentPolls { get; set; } = new();
    public List<UserCommentSummaryDto> RecentComments { get; set; } = new();
}

public class UserCommentSummaryDto
{
    public int Id { get; set; }
    public int PollId { get; set; }
    public string PollTitle { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class UserVotedPollDto
{
    public int PollId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string CreatorName { get; set; } = string.Empty;
    public DateTime VotedAt { get; set; }
    public List<string> SelectedOptionTexts { get; set; } = new();
    public int ParticipantCount { get; set; }
    public PollStatus Status { get; set; }
}

public class UserCommentedPollDto
{
    public int PollId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string CreatorName { get; set; } = string.Empty;
    public DateTime LastCommentedAt { get; set; }
    public string LastCommentSnippet { get; set; } = string.Empty;
    public int TotalUserCommentsInPoll { get; set; }
}

public class ContentReportDto
{
    public int Id { get; set; }
    public string ReporterId { get; set; } = string.Empty;
    public string ReporterName { get; set; } = string.Empty;
    public int? PollId { get; set; }
    public string? PollTitle { get; set; }
    public int? CommentId { get; set; }
    public string? CommentContent { get; set; }
    public string? AuthorName { get; set; }
    public string? AuthorId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? Details { get; set; }
    public int Status { get; set; } // 0=Pending, 1=Dismissed, 2=Resolved
    public DateTime CreatedAt { get; set; }
    public string? ResolvedByName { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolutionNotes { get; set; }
}

public class SubmitReportRequest
{
    public int? PollId { get; set; }
    public int? CommentId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? Details { get; set; }
}

public class ResolveReportRequest
{
    public bool Dismiss { get; set; }
    public bool RemoveContent { get; set; }
    public bool BanAuthor { get; set; }
    public string? Notes { get; set; }
}

public class UpdatePollRequest
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public int? CategoryId { get; set; }
    public List<string>? Tags { get; set; }
    public List<string>? Options { get; set; }
}
