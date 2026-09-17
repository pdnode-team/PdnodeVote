using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PdnodeVote.Client.Models;
using PdnodeVote.Data;
using PollStatus = PdnodeVote.Data.PollStatus;
using ResultVisibility = PdnodeVote.Data.ResultVisibility;
using ServiceResult = PdnodeVote.Client.Models.ServiceResult;

namespace PdnodeVote.Services;

public class PollService(
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    IEmailNotificationService emailNotificationService,
    UserManager<ApplicationUser> userManager,
    IPollEventNotifier? pollEventNotifier = null,
    INotificationService? notificationService = null,
    ICategoryService? categoryService = null)
{
    // Server-side content limits. The Blazor UI enforces the same numbers, but the API is reachable
    // directly; without these an oversized title/description/option list is persisted verbatim
    // (storage DoS) and can overflow bounded columns such as Notification.Message.
    public const int MaxTitleLength = 100;
    public const int MaxDescriptionLength = 500;
    public const int MinOptionCount = 2;
    public const int MaxOptionCount = 20;
    public const int MaxOptionTextLength = 200;
    public const int MaxTagLength = 30;
    public const int MaxTagsPerPoll = 5;

    // Upper bound for feed pagination: an unclamped pageSize let an anonymous caller eager-load the
    // whole table through the Include()s in GetPollFeedAsync.
    public const int MaxFeedPageSize = 100;

    public static string FormatDisplayName(string? userName, string? email = null)
    {
        if (!string.IsNullOrWhiteSpace(userName))
        {
            return userName.Contains('@') ? userName.Split('@')[0] : userName;
        }
        if (!string.IsNullOrWhiteSpace(email))
        {
            return email.Split('@')[0];
        }
        return "Anonymous";
    }

    public async Task<List<PollListItemDto>> GetPollsAsync(string? search = null, string? status = "all", string? sortBy = "latest", int? categoryId = null, string? tag = null)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync();

        // Public poll list: only show Approved polls
        var query = context.Polls
            .Include(p => p.Creator)
            .Include(p => p.Category)
            .Include(p => p.PollTags).ThenInclude(pt => pt.Tag)
            .Include(p => p.Comments)
            .Include(p => p.Options)
            .Include(p => p.Votes)
            .Where(p => p.Status == PollStatus.Approved)
            .AsNoTracking();

        if (categoryId.HasValue)
        {
            var allCats = await context.Categories.AsNoTracking().Select(c => new { c.Id, c.ParentId }).ToListAsync();
            var targetCatIds = new HashSet<int> { categoryId.Value };
            bool added;
            do
            {
                added = false;
                foreach (var c in allCats)
                {
                    if (c.ParentId.HasValue && targetCatIds.Contains(c.ParentId.Value) && !targetCatIds.Contains(c.Id))
                    {
                        targetCatIds.Add(c.Id);
                        added = true;
                    }
                }
            } while (added);

            query = query.Where(p => p.CategoryId.HasValue && targetCatIds.Contains(p.CategoryId.Value));
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            var lowerTag = tag.Trim().ToLower();
            query = query.Where(p => p.PollTags.Any(pt => pt.Tag != null && pt.Tag.Name.ToLower() == lowerTag));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(p => p.Title.ToLower().Contains(s) || (p.Description != null && p.Description.ToLower().Contains(s)));
        }

        var now = DateTime.UtcNow;
        if (status == "active")
        {
            query = query.Where(p => p.ExpiresAt == null || p.ExpiresAt > now);
        }
        else if (status == "expired")
        {
            query = query.Where(p => p.ExpiresAt != null && p.ExpiresAt <= now);
        }

        var polls = await query.ToListAsync();

        var items = polls.Select(p =>
        {
            var participantCount = p.Votes
                .Select(v => !string.IsNullOrEmpty(v.UserId) ? v.UserId : v.IpAddress)
                .Distinct()
                .Count();

            return new PollListItemDto
            {
                Id = p.Id,
                Title = p.Title,
                Description = p.Description,
                CreatorId = p.CreatorId,
                CreatorName = FormatDisplayName(p.Creator?.UserName, p.Creator?.Email),
                RequireLogin = p.RequireLogin,
                IsMultipleChoice = p.IsMultipleChoice,
                ResultVisibility = (PdnodeVote.Client.Models.ResultVisibility)(int)p.ResultVisibility,
                CreatedAt = p.CreatedAt,
                ExpiresAt = p.ExpiresAt,
                ParticipantCount = participantCount,
                OptionCount = p.Options.Count,
                Status = (PdnodeVote.Client.Models.PollStatus)(int)p.Status,
                ModerationReason = p.ModerationReason,
                IsPinned = p.IsPinned,
                CategoryId = p.CategoryId,
                CategoryName = p.Category?.Name,
                Tags = p.PollTags.Select(pt => pt.Tag?.Name ?? "").Where(t => !string.IsNullOrEmpty(t)).ToList(),
                CommentCount = p.Comments.Count(c => c.Status == PollStatus.Approved),
                HasImages = p.Options.Any(o => !string.IsNullOrEmpty(o.ImageUrl))
            };
        });

        // Always sort pinned items to top first
        if (sortBy == "popular")
        {
            return items.OrderByDescending(p => p.IsPinned)
                        .ThenByDescending(p => p.ParticipantCount)
                        .ThenByDescending(p => p.CreatedAt)
                        .ToList();
        }

        return items.OrderByDescending(p => p.IsPinned)
                    .ThenByDescending(p => p.CreatedAt)
                    .ToList();
    }

    public async Task<List<PollListItemDto>> GetUserPollsAsync(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return new List<PollListItemDto>();

        await using var context = await dbContextFactory.CreateDbContextAsync();

        var polls = await context.Polls
            .Include(p => p.Creator)
            .Include(p => p.Category)
            .Include(p => p.PollTags).ThenInclude(pt => pt.Tag)
            .Include(p => p.Comments)
            .Include(p => p.Options)
            .Include(p => p.Votes)
            .Where(p => p.CreatorId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .AsNoTracking()
            .ToListAsync();

        return polls.Select(p =>
        {
            var participantCount = p.Votes
                .Select(v => !string.IsNullOrEmpty(v.UserId) ? v.UserId : v.IpAddress)
                .Distinct()
                .Count();

            return new PollListItemDto
            {
                Id = p.Id,
                Title = p.Title,
                Description = p.Description,
                CreatorId = p.CreatorId,
                CreatorName = FormatDisplayName(p.Creator?.UserName, p.Creator?.Email),
                RequireLogin = p.RequireLogin,
                IsMultipleChoice = p.IsMultipleChoice,
                ResultVisibility = (PdnodeVote.Client.Models.ResultVisibility)(int)p.ResultVisibility,
                CreatedAt = p.CreatedAt,
                ExpiresAt = p.ExpiresAt,
                ParticipantCount = participantCount,
                OptionCount = p.Options.Count,
                Status = (PdnodeVote.Client.Models.PollStatus)(int)p.Status,
                ModerationReason = p.ModerationReason,
                IsPinned = p.IsPinned,
                CategoryId = p.CategoryId,
                CategoryName = p.Category?.Name,
                Tags = p.PollTags.Select(pt => pt.Tag?.Name ?? "").Where(t => !string.IsNullOrEmpty(t)).ToList(),
                CommentCount = p.Comments.Count(c => c.Status == PollStatus.Approved)
            };
        }).ToList();
    }

    public async Task<List<PendingReviewPollDto>> GetPendingReviewPollsAsync()
    {
        await using var context = await dbContextFactory.CreateDbContextAsync();

        var pendingPolls = await context.Polls
            .Include(p => p.Creator)
            .Include(p => p.Category)
            .Include(p => p.PollTags).ThenInclude(pt => pt.Tag)
            .Include(p => p.Options.OrderBy(o => o.Order))
            .Where(p => p.Status == PollStatus.PendingReview)
            .OrderBy(p => p.CreatedAt)
            .AsNoTracking()
            .ToListAsync();

        var result = new List<PendingReviewPollDto>();

        // One grouped count covering every creator instead of one CountAsync per pending poll.
        var creatorIds = pendingPolls.Select(p => p.CreatorId).Distinct().ToList();
        var approvedCountsByCreator = await context.Polls
            .Where(p => creatorIds.Contains(p.CreatorId) && p.Status == PollStatus.Approved)
            .GroupBy(p => p.CreatorId)
            .Select(g => new { CreatorId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CreatorId, x => x.Count);

        foreach (var poll in pendingPolls)
        {
            var creator = poll.Creator;
            var creatorCreatedAt = creator?.CreatedAt ?? DateTime.UtcNow;
            var approvedCount = approvedCountsByCreator.GetValueOrDefault(poll.CreatorId);

            var reasons = new List<string>();
            var accountAgeDays = (DateTime.UtcNow - creatorCreatedAt).TotalDays;
            if (accountAgeDays < 7)
            {
                reasons.Add($"New account ({Math.Round(accountAgeDays, 1)} days old < 7 days threshold)");
            }
            if (approvedCount < 10)
            {
                reasons.Add($"Low approved posts ({approvedCount} < 10 required)");
            }

            result.Add(new PendingReviewPollDto
            {
                Id = poll.Id,
                Title = poll.Title,
                Description = poll.Description,
                CreatorId = poll.CreatorId,
                CreatorName = FormatDisplayName(creator?.UserName, creator?.Email),
                CreatorEmail = creator?.Email ?? "",
                CreatorCreatedAt = creatorCreatedAt,
                CreatorApprovedPollsCount = approvedCount,
                CreatedAt = poll.CreatedAt,
                Options = poll.Options.Select(o => o.Text).ToList(),
                GatingReason = reasons.Count > 0 ? string.Join(", ", reasons) : "Manual Review",
                CategoryId = poll.CategoryId,
                CategoryName = poll.Category?.Name,
                Tags = poll.PollTags.Select(pt => pt.Tag?.Name ?? "").Where(t => !string.IsNullOrEmpty(t)).ToList()
            });
        }

        return result;
    }

    public async Task<List<PollListItemDto>> GetAllAdminPollsAsync(string? search = null, PollStatus? status = null)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync();

        var query = context.Polls
            .Include(p => p.Creator)
            .Include(p => p.Options)
            .Include(p => p.Votes)
            .AsNoTracking();

        if (status.HasValue)
        {
            query = query.Where(p => p.Status == status.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(p => p.Title.ToLower().Contains(s) || (p.Description != null && p.Description.ToLower().Contains(s)) || (p.Creator != null && p.Creator.UserName!.ToLower().Contains(s)));
        }

        var polls = await query.OrderByDescending(p => p.IsPinned).ThenByDescending(p => p.CreatedAt).ToListAsync();

        return polls.Select(p =>
        {
            var participantCount = p.Votes
                .Select(v => !string.IsNullOrEmpty(v.UserId) ? v.UserId : v.IpAddress)
                .Distinct()
                .Count();

            return new PollListItemDto
            {
                Id = p.Id,
                Title = p.Title,
                Description = p.Description,
                CreatorId = p.CreatorId,
                CreatorName = FormatDisplayName(p.Creator?.UserName, p.Creator?.Email),
                RequireLogin = p.RequireLogin,
                IsMultipleChoice = p.IsMultipleChoice,
                ResultVisibility = (PdnodeVote.Client.Models.ResultVisibility)(int)p.ResultVisibility,
                CreatedAt = p.CreatedAt,
                ExpiresAt = p.ExpiresAt,
                ParticipantCount = participantCount,
                OptionCount = p.Options.Count,
                Status = (PdnodeVote.Client.Models.PollStatus)(int)p.Status,
                ModerationReason = p.ModerationReason,
                IsPinned = p.IsPinned
            };
        }).ToList();
    }

    /// <summary>
    /// Validates author-supplied poll content against the server-side limits and returns the
    /// user-facing reason when invalid, or <c>null</c> when valid.
    /// </summary>
    private static string? ValidatePollContent(string? title, string? description, IReadOnlyList<string> cleanedOptions)
    {
        var trimmedTitle = title?.Trim() ?? string.Empty;
        if (trimmedTitle.Length == 0)
        {
            return "Poll title cannot be empty.";
        }
        if (trimmedTitle.Length > MaxTitleLength)
        {
            return $"Poll title cannot exceed {MaxTitleLength} characters.";
        }

        var trimmedDescription = description?.Trim();
        if (!string.IsNullOrEmpty(trimmedDescription) && trimmedDescription.Length > MaxDescriptionLength)
        {
            return $"Poll description cannot exceed {MaxDescriptionLength} characters.";
        }

        if (cleanedOptions.Count < MinOptionCount)
        {
            return "At least 2 valid options are required.";
        }
        if (cleanedOptions.Count > MaxOptionCount)
        {
            return $"A poll cannot have more than {MaxOptionCount} options.";
        }
        if (cleanedOptions.Any(o => o.Length > MaxOptionTextLength))
        {
            return $"Each option cannot exceed {MaxOptionTextLength} characters.";
        }

        return null;
    }

    /// <summary>
    /// Enforces <see cref="CategoryPostPermission"/> when a poll is placed in (or moved to) a board.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="ICategoryService.GetPostDenialAsync(int?, string, bool, bool)"/> so the
    /// rule has exactly one definition instead of two divergent copies. The local copy did not consult
    /// the <c>CategoryModerators</c> mapping, so a board-level moderator assignment had no effect on the
    /// write path (TODO-BUGS.md L5). The inline fallback below is used only when no category service was
    /// injected (unit tests build this service by hand); it implements the same rule.
    /// </remarks>
    private async Task<(bool Allowed, string Message)> CanPostInCategoryAsync(
        ApplicationDbContext context, int? categoryId, bool isAdmin, bool isAdminOrMod,
        string? userId = null, ApplicationUser? user = null)
    {
        if (!categoryId.HasValue)
        {
            return (true, string.Empty);
        }

        if (categoryService is not null && !string.IsNullOrEmpty(userId))
        {
            var denial = await categoryService.GetPostDenialAsync(categoryId, userId, isAdmin, isAdminOrMod);
            return denial is null
                ? (true, string.Empty)
                : (false, DescribeDenial(denial.Value));
        }

        var category = await context.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == categoryId.Value);
        if (category == null)
        {
            return (false, DescribeDenial(CategoryPostDenial.BoardNotFound));
        }

        if (category.PostPermission == CategoryPostPermission.AdminOnly && !isAdmin)
        {
            return (false, DescribeDenial(CategoryPostDenial.AdministratorsOnly));
        }
        if (category.PostPermission == CategoryPostPermission.ModeratorOnly && !isAdminOrMod)
        {
            return (false, DescribeDenial(CategoryPostDenial.ModeratorsOnly));
        }

        // Same board-level moderator rule as CategoryService, so behavior does not depend on whether the
        // optional dependency happens to be present.
        if (category.PostPermission == CategoryPostPermission.ModeratorOnly && !isAdmin)
        {
            bool isSuperMod = user is not null && await userManager.IsInRoleAsync(user, "SuperModerator");
            if (!isSuperMod && !string.IsNullOrEmpty(userId))
            {
                bool assigned = await context.CategoryModerators.AnyAsync(cm => cm.CategoryId == category.Id);
                if (assigned && !await context.CategoryModerators.AnyAsync(cm => cm.CategoryId == category.Id && cm.UserId == userId))
                {
                    return (false, DescribeDenial(CategoryPostDenial.NotAssignedModerator));
                }
            }
        }

        return (true, string.Empty);
    }

    private static string DescribeDenial(CategoryPostDenial denial) => denial switch
    {
        CategoryPostDenial.BoardNotFound => "The selected board does not exist.",
        CategoryPostDenial.AdministratorsOnly => "This board is restricted to administrators only.",
        CategoryPostDenial.ModeratorsOnly => "This board is restricted to moderators and administrators only.",
        CategoryPostDenial.NotAssignedModerator => "This board is restricted to its assigned moderators.",
        _ => "You do not have permission to post in this board."
    };

    /// <summary>
    /// Resolves the moderation privileges of a user for board-permission checks.
    /// </summary>
    private async Task<(bool IsAdmin, bool IsAdminOrMod)> GetModerationFlagsAsync(ApplicationUser? user)
    {
        if (user == null)
        {
            return (false, false);
        }

        bool isAdmin = user.IsRootAdmin || await userManager.IsInRoleAsync(user, "Admin");
        bool isModerator = await userManager.IsInRoleAsync(user, "SuperModerator")
                           || await userManager.IsInRoleAsync(user, "Moderator");
        return (isAdmin, isAdmin || isModerator);
    }

    /// <summary>
    /// Normalizes author-supplied tag names: trimmed, non-empty, length-capped, deduplicated
    /// case-insensitively and limited to <see cref="MaxTagsPerPoll"/> entries.
    /// </summary>
    private static List<string> NormalizeTagNames(IEnumerable<string>? tags) =>
        tags == null
            ? new List<string>()
            : tags.Where(t => !string.IsNullOrWhiteSpace(t))
                  .Select(t => t.Trim())
                  .Where(t => t.Length <= MaxTagLength)
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .Take(MaxTagsPerPoll)
                  .ToList();

    /// <summary>
    /// Replaces the poll's tags with <paramref name="newTagNames"/> while keeping
    /// <see cref="Tag.UsageCount"/> consistent: only genuinely added tags are incremented and only
    /// genuinely removed ones are decremented (never below zero).
    /// </summary>
    /// <remarks>
    /// The previous code incremented every tag on each edit (unchanged tags included) and never
    /// decremented removed ones, so the popular-tag ranking drifted upwards forever.
    /// </remarks>
    private async Task ApplyTagsAsync(ApplicationDbContext context, Poll poll, List<string> newTagNames)
    {
        var oldTagNames = poll.PollTags
            .Where(pt => pt.Tag != null)
            .Select(pt => pt.Tag!.Name)
            .ToList();
        var oldNames = new HashSet<string>(oldTagNames, StringComparer.OrdinalIgnoreCase);
        var newNames = new HashSet<string>(newTagNames, StringComparer.OrdinalIgnoreCase);

        foreach (var pollTag in poll.PollTags)
        {
            if (pollTag.Tag != null && !newNames.Contains(pollTag.Tag.Name))
            {
                pollTag.Tag.UsageCount = Math.Max(0, pollTag.Tag.UsageCount - 1);
            }
        }

        context.PollTags.RemoveRange(poll.PollTags);
        poll.PollTags.Clear();

        foreach (var tagName in newTagNames)
        {
            var tag = await GetOrCreateTagAsync(context, tagName);
            if (!oldNames.Contains(tag.Name))
            {
                tag.UsageCount++;
            }
            poll.PollTags.Add(new PollTag { Tag = tag });
        }
    }

    /// <summary>
    /// Resolves a tag by name, creating it when absent, without failing on a concurrent creation of
    /// the same name.
    /// </summary>
    /// <remarks>
    /// The previous read-then-insert raced: two requests could both see "missing" and both insert,
    /// and the loser's unique-index violation aborted the whole SaveChanges, so the poll was never
    /// created and the caller got an HTTP 500. This uses SQLite's INSERT OR IGNORE so the losing
    /// insert is a no-op, then re-reads the winner's row.
    /// </remarks>
    private static async Task<Tag> GetOrCreateTagAsync(ApplicationDbContext context, string tagName)
    {
        // Case-insensitive lookup, matching the previous behaviour (Tags.Name's unique index is
        // case-sensitive, so "C#" and "c#" would otherwise become two different tags).
        var existing = await context.Tags
            .FirstOrDefaultAsync(t => t.Name.ToLower() == tagName.ToLower());
        if (existing is not null)
        {
            return existing;
        }

        // No existing row: insert it, tolerating a concurrent creator (INSERT OR IGNORE makes the
        // losing side a no-op instead of aborting the whole SaveChanges with a unique violation).
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT OR IGNORE INTO Tags (Name, UsageCount, CreatedAt) VALUES ({tagName}, 0, {DateTime.UtcNow})");

        return await context.Tags.FirstAsync(t => t.Name.ToLower() == tagName.ToLower());
    }

    public async Task<PollDetailDto?> GetPollDetailAsync(int pollId, string? currentUserId, string clientIp)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync();

        var poll = await context.Polls
            .Include(p => p.Creator)
            .Include(p => p.Category)
            .Include(p => p.PollTags).ThenInclude(pt => pt.Tag)
            .Include(p => p.Comments)
            .Include(p => p.Options.OrderBy(o => o.Order))
            .Include(p => p.Votes)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == pollId);

        if (poll == null) return null;

        bool isAuthor = !string.IsNullOrEmpty(currentUserId) && poll.CreatorId == currentUserId;
        bool isAdmin = false;
        bool isModerator = false;

        if (!string.IsNullOrEmpty(currentUserId))
        {
            var user = await userManager.FindByIdAsync(currentUserId);
            if (user != null)
            {
                isAdmin = await userManager.IsInRoleAsync(user, "Admin") || user.IsRootAdmin;
                isModerator = await userManager.IsInRoleAsync(user, "SuperModerator") || await userManager.IsInRoleAsync(user, "Moderator");
            }
        }

        bool canModerate = isAdmin || isModerator;

        // If poll is Removed:
        // Reddit-style: regular visitors see removed notice without options. Author and mods see full info with removal notice.
        if (poll.Status == PollStatus.Removed && !isAuthor && !canModerate)
        {
            return new PollDetailDto
            {
                Id = poll.Id,
                Title = poll.Title,
                Description = null,
                CreatorId = poll.CreatorId,
                CreatorName = "[Removed]",
                CreatorEmail = "",
                Status = (PdnodeVote.Client.Models.PollStatus)(int)PollStatus.Removed,
                ModerationReason = poll.ModerationReason ?? "This post was removed by community moderators.",
                CreatedAt = poll.CreatedAt,
                IsAuthor = false,
                CanModerate = false,
                IsAdmin = false,
                Options = new()
            };
        }

        // If PendingReview or ReturnedForRevision, only author and mods/admins can view
        if ((poll.Status == PollStatus.PendingReview || poll.Status == PollStatus.ReturnedForRevision) && !isAuthor && !canModerate)
        {
            return null; // Invisible to general public
        }

        var userVotes = poll.Votes.Where(v =>
            !string.IsNullOrEmpty(currentUserId)
                ? v.UserId == currentUserId
                : (!string.IsNullOrEmpty(clientIp) && v.IpAddress == clientIp)
        ).ToList();

        bool hasVoted = userVotes.Any();
        var votedOptionIds = userVotes.Select(v => v.PollOptionId).Distinct().ToList();

        int totalVotes = poll.Votes.Count;
        int totalParticipants = poll.Votes
            .Select(v => !string.IsNullOrEmpty(v.UserId) ? v.UserId : v.IpAddress)
            .Distinct()
            .Count();

        // ResultVisibility.AfterVoting used to be enforced only by the client, which hid the bars
        // while the API still shipped the exact tally — readable in devtools or via the CSV export.
        // The author and moderators keep full visibility so the feature stays usable for them.
        bool canSeeResults = poll.ResultVisibility == ResultVisibility.AlwaysPublic
                             || hasVoted
                             || poll.IsExpired
                             || isAuthor
                             || canModerate;

        var optionDtos = poll.Options.Select(o =>
        {
            int count = poll.Votes.Count(v => v.PollOptionId == o.Id);
            double percentage = totalParticipants > 0 ? Math.Round((double)count / totalParticipants * 100, 1) : 0;
            return new PollOptionDto
            {
                Id = o.Id,
                Text = o.Text,
                ImageUrl = o.ImageUrl,
                Order = o.Order,
                VoteCount = canSeeResults ? count : 0,
                Percentage = canSeeResults ? percentage : 0
            };
        }).ToList();

        if (!canSeeResults)
        {
            totalVotes = 0;
            totalParticipants = 0;
        }

        int creatorLevel = 1;
        if (poll.Creator != null)
        {
            int cVoteCount = await context.VoteRecords.CountAsync(v => v.UserId == poll.CreatorId);
            int cApprovedPolls = await context.Polls.CountAsync(p => p.CreatorId == poll.CreatorId && p.Status == PollStatus.Approved);
            int cVotesReceived = await context.VoteRecords.CountAsync(v => v.Poll != null && v.Poll.CreatorId == poll.CreatorId);
            creatorLevel = UserLevelHelper.CalculateLevel(poll.Creator.CreatedAt, cVoteCount, cApprovedPolls, cVotesReceived);
        }

        return new PollDetailDto
        {
            Id = poll.Id,
            Title = poll.Title,
            Description = poll.Description,
            CreatorId = poll.CreatorId,
            CreatorName = FormatDisplayName(poll.Creator?.UserName, poll.Creator?.Email),
            CreatorEmail = (isAuthor || canModerate) ? (poll.Creator?.Email ?? "") : "",
            CreatorLevel = creatorLevel,
            RequireLogin = poll.RequireLogin,
            IsMultipleChoice = poll.IsMultipleChoice,
            MaxChoices = poll.MaxChoices,
            ResultVisibility = (PdnodeVote.Client.Models.ResultVisibility)(int)poll.ResultVisibility,
            CreatedAt = poll.CreatedAt,
            ExpiresAt = poll.ExpiresAt,
            Status = (PdnodeVote.Client.Models.PollStatus)(int)poll.Status,
            ModerationReason = poll.ModerationReason,
            IsPinned = poll.IsPinned,
            TotalParticipants = totalParticipants,
            TotalVotesCount = totalVotes,
            ResultsVisible = canSeeResults,
            Options = optionDtos,
            HasCurrentUserVoted = hasVoted,
            UserVotedOptionIds = votedOptionIds,
            IsAuthor = isAuthor,
            CanModerate = canModerate,
            IsAdmin = isAdmin,
            CategoryId = poll.CategoryId,
            CategoryName = poll.Category?.Name,
            Tags = poll.PollTags.Select(pt => pt.Tag?.Name ?? "").Where(t => !string.IsNullOrEmpty(t)).ToList(),
            CommentCount = poll.Comments.Count(c => c.Status == PollStatus.Approved)
        };
    }

    public async Task<(bool Success, string Message)> CastVoteAsync(int pollId, string? currentUserId, string clientIp, List<int> selectedOptionIds)
    {
        if (selectedOptionIds == null || selectedOptionIds.Count == 0)
        {
            return (false, "Please select at least one option.");
        }

        await using var context = await dbContextFactory.CreateDbContextAsync();

        var poll = await context.Polls
            .Include(p => p.Options)
            .Include(p => p.Votes)
            .FirstOrDefaultAsync(p => p.Id == pollId);

        if (poll == null)
        {
            return (false, "Poll not found.");
        }

        if (poll.Status != PollStatus.Approved)
        {
            return (false, "This poll is not currently open for voting.");
        }

        if (poll.IsExpired)
        {
            return (false, "This poll has ended and is no longer accepting votes.");
        }

        // Check if user is banned
        if (!string.IsNullOrWhiteSpace(currentUserId))
        {
            var user = await context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == currentUserId);
            if (user != null && user.IsCurrentlyBanned)
            {
                var duration = user.BannedUntil.HasValue ? $"until {user.BannedUntil.Value:yyyy-MM-dd HH:mm UTC}" : "permanently";
                return (false, $"Your account is suspended {duration}. Reason: {user.BanReason ?? "Community rules violation"}.");
            }
        }

        if (poll.RequireLogin && string.IsNullOrWhiteSpace(currentUserId))
        {
            return (false, "This poll requires you to log in before voting.");
        }

        if (!string.IsNullOrWhiteSpace(currentUserId))
        {
            bool alreadyVotedByUser = poll.Votes.Any(v => v.UserId == currentUserId);
            if (alreadyVotedByUser)
            {
                return (false, "Your account has already voted in this poll.");
            }
        }
        else
        {
            bool alreadyVotedByIp = !string.IsNullOrWhiteSpace(clientIp) &&
                                    poll.Votes.Any(v => v.IpAddress == clientIp);
            if (alreadyVotedByIp)
            {
                return (false, "Your IP address has already voted in this poll. Register or log in to vote with your account.");
            }
        }

        var validOptionIds = poll.Options.Select(o => o.Id).ToHashSet();
        if (selectedOptionIds.Any(id => !validOptionIds.Contains(id)))
        {
            return (false, "Invalid option selection.");
        }

        if (!poll.IsMultipleChoice && selectedOptionIds.Count > 1)
        {
            return (false, "This is a single choice poll. You can only pick one option.");
        }

        if (poll.IsMultipleChoice && poll.MaxChoices > 0 && selectedOptionIds.Count > poll.MaxChoices)
        {
            return (false, $"You can select at most {poll.MaxChoices} options.");
        }

        var now = DateTime.UtcNow;
        foreach (var optionId in selectedOptionIds.Distinct())
        {
            context.VoteRecords.Add(new VoteRecord
            {
                PollId = pollId,
                PollOptionId = optionId,
                UserId = currentUserId,
                IpAddress = clientIp,
                VotedAt = now
            });
        }

        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueConstraintViolation("VoteRecords"))
        {
            // Two concurrent submits both passed the "already voted" check above; the unique index on
            // (PollId, UserId) let exactly one through. Report it as the duplicate it is instead of
            // letting an unhandled DbUpdateException surface as a 500.
            return (false, "Your vote has already been recorded for this poll.");
        }

        pollEventNotifier?.NotifyPollUpdated(pollId);
        return (true, "Vote submitted successfully! Thank you for participating.");
    }

    public async Task<(bool Success, string Message, int PollId)> CreatePollAsync(
        string title,
        string? description,
        string creatorId,
        bool requireLogin,
        bool isMultipleChoice,
        int maxChoices,
        ResultVisibility resultVisibility,
        DateTime? expiresAt,
        List<string> optionTexts,
        int? categoryId = null,
        List<string>? tags = null,
        List<PollOptionInputDto>? optionItems = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return (false, "Poll title cannot be empty.", 0);
        }

        var cleanedOptions = optionTexts
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .ToList();

        if (optionItems != null && optionItems.Count >= 2)
        {
            cleanedOptions = optionItems.Where(o => !string.IsNullOrWhiteSpace(o.Text)).Select(o => o.Text.Trim()).ToList();
        }

        var validationError = ValidatePollContent(title, description, cleanedOptions);
        if (validationError != null)
        {
            return (false, validationError, 0);
        }

        await using var context = await dbContextFactory.CreateDbContextAsync();

        var creator = await context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == creatorId);
        if (creator == null)
        {
            return (false, "User account not found.", 0);
        }

        // Check if creator is banned
        if (creator.IsCurrentlyBanned)
        {
            var duration = creator.BannedUntil.HasValue ? $"until {creator.BannedUntil.Value:yyyy-MM-dd HH:mm UTC}" : "permanently";
            return (false, $"Your account is suspended {duration}. You cannot create new polls. Reason: {creator.BanReason ?? "Community rules violation"}.", 0);
        }

        var userForRole = await userManager.FindByIdAsync(creatorId);
        bool isAdmin = userForRole != null && (await userManager.IsInRoleAsync(userForRole, "Admin") || userForRole.IsRootAdmin);
        bool isSuperMod = userForRole != null && await userManager.IsInRoleAsync(userForRole, "SuperModerator");
        bool isMod = userForRole != null && await userManager.IsInRoleAsync(userForRole, "Moderator");
        bool isAdminOrMod = isAdmin || isSuperMod || isMod;

        // Check category permissions
        var categoryCheck = await CanPostInCategoryAsync(context, categoryId, isAdmin, isAdminOrMod, creatorId, creator);
        if (!categoryCheck.Allowed)
        {
            return (false, categoryCheck.Message, 0);
        }

        // Gating logic:
        // Admin, SuperModerator, or Moderator: instant publish (Approved) without review!
        // Regular users: if registered < 7 days OR approved poll count < 10 -> PendingReview
        PollStatus initialStatus = PollStatus.Approved;
        string resultMsg = "Poll created successfully!";

        if (!isAdminOrMod)
        {
            var accountAgeDays = (DateTime.UtcNow - creator.CreatedAt).TotalDays;
            var approvedCount = await context.Polls.CountAsync(p => p.CreatorId == creatorId && p.Status == PollStatus.Approved);

            if (accountAgeDays < 7 || approvedCount < 10)
            {
                initialStatus = PollStatus.PendingReview;
                resultMsg = "Your poll has been submitted to the moderation queue and is pending review before public publishing.";
            }
        }

        var poll = new Poll
        {
            Title = title.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            CreatorId = creatorId,
            RequireLogin = requireLogin,
            IsMultipleChoice = isMultipleChoice,
            MaxChoices = isMultipleChoice ? Math.Max(2, maxChoices) : 1,
            ResultVisibility = resultVisibility,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt?.ToUniversalTime(),
            Status = initialStatus,
            CategoryId = categoryId,
            Options = (optionItems != null && optionItems.Count >= 2)
                ? optionItems.Where(o => !string.IsNullOrWhiteSpace(o.Text)).Select((item, index) => new PollOption
                {
                    Text = item.Text.Trim(),
                    ImageUrl = string.IsNullOrWhiteSpace(item.ImageUrl) ? null : item.ImageUrl.Trim(),
                    Order = index + 1
                }).ToList()
                : cleanedOptions.Select((text, index) => new PollOption
                {
                    Text = text,
                    Order = index + 1
                }).ToList()
        };

        // Tags handling (up to MaxTagsPerPoll tags)
        if (tags != null && tags.Count > 0)
        {
            foreach (var tagName in NormalizeTagNames(tags))
            {
                var tag = await GetOrCreateTagAsync(context, tagName);
                tag.UsageCount++;
                poll.PollTags.Add(new PollTag { Tag = tag });
            }
        }

        context.Polls.Add(poll);
        await context.SaveChangesAsync();

        return (true, resultMsg, poll.Id);
    }

    public async Task<(bool Success, string Message)> UpdateAndResubmitPollAsync(
        int pollId,
        string userId,
        string title,
        string? description,
        List<string> optionTexts,
        int? categoryId = null,
        List<string>? tags = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return (false, "Poll title cannot be empty.");
        }

        var cleanedOptions = optionTexts
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .ToList();

        var validationError = ValidatePollContent(title, description, cleanedOptions);
        if (validationError != null)
        {
            return (false, validationError);
        }

        await using var context = await dbContextFactory.CreateDbContextAsync();

        var poll = await context.Polls
            .Include(p => p.Options)
            .Include(p => p.PollTags).ThenInclude(pt => pt.Tag)
            .Include(p => p.Votes)
            .FirstOrDefaultAsync(p => p.Id == pollId);

        if (poll == null) return (false, "Poll not found.");

        if (poll.CreatorId != userId)
        {
            return (false, "You do not have permission to modify this poll.");
        }

        if (poll.Status != PollStatus.ReturnedForRevision)
        {
            return (false, "Only polls returned for revision can be edited and resubmitted.");
        }

        // Refuse to touch a poll that already has votes. Resubmitting rewrites the option rows, and
        // VoteRecord is configured with cascade delete on PollOption (ApplicationDbContext), so
        // recreating them used to silently destroy every existing vote. Voting is only reachable
        // while a poll is Approved, so this should not normally happen — guard it explicitly rather
        // than rely on that.
        if (poll.Votes.Count > 0)
        {
            return (false,
                $"This poll already has {poll.Votes.Count} vote(s) and can no longer be revised, " +
                "because changing its options would invalidate them. Please create a new poll instead.");
        }

        poll.Title = title.Trim();
        poll.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        poll.Status = PollStatus.PendingReview;
        poll.ModerationReason = null; // Clear rejection reason

        // Re-check the board permission: moving a poll into an AdminOnly/ModeratorOnly board must be
        // subject to the same rule CreatePollAsync enforces.
        if (categoryId is > 0)
        {
            var editor = await userManager.FindByIdAsync(userId);
            var (editorIsAdmin, editorIsAdminOrMod) = await GetModerationFlagsAsync(editor);
            var categoryCheck = await CanPostInCategoryAsync(context, categoryId, editorIsAdmin, editorIsAdminOrMod, userId, editor);
            if (!categoryCheck.Allowed) return (false, categoryCheck.Message);
        }
        if (categoryId.HasValue) poll.CategoryId = categoryId.Value;

        // Safe now that votes are ruled out above: replace the option rows.
        context.PollOptions.RemoveRange(poll.Options);
        poll.Options = cleanedOptions.Select((t, i) => new PollOption
        {
            PollId = poll.Id,
            Text = t,
            Order = i + 1
        }).ToList();

        // Update tags (only added/removed tags adjust UsageCount)
        if (tags != null)
        {
            await ApplyTagsAsync(context, poll, NormalizeTagNames(tags));
        }

        await context.SaveChangesAsync();
        return (true, "Poll updated and resubmitted for moderator review!");
    }

    private async Task<bool> CanModerateAsync(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        var user = await userManager.FindByIdAsync(userId);
        if (user == null) return false;
        return user.IsRootAdmin
            || await userManager.IsInRoleAsync(user, "Admin")
            || await userManager.IsInRoleAsync(user, "SuperModerator")
            || await userManager.IsInRoleAsync(user, "Moderator");
    }

    private async Task<bool> IsAdminAsync(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        var user = await userManager.FindByIdAsync(userId);
        if (user == null) return false;
        return user.IsRootAdmin || await userManager.IsInRoleAsync(user, "Admin");
    }

    public async Task<(bool Success, string Message)> ApprovePollAsync(int pollId, string moderatorId)
    {
        if (!await CanModerateAsync(moderatorId))
        {
            return (false, "You do not have permission to moderate polls.");
        }

        await using var context = await dbContextFactory.CreateDbContextAsync();

        var poll = await context.Polls.Include(p => p.Creator).FirstOrDefaultAsync(p => p.Id == pollId);
        if (poll == null) return (false, "Poll not found.");

        // State machine guard: only a poll awaiting review (or one returned to its author) can be
        // approved. Without this an already Removed poll could be re-published simply by approving it.
        if (poll.Status != PollStatus.PendingReview && poll.Status != PollStatus.ReturnedForRevision)
        {
            return (false, $"Only polls pending review or returned for revision can be approved. This poll is currently {poll.Status}.");
        }

        poll.Status = PollStatus.Approved;
        poll.ModerationReason = null;
        await context.SaveChangesAsync();
        pollEventNotifier?.NotifyPollUpdated(pollId);

        if (poll.Creator != null && !string.IsNullOrEmpty(poll.Creator.Email))
        {
            await emailNotificationService.NotifyPollApprovedAsync(poll, poll.Creator.Email);
        }

        if (notificationService != null && !string.IsNullOrEmpty(poll.CreatorId))
        {
            _ = notificationService.CreateNotificationAsync(
                poll.CreatorId,
                NotificationType.PollApproved,
                "Poll Approved",
                $"Your poll '{poll.Title}' has been approved and is now public.",
                $"/poll/{poll.Id}");
        }

        return (true, "Poll approved successfully and is now public.");
    }

    public async Task<(bool Success, string Message)> ReturnPollForRevisionAsync(int pollId, string moderatorId, string reason)
    {
        if (!await CanModerateAsync(moderatorId))
        {
            return (false, "You do not have permission to moderate polls.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return (false, "A reason must be provided to the author explaining what needs to be changed.");
        }

        await using var context = await dbContextFactory.CreateDbContextAsync();

        var poll = await context.Polls
            .Include(p => p.Creator)
            .Include(p => p.Votes)
            .FirstOrDefaultAsync(p => p.Id == pollId);
        if (poll == null) return (false, "Poll not found.");

        // State machine guard: reverting is only meaningful for a pending or already approved poll.
        if (poll.Status != PollStatus.PendingReview && poll.Status != PollStatus.Approved)
        {
            return (false, $"Only polls pending review or approved can be returned for revision. This poll is currently {poll.Status}.");
        }

        // A poll that already has votes cannot be returned for revision: the author's resubmit
        // rewrites the options, which cascade-deletes the vote records. UpdatePollAsync and
        // WithdrawToDraftAsync already refuse this; moderation must too.
        if (poll.Votes.Count > 0)
        {
            return (false,
                $"This poll already has {poll.Votes.Count} vote(s), so returning it for revision would " +
                "invalidate them. Remove or archive it instead if it must come down.");
        }

        poll.Status = PollStatus.ReturnedForRevision;
        poll.ModerationReason = reason.Trim();
        await context.SaveChangesAsync();
        pollEventNotifier?.NotifyPollUpdated(pollId);

        if (poll.Creator != null && !string.IsNullOrEmpty(poll.Creator.Email))
        {
            await emailNotificationService.NotifyPollReturnedForRevisionAsync(poll, poll.Creator.Email, reason.Trim());
        }

        if (notificationService != null && !string.IsNullOrEmpty(poll.CreatorId))
        {
            _ = notificationService.CreateNotificationAsync(
                poll.CreatorId,
                NotificationType.PollReturned,
                "Poll Needs Revision",
                $"Your poll '{poll.Title}' needs changes: {reason.Trim()}",
                $"/poll/{poll.Id}");
        }

        return (true, "Poll returned for revision. Author has been notified.");
    }

    public async Task<(bool Success, string Message)> RemovePollAsync(int pollId, string moderatorId, string reason)
    {
        if (!await CanModerateAsync(moderatorId))
        {
            return (false, "You do not have permission to moderate polls.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return (false, "A removal reason must be provided.");
        }

        await using var context = await dbContextFactory.CreateDbContextAsync();

        var poll = await context.Polls.Include(p => p.Creator).FirstOrDefaultAsync(p => p.Id == pollId);
        if (poll == null) return (false, "Poll not found.");

        poll.Status = PollStatus.Removed;
        poll.ModerationReason = reason.Trim();
        await context.SaveChangesAsync();
        pollEventNotifier?.NotifyPollUpdated(pollId);

        if (poll.Creator != null && !string.IsNullOrEmpty(poll.Creator.Email))
        {
            await emailNotificationService.NotifyPollRemovedAsync(poll, poll.Creator.Email, reason.Trim());
        }

        if (notificationService != null && !string.IsNullOrEmpty(poll.CreatorId))
        {
            _ = notificationService.CreateNotificationAsync(
                poll.CreatorId,
                NotificationType.PollRejected,
                "Poll Removed",
                $"Your poll '{poll.Title}' was removed: {reason.Trim()}",
                $"/poll/{poll.Id}");
        }

        return (true, "Poll removed. Hidden from public access.");
    }

    public async Task<(bool Success, string Message)> ArchivePollAsync(int pollId, string moderatorId, string? reason)
    {
        if (!await IsAdminAsync(moderatorId))
        {
            return (false, "Only administrators can archive polls.");
        }

        await using var context = await dbContextFactory.CreateDbContextAsync();

        var poll = await context.Polls.Include(p => p.Creator).FirstOrDefaultAsync(p => p.Id == pollId);
        if (poll == null) return (false, "Poll not found.");

        // State machine guard: archiving closes an already published poll. A Removed poll must stay
        // removed (archiving it would also rewrite its moderation reason).
        if (poll.Status != PollStatus.Approved)
        {
            return (false, $"Only approved polls can be archived. This poll is currently {poll.Status}.");
        }

        poll.Status = PollStatus.Archived;
        poll.ModerationReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        await context.SaveChangesAsync();
        pollEventNotifier?.NotifyPollUpdated(pollId);

        if (poll.Creator != null && !string.IsNullOrEmpty(poll.Creator.Email))
        {
            await emailNotificationService.NotifyPollArchivedAsync(poll, poll.Creator.Email, reason);
        }

        return (true, "Poll archived. Voting is now closed.");
    }

    public async Task<(bool Success, string Message, bool IsPinned)> TogglePinPollAsync(int pollId, string moderatorId)
    {
        if (!await IsAdminAsync(moderatorId))
        {
            return (false, "Only administrators can pin polls.", false);
        }

        await using var context = await dbContextFactory.CreateDbContextAsync();

        var poll = await context.Polls.Include(p => p.Creator).FirstOrDefaultAsync(p => p.Id == pollId);
        if (poll == null) return (false, "Poll not found.", false);

        // Removed content must not become a pinned/highlighted entry again.
        if (poll.Status == PollStatus.Removed)
        {
            return (false, "Removed polls cannot be pinned.", poll.IsPinned);
        }

        poll.IsPinned = !poll.IsPinned;
        await context.SaveChangesAsync();
        pollEventNotifier?.NotifyPollUpdated(pollId);

        if (poll.Creator != null && !string.IsNullOrEmpty(poll.Creator.Email))
        {
            await emailNotificationService.NotifyPollPinnedAsync(poll, poll.Creator.Email, poll.IsPinned);
        }

        return (true, poll.IsPinned ? "Poll pinned to top." : "Poll unpinned.", poll.IsPinned);
    }

    public async Task<(bool Success, string Message)> DeletePollAsync(int pollId, string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return (false, "You must be logged in to delete a poll.");
        }

        await using var context = await dbContextFactory.CreateDbContextAsync();

        var poll = await context.Polls
            .Include(p => p.PollTags).ThenInclude(pt => pt.Tag)
            .FirstOrDefaultAsync(p => p.Id == pollId);
        if (poll == null)
        {
            return (false, "Poll not found or already deleted.");
        }

        var user = await userManager.FindByIdAsync(userId);
        bool isAdmin = user != null && (await userManager.IsInRoleAsync(user, "Admin") || user.IsRootAdmin);

        if (poll.CreatorId != userId && !isAdmin)
        {
            return (false, "You do not have permission to delete this poll.");
        }

        // Keep UsageCount consistent with the tag rows that are about to cascade away.
        foreach (var pollTag in poll.PollTags)
        {
            if (pollTag.Tag != null)
            {
                pollTag.Tag.UsageCount = Math.Max(0, pollTag.Tag.UsageCount - 1);
            }
        }

        context.Polls.Remove(poll);
        await context.SaveChangesAsync();

        return (true, "Poll deleted successfully.");
    }

    // ==================== FEED PAGINATION & ADVANCED SEARCH ====================
    public async Task<PagedResult<PollListItemDto>> GetPollFeedAsync(
        int page = 1,
        int pageSize = 15,
        string? search = null,
        string? status = "all",
        string? sortBy = "latest",
        int? categoryId = null,
        string? tag = null,
        string? author = null,
        bool onlySubscribed = false,
        string? currentUserId = null)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 15;
        // Clamp the upper bound too: otherwise ?pageSize=100000000 made the anonymous path eager-load
        // the whole table (all the Include()s above) in a single request.
        if (pageSize > MaxFeedPageSize) pageSize = MaxFeedPageSize;

        await using var context = await dbContextFactory.CreateDbContextAsync();

        var query = context.Polls
            .Include(p => p.Creator)
            .Include(p => p.Category)
            .Include(p => p.PollTags).ThenInclude(pt => pt.Tag)
            .Include(p => p.Comments)
            .Include(p => p.Options)
            .Include(p => p.Votes)
            .Where(p => p.Status == PollStatus.Approved)
            .AsNoTracking();

        // Check author filter or @author syntax in search
        string? effectiveAuthor = author;
        string? effectiveSearch = search;
        if (!string.IsNullOrWhiteSpace(search) && search.Trim().StartsWith("@"))
        {
            effectiveAuthor = search.Trim()[1..].Trim();
            effectiveSearch = null;
        }

        if (!string.IsNullOrWhiteSpace(effectiveAuthor))
        {
            var a = effectiveAuthor.ToLower();
            query = query.Where(p => (p.Creator != null && p.Creator.UserName != null && p.Creator.UserName.ToLower().Contains(a)) ||
                                     (p.Creator != null && p.Creator.Email != null && p.Creator.Email.ToLower().Contains(a)));
        }

        if (!string.IsNullOrWhiteSpace(effectiveSearch))
        {
            var s = effectiveSearch.Trim().ToLower();
            query = query.Where(p => p.Title.ToLower().Contains(s) ||
                                     (p.Description != null && p.Description.ToLower().Contains(s)) ||
                                     p.PollTags.Any(pt => pt.Tag != null && pt.Tag.Name.ToLower().Contains(s)));
        }

        if (categoryId.HasValue)
        {
            var allCats = await context.Categories.AsNoTracking().Select(c => new { c.Id, c.ParentId }).ToListAsync();
            var targetCatIds = new HashSet<int> { categoryId.Value };
            bool added;
            do
            {
                added = false;
                foreach (var c in allCats)
                {
                    if (c.ParentId.HasValue && targetCatIds.Contains(c.ParentId.Value) && !targetCatIds.Contains(c.Id))
                    {
                        targetCatIds.Add(c.Id);
                        added = true;
                    }
                }
            } while (added);

            query = query.Where(p => p.CategoryId != null && targetCatIds.Contains(p.CategoryId.Value));
        }

        if (onlySubscribed && !string.IsNullOrEmpty(currentUserId))
        {
            var subscribedCatIds = await context.CategorySubscriptions
                .Where(cs => cs.UserId == currentUserId)
                .Select(cs => cs.CategoryId)
                .ToListAsync();

            query = query.Where(p => p.CategoryId != null && subscribedCatIds.Contains(p.CategoryId.Value));
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            var cleanTag = tag.Trim().ToLower();
            query = query.Where(p => p.PollTags.Any(pt => pt.Tag != null && pt.Tag.Name.ToLower() == cleanTag));
        }

        var now = DateTime.UtcNow;
        if (status == "active")
        {
            query = query.Where(p => p.ExpiresAt == null || p.ExpiresAt > now);
        }
        else if (status == "expired")
        {
            query = query.Where(p => p.ExpiresAt != null && p.ExpiresAt <= now);
        }

        var totalCount = await query.CountAsync();

        // Order query
        IQueryable<Poll> orderedQuery;
        if (sortBy == "popular")
        {
            orderedQuery = query.OrderByDescending(p => p.IsPinned)
                                .ThenByDescending(p => p.Votes.Count)
                                .ThenByDescending(p => p.CreatedAt);
        }
        else
        {
            orderedQuery = query.OrderByDescending(p => p.IsPinned)
                                .ThenByDescending(p => p.CreatedAt);
        }

        var polls = await orderedQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var items = polls.Select(p =>
        {
            var participantCount = p.Votes
                .Select(v => !string.IsNullOrEmpty(v.UserId) ? v.UserId : v.IpAddress)
                .Distinct()
                .Count();

            return new PollListItemDto
            {
                Id = p.Id,
                Title = p.Title,
                Description = p.Description,
                CreatorId = p.CreatorId,
                CreatorName = FormatDisplayName(p.Creator?.UserName, p.Creator?.Email),
                RequireLogin = p.RequireLogin,
                IsMultipleChoice = p.IsMultipleChoice,
                ResultVisibility = (PdnodeVote.Client.Models.ResultVisibility)(int)p.ResultVisibility,
                CreatedAt = p.CreatedAt,
                ExpiresAt = p.ExpiresAt,
                ParticipantCount = participantCount,
                OptionCount = p.Options.Count,
                Status = (PdnodeVote.Client.Models.PollStatus)(int)p.Status,
                ModerationReason = p.ModerationReason,
                IsPinned = p.IsPinned,
                CategoryId = p.CategoryId,
                CategoryName = p.Category?.Name,
                Tags = p.PollTags.Select(pt => pt.Tag?.Name ?? "").Where(t => !string.IsNullOrEmpty(t)).ToList(),
                CommentCount = p.Comments.Count(c => c.Status == PollStatus.Approved),
                HasImages = p.Options.Any(o => !string.IsNullOrEmpty(o.ImageUrl))
            };
        }).ToList();

        return new PagedResult<PollListItemDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    // ==================== USER ACTIVITY (VOTED & COMMENTED) ====================
    public async Task<PagedResult<UserVotedPollDto>> GetVotedPollsByUserAsync(string userId, int page = 1, int pageSize = 15)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 15;

        await using var context = await dbContextFactory.CreateDbContextAsync();

        var userVotesQuery = context.VoteRecords
            .Where(v => v.UserId == userId)
            .GroupBy(v => v.PollId)
            .Select(g => new
            {
                PollId = g.Key,
                LastVotedAt = g.Max(v => v.VotedAt)
            });

        var totalCount = await userVotesQuery.CountAsync();

        var pagedVoteSummaries = await userVotesQuery
            .OrderByDescending(v => v.LastVotedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var pollIds = pagedVoteSummaries.Select(s => s.PollId).ToList();

        var polls = await context.Polls
            .Include(p => p.Creator)
            .Include(p => p.Options)
            .Include(p => p.Votes)
            .Where(p => pollIds.Contains(p.Id))
            .AsNoTracking()
            .ToListAsync();

        var pollMap = polls.ToDictionary(p => p.Id);

        var items = new List<UserVotedPollDto>();
        foreach (var summary in pagedVoteSummaries)
        {
            if (pollMap.TryGetValue(summary.PollId, out var p))
            {
                var userSelectedOptionIds = p.Votes
                    .Where(v => v.UserId == userId)
                    .Select(v => v.PollOptionId)
                    .Distinct()
                    .ToHashSet();

                var selectedTexts = p.Options
                    .Where(o => userSelectedOptionIds.Contains(o.Id))
                    .Select(o => o.Text)
                    .ToList();

                var participantCount = p.Votes
                    .Select(v => !string.IsNullOrEmpty(v.UserId) ? v.UserId : v.IpAddress)
                    .Distinct()
                    .Count();

                items.Add(new UserVotedPollDto
                {
                    PollId = p.Id,
                    Title = p.Title,
                    CreatorName = FormatDisplayName(p.Creator?.UserName, p.Creator?.Email),
                    VotedAt = summary.LastVotedAt,
                    SelectedOptionTexts = selectedTexts,
                    ParticipantCount = participantCount,
                    Status = (PdnodeVote.Client.Models.PollStatus)(int)p.Status
                });
            }
        }

        return new PagedResult<UserVotedPollDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<PagedResult<UserCommentedPollDto>> GetCommentedPollsByUserAsync(string userId, int page = 1, int pageSize = 15)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 15;

        await using var context = await dbContextFactory.CreateDbContextAsync();

        var commentsQuery = context.PollComments
            .Where(c => c.UserId == userId && c.Status != PollStatus.Removed)
            .GroupBy(c => c.PollId)
            .Select(g => new
            {
                PollId = g.Key,
                LastCommentedAt = g.Max(c => c.CreatedAt),
                TotalComments = g.Count()
            });

        var totalCount = await commentsQuery.CountAsync();

        var pagedSummaries = await commentsQuery
            .OrderByDescending(c => c.LastCommentedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var pollIds = pagedSummaries.Select(s => s.PollId).ToList();

        var polls = await context.Polls
            .Include(p => p.Creator)
            .Include(p => p.Comments)
            .Where(p => pollIds.Contains(p.Id))
            .AsNoTracking()
            .ToListAsync();

        var pollMap = polls.ToDictionary(p => p.Id);

        var items = new List<UserCommentedPollDto>();
        foreach (var summary in pagedSummaries)
        {
            if (pollMap.TryGetValue(summary.PollId, out var p))
            {
                var lastComment = p.Comments
                    .Where(c => c.UserId == userId && c.Status != PollStatus.Removed)
                    .OrderByDescending(c => c.CreatedAt)
                    .FirstOrDefault();

                items.Add(new UserCommentedPollDto
                {
                    PollId = p.Id,
                    Title = p.Title,
                    CreatorName = FormatDisplayName(p.Creator?.UserName, p.Creator?.Email),
                    LastCommentedAt = summary.LastCommentedAt,
                    LastCommentSnippet = lastComment?.Content ?? "",
                    TotalUserCommentsInPoll = summary.TotalComments
                });
            }
        }

        return new PagedResult<UserCommentedPollDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    // ==================== PUBLIC USER PROFILE ====================
    public async Task<UserProfileDto?> GetUserPublicProfileAsync(string username, string? currentUserId = null)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;

        await using var context = await dbContextFactory.CreateDbContextAsync();

        var clean = username.Trim().ToLower();
        var user = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => (u.UserName != null && u.UserName.ToLower() == clean) ||
                                      (u.Email != null && u.Email.ToLower() == clean));

        if (user == null) return null;

        var roles = await userManager.GetRolesAsync(user);
        string? roleBadge = null;
        if (user.IsRootAdmin || roles.Contains("Admin")) roleBadge = "Admin";
        else if (roles.Contains("SuperModerator")) roleBadge = "SuperModerator";
        else if (roles.Contains("Moderator")) roleBadge = "Moderator";

        // Count approved created polls
        var createdPolls = await context.Polls
            .Include(p => p.Category)
            .Include(p => p.PollTags).ThenInclude(pt => pt.Tag)
            .Include(p => p.Comments)
            .Include(p => p.Options)
            .Include(p => p.Votes)
            .Where(p => p.CreatorId == user.Id && p.Status == PollStatus.Approved)
            .OrderByDescending(p => p.CreatedAt)
            .AsNoTracking()
            .ToListAsync();

        var totalVotesReceived = createdPolls.Sum(p => p.Votes.Count);

        var recentPolls = createdPolls.Take(10).Select(p =>
        {
            var participantCount = p.Votes
                .Select(v => !string.IsNullOrEmpty(v.UserId) ? v.UserId : v.IpAddress)
                .Distinct()
                .Count();

            return new PollListItemDto
            {
                Id = p.Id,
                Title = p.Title,
                Description = p.Description,
                CreatorId = p.CreatorId,
                CreatorName = FormatDisplayName(user.UserName, user.Email),
                RequireLogin = p.RequireLogin,
                IsMultipleChoice = p.IsMultipleChoice,
                ResultVisibility = (PdnodeVote.Client.Models.ResultVisibility)(int)p.ResultVisibility,
                CreatedAt = p.CreatedAt,
                ExpiresAt = p.ExpiresAt,
                ParticipantCount = participantCount,
                OptionCount = p.Options.Count,
                Status = (PdnodeVote.Client.Models.PollStatus)(int)p.Status,
                CategoryId = p.CategoryId,
                CategoryName = p.Category?.Name,
                Tags = p.PollTags.Select(pt => pt.Tag?.Name ?? "").Where(t => !string.IsNullOrEmpty(t)).ToList(),
                CommentCount = p.Comments.Count(c => c.Status == PollStatus.Approved)
            };
        }).ToList();

        // Comments count
        var commentsCount = await context.PollComments.CountAsync(c => c.UserId == user.Id && c.Status == PollStatus.Approved);

        var recentComments = await context.PollComments
            .Include(c => c.Poll)
            .Where(c => c.UserId == user.Id && c.Status == PollStatus.Approved)
            .OrderByDescending(c => c.CreatedAt)
            .Take(10)
            .Select(c => new UserCommentSummaryDto
            {
                Id = c.Id,
                PollId = c.PollId,
                PollTitle = c.Poll != null ? c.Poll.Title : "",
                Content = c.Content,
                CreatedAt = c.CreatedAt
            })
            .ToListAsync();

        int userVoteCount = await context.VoteRecords.CountAsync(v => v.UserId == user.Id);
        int userLevel = UserLevelHelper.CalculateLevel(user.CreatedAt, userVoteCount, createdPolls.Count, totalVotesReceived);

        return new UserProfileDto
        {
            UserId = user.Id,
            UserName = user.UserName ?? "User",
            DisplayName = FormatDisplayName(user.UserName, user.Email),
            RoleBadge = roleBadge,
            UserLevel = userLevel,
            JoinedAt = user.CreatedAt,
            CreatedPollsCount = createdPolls.Count,
            TotalVotesReceived = totalVotesReceived,
            CommentsCount = commentsCount,
            RecentPolls = recentPolls,
            RecentComments = recentComments
        };
    }

    // ==================== BOARD SUBSCRIPTION / FAVORITES ====================
    public async Task<ServiceResult> ToggleCategorySubscriptionAsync(string userId, int categoryId)
    {
        if (string.IsNullOrEmpty(userId)) return ServiceResult.Fail("Unauthorized");

        await using var context = await dbContextFactory.CreateDbContextAsync();

        var existing = await context.CategorySubscriptions
            .FirstOrDefaultAsync(cs => cs.UserId == userId && cs.CategoryId == categoryId);

        if (existing != null)
        {
            context.CategorySubscriptions.Remove(existing);
            await context.SaveChangesAsync();
            return ServiceResult.Ok("Board unfollowed.");
        }
        else
        {
            context.CategorySubscriptions.Add(new CategorySubscription
            {
                UserId = userId,
                CategoryId = categoryId,
                SubscribedAt = DateTime.UtcNow
            });

            try
            {
                await context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.IsUniqueConstraintViolation("CategorySubscriptions"))
            {
                // (UserId, CategoryId) is the primary key; a concurrent toggle already created the row,
                // so the requested state is satisfied — treat it as success rather than a 500.
                return ServiceResult.Ok("Board followed!");
            }

            return ServiceResult.Ok("Board followed!");
        }
    }

    public async Task<List<int>> GetSubscribedCategoryIdsAsync(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return new List<int>();

        await using var context = await dbContextFactory.CreateDbContextAsync();
        return await context.CategorySubscriptions
            .Where(cs => cs.UserId == userId)
            .Select(cs => cs.CategoryId)
            .ToListAsync();
    }

    // ==================== SAFE IN-PLACE POLL UPDATE & WITHDRAW ====================
    public async Task<ServiceResult> UpdatePollAsync(int pollId, string userId, UpdatePollRequest request)
    {
        if (string.IsNullOrEmpty(userId)) return ServiceResult.Fail("Unauthorized");

        await using var context = await dbContextFactory.CreateDbContextAsync();

        var poll = await context.Polls
            .Include(p => p.Options)
            .Include(p => p.Votes)
            .Include(p => p.PollTags).ThenInclude(pt => pt.Tag)
            .FirstOrDefaultAsync(p => p.Id == pollId);

        if (poll == null) return ServiceResult.Fail("Poll not found.");

        var user = await userManager.FindByIdAsync(userId);
        var (isAdmin, isAdminOrMod) = await GetModerationFlagsAsync(user);

        if (poll.CreatorId != userId && !isAdmin)
        {
            return ServiceResult.Fail("You do not have permission to edit this poll.");
        }

        // Server-side content limits for the fields this endpoint can change.
        if (!string.IsNullOrWhiteSpace(request.Title) && request.Title.Trim().Length > MaxTitleLength)
        {
            return ServiceResult.Fail($"Poll title cannot exceed {MaxTitleLength} characters.");
        }
        if (request.Description != null && request.Description.Trim().Length > MaxDescriptionLength)
        {
            return ServiceResult.Fail($"Poll description cannot exceed {MaxDescriptionLength} characters.");
        }
        if (request.Options != null)
        {
            var providedOptions = request.Options
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .Select(o => o.Trim())
                .ToList();
            if (providedOptions.Count > MaxOptionCount)
            {
                return ServiceResult.Fail($"A poll cannot have more than {MaxOptionCount} options.");
            }
            if (providedOptions.Any(o => o.Length > MaxOptionTextLength))
            {
                return ServiceResult.Fail($"Each option cannot exceed {MaxOptionTextLength} characters.");
            }
        }

        // A board change must satisfy the same PostPermission rule as creating a poll there.
        var targetCategoryId = request.CategoryId is > 0 ? request.CategoryId : null;
        if (targetCategoryId.HasValue)
        {
            var categoryCheck = await CanPostInCategoryAsync(context, targetCategoryId, isAdmin, isAdminOrMod, userId, user);
            if (!categoryCheck.Allowed) return ServiceResult.Fail(categoryCheck.Message);
        }

        int totalVotes = poll.Votes.Count;

        // If poll already has active votes:
        // Title and Options cannot be modified!
        if (totalVotes > 0)
        {
            if (!string.IsNullOrWhiteSpace(request.Title) && request.Title.Trim() != poll.Title)
            {
                return ServiceResult.Fail("Voting is already active. Title cannot be modified to ensure fairness. You can edit the description and tags.");
            }

            if (request.Options != null && request.Options.Count > 0)
            {
                var existingOptions = poll.Options.OrderBy(o => o.Order).Select(o => o.Text.Trim()).ToList();
                var newOptions = request.Options.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToList();
                if (!existingOptions.SequenceEqual(newOptions))
                {
                    return ServiceResult.Fail("Voting is already active. Options cannot be changed to prevent invalidating existing votes.");
                }
            }
        }
        else
        {
            // Zero votes: Title and Options can be updated!
            if (!string.IsNullOrWhiteSpace(request.Title))
            {
                poll.Title = request.Title.Trim();
            }

            if (request.Options != null && request.Options.Count >= 2)
            {
                var cleanOptions = request.Options
                    .Where(o => !string.IsNullOrWhiteSpace(o))
                    .Select(o => o.Trim())
                    .Distinct()
                    .ToList();

                if (cleanOptions.Count >= 2)
                {
                    context.PollOptions.RemoveRange(poll.Options);
                    poll.Options = cleanOptions.Select((text, index) => new PollOption
                    {
                        Text = text,
                        Order = index
                    }).ToList();
                }
            }
        }

        // Always allowed: Description, Category, Tags
        if (request.Description != null)
        {
            // Blank means "no description", matching CreatePollAsync.
            poll.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        }

        if (request.CategoryId.HasValue)
        {
            poll.CategoryId = request.CategoryId.Value > 0 ? request.CategoryId.Value : null;
        }

        // Update tags (only added/removed tags adjust UsageCount)
        if (request.Tags != null)
        {
            await ApplyTagsAsync(context, poll, NormalizeTagNames(request.Tags));
        }

        await context.SaveChangesAsync();
        pollEventNotifier?.NotifyPollUpdated(pollId);

        return ServiceResult.Ok("Poll updated successfully!");
    }

    public async Task<ServiceResult> WithdrawToDraftAsync(int pollId, string userId)
    {
        if (string.IsNullOrEmpty(userId)) return ServiceResult.Fail("Unauthorized");

        await using var context = await dbContextFactory.CreateDbContextAsync();

        var poll = await context.Polls.Include(p => p.Votes).FirstOrDefaultAsync(p => p.Id == pollId);
        if (poll == null) return ServiceResult.Fail("Poll not found.");

        var user = await userManager.FindByIdAsync(userId);
        bool isAdmin = user != null && (await userManager.IsInRoleAsync(user, "Admin") || user.IsRootAdmin);

        if (poll.CreatorId != userId && !isAdmin)
        {
            return ServiceResult.Fail("You do not have permission to withdraw this poll.");
        }

        if (poll.Votes.Count > 0)
        {
            return ServiceResult.Fail("This poll already has active votes and cannot be withdrawn to draft. You can safe-edit its description/tags instead.");
        }

        poll.Status = PollStatus.ReturnedForRevision;
        poll.ModerationReason = "Withdrawn by author to draft for editing.";
        await context.SaveChangesAsync();
        pollEventNotifier?.NotifyPollUpdated(pollId);

        return ServiceResult.Ok("Poll withdrawn to draft. You can now edit all fields and resubmit.");
    }
}
