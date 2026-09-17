using PdnodeVote.Client.Models;

namespace PdnodeVote.Services;

public interface IPollEventNotifier
{
    event Action<int>? PollUpdated;
    void NotifyPollUpdated(int pollId);

    event Action<int, PollCommentDto>? CommentAdded;
    void NotifyCommentAdded(int pollId, PollCommentDto comment);

    event Action<int, int>? CommentDeleted;
    void NotifyCommentDeleted(int pollId, int commentId);

    event Action<string, int, NotificationDto>? UserNotificationReceived;
    void NotifyUserNotification(string userId, int unreadCount, NotificationDto notification);
}

public class PollEventNotifier : IPollEventNotifier
{
    public event Action<int>? PollUpdated;
    public event Action<int, PollCommentDto>? CommentAdded;
    public event Action<int, int>? CommentDeleted;
    public event Action<string, int, NotificationDto>? UserNotificationReceived;

    public void NotifyPollUpdated(int pollId)
    {
        try
        {
            PollUpdated?.Invoke(pollId);
        }
        catch
        {
            // Ignore subscriber exceptions
        }
    }

    public void NotifyCommentAdded(int pollId, PollCommentDto comment)
    {
        try
        {
            CommentAdded?.Invoke(pollId, comment);
        }
        catch
        {
            // Ignore subscriber exceptions
        }
    }

    public void NotifyCommentDeleted(int pollId, int commentId)
    {
        try
        {
            CommentDeleted?.Invoke(pollId, commentId);
        }
        catch
        {
            // Ignore subscriber exceptions
        }
    }

    public void NotifyUserNotification(string userId, int unreadCount, NotificationDto notification)
    {
        try
        {
            UserNotificationReceived?.Invoke(userId, unreadCount, notification);
        }
        catch
        {
            // Ignore subscriber exceptions
        }
    }
}
