using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PdnodeVote.Data;

public enum NotificationType
{
    PollApproved = 1,
    PollRejected = 2,
    PollReturned = 3,
    CommentReplied = 4,
    CommentModerated = 5,
    CategoryRequestApproved = 6,
    CategoryRequestRejected = 7,
    SystemNotice = 8
}

public class Notification
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;

    [ForeignKey(nameof(UserId))]
    public ApplicationUser? User { get; set; }

    public NotificationType Type { get; set; } = NotificationType.SystemNotice;

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(1000)]
    public string Message { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? TargetUrl { get; set; }

    public bool IsRead { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
