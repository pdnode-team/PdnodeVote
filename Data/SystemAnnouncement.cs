using System.ComponentModel.DataAnnotations;

namespace PdnodeVote.Data;

public class SystemAnnouncement
{
    [Key]
    public int Id { get; set; } = 1;

    [MaxLength(500)]
    public string Message { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? TargetUrl { get; set; }

    public bool IsActive { get; set; } = false;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
