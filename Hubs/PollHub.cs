using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using PdnodeVote.Services;

namespace PdnodeVote.Hubs;

[Authorize]
public class PollHub : Hub
{
    /// <summary>
    /// Subscribes the caller to their own notification stream.
    /// </summary>
    /// <remarks>
    /// The group is derived from the authenticated identity, never from client input: previously
    /// any anonymous connection could join "user_{victimId}" and receive another user's private
    /// notifications. <paramref name="userId"/> is accepted only so older clients keep working,
    /// and is rejected when it does not match the caller.
    /// </remarks>
    public async Task JoinUserGroup(string? userId = null)
    {
        var selfId = Context.UserIdentifier;
        if (string.IsNullOrEmpty(selfId)) return;

        if (!string.IsNullOrEmpty(userId) && !string.Equals(userId, selfId, StringComparison.Ordinal))
        {
            throw new HubException("You can only subscribe to your own notifications.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{selfId}");
    }
}

public class PollNotificationBroadcaster(IPollEventNotifier notifier, IHubContext<PollHub> hubContext) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        notifier.PollUpdated += OnPollUpdated;
        notifier.CommentAdded += OnCommentAdded;
        notifier.CommentDeleted += OnCommentDeleted;
        notifier.UserNotificationReceived += OnUserNotification;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        notifier.PollUpdated -= OnPollUpdated;
        notifier.CommentAdded -= OnCommentAdded;
        notifier.CommentDeleted -= OnCommentDeleted;
        notifier.UserNotificationReceived -= OnUserNotification;
        return Task.CompletedTask;
    }

    private void OnPollUpdated(int pollId)
    {
        _ = hubContext.Clients.All.SendAsync("PollUpdated", pollId);
    }

    private void OnCommentAdded(int pollId, PdnodeVote.Client.Models.PollCommentDto comment)
    {
        _ = hubContext.Clients.All.SendAsync("CommentAdded", pollId, comment);
    }

    private void OnCommentDeleted(int pollId, int commentId)
    {
        _ = hubContext.Clients.All.SendAsync("CommentDeleted", pollId, commentId);
    }

    private void OnUserNotification(string userId, int unreadCount, PdnodeVote.Client.Models.NotificationDto notification)
    {
        // Send to the group only. The previous code also called Clients.User(userId), which
        // delivered every notification twice to any user who had joined their own group.
        // The event name must match the client handler in NotificationBell.razor ("NotificationReceived").
        _ = hubContext.Clients.Group($"user_{userId}").SendAsync("NotificationReceived", unreadCount, notification);
    }
}
