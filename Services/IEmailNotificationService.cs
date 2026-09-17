using PdnodeVote.Data;

namespace PdnodeVote.Services;

public interface IEmailNotificationService
{
    Task SendEmailAsync(string toEmail, string subject, string htmlBody);
    Task SendBulkEmailAsync(IEnumerable<string> toEmails, string subject, string htmlBody);
    Task NotifyPollApprovedAsync(Poll poll, string authorEmail);
    Task NotifyPollReturnedForRevisionAsync(Poll poll, string authorEmail, string reason);
    Task NotifyPollRemovedAsync(Poll poll, string authorEmail, string reason);
    Task NotifyPollArchivedAsync(Poll poll, string authorEmail, string? reason);
    Task NotifyPollPinnedAsync(Poll poll, string authorEmail, bool isPinned);
    Task NotifyAccountBannedAsync(string email, bool isPermanent, DateTime? bannedUntil, string reason);
    Task NotifyAccountUnbannedAsync(string email);
    Task NotifyPasswordResetAsync(string email, string newPassword);
}
