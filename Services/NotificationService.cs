using Microsoft.EntityFrameworkCore;
using PdnodeVote.Client.Models;
using PdnodeVote.Data;

namespace PdnodeVote.Services;

public class NotificationService : INotificationService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly IPollEventNotifier _notifier;

    public NotificationService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IPollEventNotifier notifier)
    {
        _dbContextFactory = dbContextFactory;
        _notifier = notifier;
    }

    public async Task<List<NotificationDto>> GetNotificationsAsync(string userId, int limit = 20)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var list = await context.Notifications
            .AsNoTracking()
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .Take(limit)
            .Select(n => new NotificationDto
            {
                Id = n.Id,
                UserId = n.UserId,
                Type = (int)n.Type,
                Title = n.Title,
                Message = n.Message,
                TargetUrl = n.TargetUrl,
                IsRead = n.IsRead,
                CreatedAt = n.CreatedAt
            })
            .ToListAsync();

        return list;
    }

    public async Task<int> GetUnreadCountAsync(string userId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.Notifications
            .CountAsync(n => n.UserId == userId && !n.IsRead);
    }

    public async Task<ServiceResult> MarkAsReadAsync(string userId, int? notificationId = null)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        if (notificationId.HasValue)
        {
            var item = await context.Notifications.FirstOrDefaultAsync(n => n.Id == notificationId.Value && n.UserId == userId);
            if (item != null)
            {
                item.IsRead = true;
                await context.SaveChangesAsync();
            }
        }
        else
        {
            await context.Notifications
                .Where(n => n.UserId == userId && !n.IsRead)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));
        }

        var unread = await context.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead);
        _notifier.NotifyUserNotification(userId, unread, new NotificationDto { IsRead = true });

        return ServiceResult.Ok();
    }

    public async Task CreateNotificationAsync(string userId, NotificationType type, string title, string message, string? targetUrl = null)
    {
        if (string.IsNullOrEmpty(userId)) return;

        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var notification = new Notification
        {
            UserId = userId,
            Type = type,
            Title = title,
            Message = message,
            TargetUrl = targetUrl,
            IsRead = false,
            CreatedAt = DateTime.UtcNow
        };

        context.Notifications.Add(notification);
        await context.SaveChangesAsync();

        var unread = await context.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead);
        var dto = new NotificationDto
        {
            Id = notification.Id,
            UserId = notification.UserId,
            Type = (int)notification.Type,
            Title = notification.Title,
            Message = notification.Message,
            TargetUrl = notification.TargetUrl,
            IsRead = false,
            CreatedAt = notification.CreatedAt
        };

        _notifier.NotifyUserNotification(userId, unread, dto);
    }
}
