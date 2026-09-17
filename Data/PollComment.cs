namespace PdnodeVote.Data;

public class PollComment
{
    public int Id { get; set; }

    public int PollId { get; set; }
    public Poll? Poll { get; set; }

    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

    // Supports 2 levels of replies (null for top-level comments, points to top-level comment id for replies)
    public int? ParentCommentId { get; set; }
    public PollComment? ParentComment { get; set; }
    public List<PollComment> Replies { get; set; } = new();

    // Plain text only, max 1000 characters
    public string Content { get; set; } = string.Empty;

    // Follows the same status as Poll: Approved, PendingReview, Removed
    public PollStatus Status { get; set; } = PollStatus.Approved;

    public string? ModerationReason { get; set; }

    public int Upvotes { get; set; } = 0;
    public bool IsPinned { get; set; } = false;
    public List<CommentLike> Likes { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
