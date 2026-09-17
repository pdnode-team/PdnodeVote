using PdnodeVote.Client.Models;
using PdnodeVote.Data;

namespace PdnodeVote.Services;

public interface INotificationService
{
    Task<List<NotificationDto>> GetNotificationsAsync(string userId, int limit = 20);
    Task<int> GetUnreadCountAsync(string userId);
    Task<ServiceResult> MarkAsReadAsync(string userId, int? notificationId = null);
    Task CreateNotificationAsync(string userId, NotificationType type, string title, string message, string? targetUrl = null);
}
