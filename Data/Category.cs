namespace PdnodeVote.Data;

public enum CategoryPostPermission
{
    Anyone = 0,
    ModeratorOnly = 1,
    AdminOnly = 2
}

public class Category
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Slug { get; set; } = string.Empty;

    // Hierarchy: ParentId is null for Root Categories. Max depth is 3 sub-levels (depth 0..3)
    public int? ParentId { get; set; }
    public Category? Parent { get; set; }
    public List<Category> Children { get; set; } = new();

    public int Depth { get; set; } = 0;

    // Who can create polls in this category
    public CategoryPostPermission PostPermission { get; set; } = CategoryPostPermission.Anyone;

    // System-protected categories (e.g. Announcement, Moderator zone) cannot be deleted by users
    public bool IsSystem { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<CategoryModerator> Moderators { get; set; } = new();
    public List<Poll> Polls { get; set; } = new();
}
