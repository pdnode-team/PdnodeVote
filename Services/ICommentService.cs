using PdnodeVote.Client.Models;

namespace PdnodeVote.Services;

public interface ICommentService
{
    Task<List<PollCommentDto>> GetPollCommentsAsync(int pollId, string? currentUserId);
    Task<bool> RequiresCommentModerationAsync(string userId);
    Task<ServiceResult> AddCommentAsync(int pollId, string userId, string content, int? parentCommentId = null);
    Task<ServiceResult> DeleteCommentAsync(int commentId, string currentUserId);
    Task<List<PendingCommentDto>> GetPendingCommentsAsync();
    Task<ServiceResult> ApproveCommentAsync(int commentId, string reviewerId);
    Task<ServiceResult> RemoveCommentAsync(int commentId, string reviewerId, string reason);
    Task<ServiceResult> UpvoteCommentAsync(int commentId, string userId);
    Task<ServiceResult> TogglePinCommentAsync(int commentId, string userId);
}
