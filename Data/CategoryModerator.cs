namespace PdnodeVote.Data;

public class CategoryModerator
{
    public int Id { get; set; }

    public int CategoryId { get; set; }
    public Category? Category { get; set; }

    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
}
