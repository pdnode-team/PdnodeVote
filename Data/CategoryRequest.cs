namespace PdnodeVote.Data;

public enum CategoryRequestStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2
}

public class CategoryRequest
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public int? ParentId { get; set; }
    public Category? Parent { get; set; }

    public string ApplicantId { get; set; } = string.Empty;
    public ApplicationUser? Applicant { get; set; }

    public CategoryRequestStatus Status { get; set; } = CategoryRequestStatus.Pending;

    public string? RejectionReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<CategoryRequestReview> Reviews { get; set; } = new();
}
