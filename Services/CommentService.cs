using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PdnodeVote.Client.Models;
using PdnodeVote.Data;
using PollStatus = PdnodeVote.Data.PollStatus;

namespace PdnodeVote.Services;

public class CommentService : ICommentService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IPollEventNotifier? _pollEventNotifier;
    private readonly INotificationService? _notificationService;

    public CommentService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        UserManager<ApplicationUser> userManager,
        IPollEventNotifier? pollEventNotifier = null,
        INotificationService? notificationService = null)
    {
        _dbContextFactory = dbContextFactory;
        _userManager = userManager;
        _pollEventNotifier = pollEventNotifier;
        _notificationService = notificationService;
    }

    public async Task<List<PollCommentDto>> GetPollCommentsAsync(int pollId, string? currentUserId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        bool canModerate = false;
        if (!string.IsNullOrEmpty(currentUserId))
        {
            var user = await _userManager.FindByIdAsync(currentUserId);
            if (user != null)
            {
                canModerate = await _userManager.IsInRoleAsync(user, "Admin") ||
                              await _userManager.IsInRoleAsync(user, "SuperModerator") ||
                              await _userManager.IsInRoleAsync(user, "Moderator") ||
                              user.IsRootAdmin;
            }
        }

        var comments = await context.PollComments
            .Include(c => c.User)
            .Where(c => c.PollId == pollId)
            .OrderBy(c => c.CreatedAt)
            .AsNoTracking()
            .ToListAsync();

        // Mirror the poll's own visibility. GET /api/polls/{id}/comments is anonymous, so without this
        // the comment text and author names of Removed / PendingReview / ReturnedForRevision polls
        // stayed readable even though GetPollDetailAsync refuses to serve the poll itself.
        if (comments.Count > 0)
        {
            var poll = await context.Polls
                .AsNoTracking()
                .Select(p => new { p.Id, p.Status, p.CreatorId })
                .FirstOrDefaultAsync(p => p.Id == pollId);

            if (poll == null) return new List<PollCommentDto>();

            bool canSeePoll = poll.Status == PollStatus.Approved
                              || canModerate
                              || (!string.IsNullOrEmpty(currentUserId) && poll.CreatorId == currentUserId);

            if (!canSeePoll) return new List<PollCommentDto>();
        }

        var visibleComments = comments.Where(c =>
            c.Status == PollStatus.Approved ||
            canModerate ||
            (!string.IsNullOrEmpty(currentUserId) && c.UserId == currentUserId) ||
            (c.Status == PollStatus.Removed && comments.Any(r => r.ParentCommentId == c.Id))
        ).ToList();

        // Get user role badges and user levels efficiently
        var userIds = visibleComments.Select(c => c.UserId).Distinct().ToList();
        var roleBadgeMap = new Dictionary<string, string>();
        var userLevelMap = new Dictionary<string, int>();

        var userLikedCommentIds = new HashSet<int>();
        if (!string.IsNullOrEmpty(currentUserId))
        {
            var liked = await context.CommentLikes
                .Where(cl => cl.UserId == currentUserId && visibleComments.Select(c => c.Id).Contains(cl.CommentId))
                .Select(cl => cl.CommentId)
                .ToListAsync();
            userLikedCommentIds = liked.ToHashSet();
        }

        foreach (var uid in userIds)
        {
            var u = await _userManager.FindByIdAsync(uid);
            if (u != null)
            {
                if (await _userManager.IsInRoleAsync(u, "Admin") || u.IsRootAdmin)
                {
                    roleBadgeMap[uid] = "Admin";
                }
                else if (await _userManager.IsInRoleAsync(u, "SuperModerator"))
                {
                    roleBadgeMap[uid] = "SuperModerator";
                }
                else if (await _userManager.IsInRoleAsync(u, "Moderator"))
                {
                    roleBadgeMap[uid] = "Moderator";
                }

                var voteCount = await context.VoteRecords.CountAsync(v => v.UserId == uid);
                var approvedPolls = await context.Polls.CountAsync(p => p.CreatorId == uid && p.Status == PollStatus.Approved);
                var votesReceived = await context.VoteRecords.CountAsync(v => v.Poll != null && v.Poll.CreatorId == uid);
                userLevelMap[uid] = UserLevelHelper.CalculateLevel(u.CreatedAt, voteCount, approvedPolls, votesReceived);
            }
        }

        var dtos = visibleComments.Select(c => new PollCommentDto
        {
            Id = c.Id,
            PollId = c.PollId,
            UserId = c.UserId,
            UserName = PollService.FormatDisplayName(c.User?.UserName, c.User?.Email),
            UserRoleBadge = roleBadgeMap.GetValueOrDefault(c.UserId),
            UserLevel = userLevelMap.GetValueOrDefault(c.UserId, 1),
            ParentCommentId = c.ParentCommentId,
            Content = c.Status == PollStatus.Removed ? "This comment has been removed" : c.Content,
            Status = (int)c.Status,
            CreatedAt = c.CreatedAt,
            CanDelete = c.Status != PollStatus.Removed && (!string.IsNullOrEmpty(currentUserId) && (c.UserId == currentUserId || canModerate)),
            Upvotes = c.Upvotes,
            HasUpvoted = userLikedCommentIds.Contains(c.Id),
            IsPinned = c.IsPinned,
            Replies = new()
        }).ToList();

        // Build 2-level hierarchy: top-level comments and replies
        var topLevel = dtos.Where(d => d.ParentCommentId == null).ToList();
        var lookup = topLevel.ToDictionary(d => d.Id);

        foreach (var reply in dtos.Where(d => d.ParentCommentId != null))
        {
            if (lookup.TryGetValue(reply.ParentCommentId!.Value, out var parent))
            {
                parent.Replies.Add(reply);
            }
            else
            {
                // In case parent was removed/not found in lookup, append to top
                topLevel.Add(reply);
            }
        }

        return topLevel;
    }

    public async Task<bool> RequiresCommentModerationAsync(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return true;

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null) return true;
        if (await IsModeratorAsync(user)) return false;

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var accountAgeDays = (DateTime.UtcNow - user.CreatedAt).TotalDays;
        var approvedPostsCount = await context.Polls.CountAsync(p => p.CreatorId == userId && p.Status == PollStatus.Approved);
        return accountAgeDays < 7 || approvedPostsCount < 10;
    }

    private async Task<bool> IsModeratorAsync(ApplicationUser user)
    {
        return await _userManager.IsInRoleAsync(user, "Admin") ||
               await _userManager.IsInRoleAsync(user, "SuperModerator") ||
               await _userManager.IsInRoleAsync(user, "Moderator") ||
               user.IsRootAdmin;
    }

    public async Task<ServiceResult> AddCommentAsync(int pollId, string userId, string content, int? parentCommentId = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return ServiceResult.Fail("Comment content cannot be empty.");
        }

        var sanitized = Regex.Replace(content.Trim(), @"<[^>]*>", string.Empty);
        if (sanitized.Length > 1000)
        {
            return ServiceResult.Fail("Comment content cannot exceed 1000 characters.");
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var poll = await context.Polls.FirstOrDefaultAsync(p => p.Id == pollId);
        if (poll == null) return ServiceResult.Fail("Poll not found.");
        if (poll.Status == PollStatus.Removed) return ServiceResult.Fail("This poll has been removed. Comments are closed.");

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null) return ServiceResult.Fail("User not found or unauthenticated.");
        if (user.IsCurrentlyBanned) return ServiceResult.Fail("Your account is suspended. You cannot post comments.");

        // Validate parent comment for 2-level nesting
        int? effectiveParentId = null;
        if (parentCommentId.HasValue)
        {
            var parent = await context.PollComments.FirstOrDefaultAsync(c => c.Id == parentCommentId.Value && c.PollId == pollId);
            if (parent == null)
            {
                return ServiceResult.Fail("The comment you are replying to does not exist.");
            }
            // If replying to a reply, flatten to the root parent comment to maintain strict 2-level nesting
            effectiveParentId = parent.ParentCommentId ?? parent.Id;
        }

        bool isAdminOrMod = await IsModeratorAsync(user);
        bool requiresModeration = await RequiresCommentModerationAsync(userId);

        PollStatus initialStatus = PollStatus.Approved;
        string resultMsg = "Comment posted successfully!";

        if (requiresModeration)
        {
            initialStatus = PollStatus.PendingReview;
            resultMsg = "Your comment is in the moderation queue and will be published once approved.";
        }

        var comment = new PollComment
        {
            PollId = pollId,
            UserId = userId,
            ParentCommentId = effectiveParentId,
            Content = sanitized,
            Status = initialStatus,
            CreatedAt = DateTime.UtcNow
        };

        context.PollComments.Add(comment);
        await context.SaveChangesAsync();

        if (initialStatus == PollStatus.Approved)
        {
            var roleBadge = isAdminOrMod
                ? (await _userManager.IsInRoleAsync(user, "Admin") || user.IsRootAdmin ? "Admin" :
                   await _userManager.IsInRoleAsync(user, "SuperModerator") ? "SuperModerator" : "Moderator")
                : null;

            var commentDto = new PollCommentDto
            {
                Id = comment.Id,
                PollId = pollId,
                UserId = userId,
                UserName = PollService.FormatDisplayName(user.UserName, user.Email),
                UserRoleBadge = roleBadge,
                ParentCommentId = effectiveParentId,
                Content = sanitized,
                Status = (int)initialStatus,
                CreatedAt = comment.CreatedAt,
                CanDelete = true,
                Replies = new()
            };

            _pollEventNotifier?.NotifyCommentAdded(pollId, commentDto);
            _pollEventNotifier?.NotifyPollUpdated(pollId);

            if (_notificationService != null)
            {
                var commenterName = PollService.FormatDisplayName(user.UserName, user.Email);
                var snippet = sanitized.Length > 60 ? sanitized[..60] + "..." : sanitized;

                if (effectiveParentId.HasValue)
                {
                    var parentComment = await context.PollComments.AsNoTracking().FirstOrDefaultAsync(c => c.Id == effectiveParentId.Value);
                    if (parentComment != null && parentComment.UserId != userId)
                    {
                        _ = _notificationService.CreateNotificationAsync(
                            parentComment.UserId,
                            NotificationType.CommentReplied,
                            "New Reply to Your Comment",
                            $"{commenterName} replied: \"{snippet}\"",
                            $"/poll/{pollId}");
                    }
                }
                else if (poll.CreatorId != userId)
                {
                    _ = _notificationService.CreateNotificationAsync(
                        poll.CreatorId,
                        NotificationType.CommentReplied,
                        "New Comment on Your Poll",
                        $"{commenterName} commented: \"{snippet}\"",
                        $"/poll/{pollId}");
                }
            }
        }

        return ServiceResult.Ok(resultMsg, pollId);
    }

    public async Task<ServiceResult> DeleteCommentAsync(int commentId, string currentUserId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var comment = await context.PollComments.FirstOrDefaultAsync(c => c.Id == commentId);
        if (comment == null) return ServiceResult.Fail("Comment not found.");

        var user = await _userManager.FindByIdAsync(currentUserId);
        if (user == null) return ServiceResult.Fail("Unauthorized action.");

        bool canModerate = await _userManager.IsInRoleAsync(user, "Admin") ||
                           await _userManager.IsInRoleAsync(user, "SuperModerator") ||
                           await _userManager.IsInRoleAsync(user, "Moderator") ||
                           user.IsRootAdmin;

        if (comment.UserId != currentUserId && !canModerate)
        {
            return ServiceResult.Fail("You can only delete your own comments.");
        }

        // Check if there are replies
        bool hasReplies = await context.PollComments.AnyAsync(c => c.ParentCommentId == commentId && c.Status != PollStatus.Removed);
        int pollId = comment.PollId;

        if (hasReplies)
        {
            comment.Status = PollStatus.Removed;
            comment.ModerationReason = "This comment has been deleted.";
            await context.SaveChangesAsync();
        }
        else
        {
            context.PollComments.Remove(comment);
            await context.SaveChangesAsync();
        }

        _pollEventNotifier?.NotifyCommentDeleted(pollId, commentId);
        _pollEventNotifier?.NotifyPollUpdated(pollId);

        return ServiceResult.Ok("Comment deleted.");
    }

    public async Task<List<PendingCommentDto>> GetPendingCommentsAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var pendingComments = await context.PollComments
            .Include(c => c.User)
            .Include(c => c.Poll)
            .Where(c => c.Status == PollStatus.PendingReview)
            .OrderBy(c => c.CreatedAt)
            .AsNoTracking()
            .ToListAsync();

        var result = new List<PendingCommentDto>();

        foreach (var c in pendingComments)
        {
            var userCreatedAt = c.User?.CreatedAt ?? DateTime.UtcNow;
            var approvedCount = await context.Polls.CountAsync(p => p.CreatorId == c.UserId && p.Status == PollStatus.Approved);

            var reasons = new List<string>();
            var accountAgeDays = (DateTime.UtcNow - userCreatedAt).TotalDays;
            if (accountAgeDays < 7)
            {
                reasons.Add($"New account ({Math.Round(accountAgeDays, 1)}d < 7d threshold)");
            }
            if (approvedCount < 10)
            {
                reasons.Add($"Insufficient approved polls ({approvedCount} < 10 required)");
            }

            result.Add(new PendingCommentDto
            {
                Id = c.Id,
                PollId = c.PollId,
                PollTitle = c.Poll?.Title ?? $"Poll #{c.PollId}",
                UserId = c.UserId,
                UserName = PollService.FormatDisplayName(c.User?.UserName, c.User?.Email),
                Content = c.Content,
                CreatedAt = c.CreatedAt,
                GatingReason = string.Join("; ", reasons)
            });
        }

        return result;
    }

    public async Task<ServiceResult> ApproveCommentAsync(int commentId, string reviewerId)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var reviewer = await _userManager.FindByIdAsync(reviewerId);
        if (reviewer == null) return ServiceResult.Fail("Unauthorized action.");
        bool canModerate = await _userManager.IsInRoleAsync(reviewer, "Admin") ||
                           await _userManager.IsInRoleAsync(reviewer, "SuperModerator") ||
                           await _userManager.IsInRoleAsync(reviewer, "Moderator") ||
                           reviewer.IsRootAdmin;
        if (!canModerate) return ServiceResult.Fail("You do not have permission to moderate comments.");

        var comment = await context.PollComments.Include(c => c.User).FirstOrDefaultAsync(c => c.Id == commentId);
        if (comment == null) return ServiceResult.Fail("Comment not found.");

        comment.Status = PollStatus.Approved;
        comment.ModerationReason = null;
        await context.SaveChangesAsync();

        var u = comment.User;
        var roleBadge = u != null ? (await _userManager.IsInRoleAsync(u, "Admin") || u.IsRootAdmin ? "Admin" :
            await _userManager.IsInRoleAsync(u, "SuperModerator") ? "SuperModerator" :
            await _userManager.IsInRoleAsync(u, "Moderator") ? "Moderator" : null) : null;

        var commentDto = new PollCommentDto
        {
            Id = comment.Id,
            PollId = comment.PollId,
            UserId = comment.UserId,
            UserName = PollService.FormatDisplayName(u?.UserName, u?.Email),
            UserRoleBadge = roleBadge,
            ParentCommentId = comment.ParentCommentId,
            Content = comment.Content,
            Status = (int)PollStatus.Approved,
            CreatedAt = comment.CreatedAt,
            CanDelete = true,
            Replies = new()
        };

        _pollEventNotifier?.NotifyCommentAdded(comment.PollId, commentDto);
        _pollEventNotifier?.NotifyPollUpdated(comment.PollId);

        return ServiceResult.Ok("Comment approved successfully!");
    }

    public async Task<ServiceResult> RemoveCommentAsync(int commentId, string reviewerId, string reason)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var reviewer = await _userManager.FindByIdAsync(reviewerId);
        if (reviewer == null) return ServiceResult.Fail("Unauthorized action.");
        bool canModerate = await _userManager.IsInRoleAsync(reviewer, "Admin") ||
                           await _userManager.IsInRoleAsync(reviewer, "SuperModerator") ||
                           await _userManager.IsInRoleAsync(reviewer, "Moderator") ||
                           reviewer.IsRootAdmin;
        if (!canModerate) return ServiceResult.Fail("You do not have permission to moderate comments.");

        var comment = await context.PollComments.FirstOrDefaultAsync(c => c.Id == commentId);
        if (comment == null) return ServiceResult.Fail("Comment not found.");

        bool hasReplies = await context.PollComments.AnyAsync(c => c.ParentCommentId == commentId);
        int pollId = comment.PollId;

        if (hasReplies)
        {
            comment.Status = PollStatus.Removed;
            comment.ModerationReason = string.IsNullOrWhiteSpace(reason) ? "Violating comment removed by moderator." : reason.Trim();
            await context.SaveChangesAsync();
        }
        else
        {
            context.PollComments.Remove(comment);
            await context.SaveChangesAsync();
        }

        _pollEventNotifier?.NotifyCommentDeleted(pollId, commentId);
        _pollEventNotifier?.NotifyPollUpdated(pollId);

        return ServiceResult.Ok("Comment rejected / removed.");
    }

    public async Task<ServiceResult> UpvoteCommentAsync(int commentId, string userId)
    {
        if (string.IsNullOrEmpty(userId)) return ServiceResult.Fail("Authentication required to upvote.");
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var comment = await context.PollComments.FirstOrDefaultAsync(c => c.Id == commentId && c.Status == PollStatus.Approved);
        if (comment == null) return ServiceResult.Fail("Comment not found or not approved.");

        var existingLike = await context.CommentLikes.FirstOrDefaultAsync(cl => cl.CommentId == commentId && cl.UserId == userId);
        if (existingLike != null)
        {
            context.CommentLikes.Remove(existingLike);
            comment.Upvotes = Math.Max(0, comment.Upvotes - 1);
            await context.SaveChangesAsync();
            return ServiceResult.Ok("Upvote removed.", comment.Upvotes);
        }

        context.CommentLikes.Add(new CommentLike
        {
            CommentId = commentId,
            UserId = userId,
            CreatedAt = DateTime.UtcNow
        });
        comment.Upvotes++;

        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueConstraintViolation("CommentLikes"))
        {
            // CommentLikes has a composite PK (CommentId, UserId); a concurrent double-submit means the
            // like already exists. Re-read the persisted row so the returned count reflects the database
            // rather than an extra increment that was rolled back.
            var current = await context.PollComments.AsNoTracking()
                .Where(c => c.Id == commentId)
                .Select(c => c.Upvotes)
                .FirstOrDefaultAsync();
            return ServiceResult.Ok("Upvoted successfully.", current);
        }

        return ServiceResult.Ok("Upvoted successfully.", comment.Upvotes);
    }

    public async Task<ServiceResult> TogglePinCommentAsync(int commentId, string userId)
    {
        if (string.IsNullOrEmpty(userId)) return ServiceResult.Fail("Authentication required.");
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var comment = await context.PollComments.Include(c => c.Poll).FirstOrDefaultAsync(c => c.Id == commentId);
        if (comment == null) return ServiceResult.Fail("Comment not found.");

        var user = await _userManager.FindByIdAsync(userId);
        bool canPin = comment.Poll != null && (comment.Poll.CreatorId == userId || (user != null && await IsModeratorAsync(user)));
        if (!canPin) return ServiceResult.Fail("Only the poll author or moderators can pin comments.");

        if (!comment.IsPinned)
        {
            var existingPinned = await context.PollComments.Where(c => c.PollId == comment.PollId && c.IsPinned).ToListAsync();
            foreach (var p in existingPinned) p.IsPinned = false;
            comment.IsPinned = true;
        }
        else
        {
            comment.IsPinned = false;
        }

        await context.SaveChangesAsync();
        return ServiceResult.Ok(comment.IsPinned ? "Comment pinned." : "Comment unpinned.", comment.PollId);
    }
}
