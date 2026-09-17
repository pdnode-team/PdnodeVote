namespace PdnodeVote.Data;

public class CategoryRequestReview
{
    public int Id { get; set; }

    public int RequestId { get; set; }
    public CategoryRequest? Request { get; set; }

    public string ReviewerId { get; set; } = string.Empty;
    public ApplicationUser? Reviewer { get; set; }

    public bool IsApproved { get; set; }

    public string? Comment { get; set; }

    public DateTime ReviewedAt { get; set; } = DateTime.UtcNow;
}
