namespace PdnodeVote.Client.Models;

public enum PollStatus
{
    Approved = 0,
    PendingReview = 1,
    ReturnedForRevision = 2,
    Removed = 3,
    Archived = 4
}

public enum ResultVisibility
{
    AlwaysPublic = 0,
    AfterVoting = 1
}

public class PollDetailDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string CreatorId { get; set; } = string.Empty;
    public string CreatorName { get; set; } = string.Empty;
    public string CreatorEmail { get; set; } = string.Empty;
    public bool RequireLogin { get; set; }
    public bool IsMultipleChoice { get; set; }
    public int MaxChoices { get; set; }
    public ResultVisibility ResultVisibility { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value <= DateTime.UtcNow;

    public PollStatus Status { get; set; } = PollStatus.Approved;
    public string? ModerationReason { get; set; }
    public bool IsPinned { get; set; }

    public int TotalParticipants { get; set; }
    public int TotalVotesCount { get; set; }

    /// <summary>
    /// False when the poll is configured <see cref="ResultVisibility.AfterVoting"/> and the caller
    /// has not voted yet (and is neither the author nor a moderator, and the poll has not expired).
    /// The vote counts and percentages in <see cref="Options"/> are withheld (zeroed) in that case,
    /// so callers must not present them as real results.
    /// </summary>
    public bool ResultsVisible { get; set; } = true;

    public List<PollOptionDto> Options { get; set; } = new();

    public bool HasCurrentUserVoted { get; set; }
    public List<int> UserVotedOptionIds { get; set; } = new();

    public bool IsAuthor { get; set; }
    public bool CanModerate { get; set; }
    public bool IsAdmin { get; set; }
    public bool IsSuperModerator { get; set; }

    public int? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? CategorySlug { get; set; }
    public List<string> Tags { get; set; } = new();
    public int CommentCount { get; set; }
    public int CreatorLevel { get; set; } = 1;
}

public class PollOptionDto
{
    public int Id { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public int Order { get; set; }
    public int VoteCount { get; set; }
    public double Percentage { get; set; }
}

public class PollListItemDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string CreatorId { get; set; } = string.Empty;
    public string CreatorName { get; set; } = string.Empty;
    public bool RequireLogin { get; set; }
    public bool IsMultipleChoice { get; set; }
    public ResultVisibility ResultVisibility { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value <= DateTime.UtcNow;
    public int ParticipantCount { get; set; }
    public int OptionCount { get; set; }

    public PollStatus Status { get; set; } = PollStatus.Approved;
    public string? ModerationReason { get; set; }
    public bool IsPinned { get; set; }

    public int? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public List<string> Tags { get; set; } = new();
    public int CommentCount { get; set; }
    public bool HasImages { get; set; }
    public int CreatorLevel { get; set; } = 1;
}

public class PendingReviewPollDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string CreatorId { get; set; } = string.Empty;
    public string CreatorName { get; set; } = string.Empty;
    public string CreatorEmail { get; set; } = string.Empty;
    public DateTime CreatorCreatedAt { get; set; }
    public int CreatorApprovedPollsCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<string> Options { get; set; } = new();
    public string GatingReason { get; set; } = string.Empty;
    public int? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public List<string> Tags { get; set; } = new();
}

public class PollOptionInputDto
{
    public string Text { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
}

public class CreatePollRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? CategoryId { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<string> Options { get; set; } = new();
    public List<PollOptionInputDto> OptionItems { get; set; } = new();
    public bool RequireLogin { get; set; }
    public bool IsMultipleChoice { get; set; }
    public int MaxChoices { get; set; } = 1;
    public ResultVisibility ResultVisibility { get; set; } = ResultVisibility.AlwaysPublic;
    public DateTime? ExpiresAt { get; set; }
}

public class VoteRequest
{
    public List<int> SelectedOptionIds { get; set; } = new();
    public string? TurnstileToken { get; set; }
}

public class ResubmitPollRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? CategoryId { get; set; }
    public List<string> Tags { get; set; } = new();
    public List<string> Options { get; set; } = new();
}

public class ModerationActionRequest
{
    public string? Reason { get; set; }
}

public class CategoryDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Slug { get; set; } = string.Empty;
    public int? ParentId { get; set; }
    public int Depth { get; set; }
    public int PostPermission { get; set; } // 0=Anyone, 1=ModeratorOnly, 2=AdminOnly
    public bool IsSystem { get; set; }
    public int PollCount { get; set; }
    public List<CategoryDto> Children { get; set; } = new();
}

public class CategoryRequestDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int? ParentId { get; set; }
    public string? ParentName { get; set; }
    public string ApplicantId { get; set; } = string.Empty;
    public string ApplicantName { get; set; } = string.Empty;
    public int Status { get; set; } // 0=Pending, 1=Approved, 2=Rejected
    public string? RejectionReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public int ApprovalsCount { get; set; }
    public int RejectionsCount { get; set; }
    public bool HasReviewedByCurrentUser { get; set; }
}

public class SubmitCategoryRequest
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int? ParentId { get; set; }
}

public class ReviewCategoryRequest
{
    public bool Approve { get; set; }
    public string? Comment { get; set; }
}

public class PollCommentDto
{
    public int Id { get; set; }
    public int PollId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? UserRoleBadge { get; set; } // "Admin" | "SuperModerator" | "Moderator" | null
    public int UserLevel { get; set; } = 1;
    public int? ParentCommentId { get; set; }
    public string Content { get; set; } = string.Empty;
    public int Status { get; set; } // 0=Approved, 1=PendingReview, 3=Removed
    public DateTime CreatedAt { get; set; }
    public bool CanDelete { get; set; }
    public int Upvotes { get; set; }
    public bool HasUpvoted { get; set; }
    public bool IsPinned { get; set; }
    public List<PollCommentDto> Replies { get; set; } = new();
}

public class SystemAnnouncementDto
{
    public int Id { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? TargetUrl { get; set; }
    public bool IsActive { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class UpdateAnnouncementRequest
{
    public string Message { get; set; } = string.Empty;
    public string? TargetUrl { get; set; }
    public bool IsActive { get; set; }
}

public class CommentGatingDto
{
    public bool RequiresModeration { get; set; }
}

public class CreateCommentRequest
{
    public int PollId { get; set; }
    public int? ParentCommentId { get; set; }
    public string Content { get; set; } = string.Empty;
}

public class PendingCommentDto
{
    public int Id { get; set; }
    public int PollId { get; set; }
    public string PollTitle { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string GatingReason { get; set; } = string.Empty;
}

public class TagDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int UsageCount { get; set; }
}

public class ServiceResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int PollId { get; set; }

    public static ServiceResult Ok(string message = "", int pollId = 0) => new() { Success = true, Message = message, PollId = pollId };
    public static ServiceResult Fail(string message) => new() { Success = false, Message = message };
}
