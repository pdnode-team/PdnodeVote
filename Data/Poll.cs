namespace PdnodeVote.Data;

public class Poll
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    // Author ID
    public string CreatorId { get; set; } = string.Empty;
    public ApplicationUser? Creator { get; set; }

    // Status: Approved, PendingReview, ReturnedForRevision, Removed, Archived
    public PollStatus Status { get; set; } = PollStatus.Approved;

    // Reason if returned for revision or removed by moderators
    public string? ModerationReason { get; set; }

    // Pin poll to top of lists
    public bool IsPinned { get; set; } = false;

    // Login requirement
    public bool RequireLogin { get; set; } = false;

    // Multiple choice support
    public bool IsMultipleChoice { get; set; } = false;

    // Max choices for multiple choice
    public int MaxChoices { get; set; } = 1;

    // Result visibility: AlwaysPublic or AfterVoting
    public ResultVisibility ResultVisibility { get; set; } = ResultVisibility.AlwaysPublic;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Expiration timestamp
    public DateTime? ExpiresAt { get; set; }

    // Category (Section)
    public int? CategoryId { get; set; }
    public Category? Category { get; set; }

    public List<PollOption> Options { get; set; } = new();

    public List<VoteRecord> Votes { get; set; } = new();

    public List<PollTag> PollTags { get; set; } = new();

    public List<PollComment> Comments { get; set; } = new();

    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value <= DateTime.UtcNow;
}
