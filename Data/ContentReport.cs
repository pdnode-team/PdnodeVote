using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PdnodeVote.Data;

public enum ContentReportStatus
{
    Pending = 0,
    Dismissed = 1,
    Resolved = 2
}

public class ContentReport
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string ReporterId { get; set; } = string.Empty;

    [ForeignKey(nameof(ReporterId))]
    public ApplicationUser? Reporter { get; set; }

    public int? PollId { get; set; }

    [ForeignKey(nameof(PollId))]
    public Poll? Poll { get; set; }

    public int? CommentId { get; set; }

    [ForeignKey(nameof(CommentId))]
    public PollComment? Comment { get; set; }

    [Required]
    [MaxLength(100)]
    public string Reason { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Details { get; set; }

    public ContentReportStatus Status { get; set; } = ContentReportStatus.Pending;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? ResolvedById { get; set; }

    [ForeignKey(nameof(ResolvedById))]
    public ApplicationUser? ResolvedBy { get; set; }

    public DateTime? ResolvedAt { get; set; }

    [MaxLength(500)]
    public string? ResolutionNotes { get; set; }
}
