namespace PdnodeVote.Data;

public class CommentLike
{
    public int CommentId { get; set; }
    public PollComment? Comment { get; set; }

    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
