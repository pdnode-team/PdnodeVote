using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PdnodeVote.Client.Models;
using PdnodeVote.Data;
using PollStatus = PdnodeVote.Data.PollStatus;

namespace PdnodeVote.Services;

public class CategoryService : ICategoryService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly INotificationService? _notificationService;

    public CategoryService(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        UserManager<ApplicationUser> userManager,
        INotificationService? notificationService = null)
    {
        _dbContextFactory = dbContextFactory;
        _userManager = userManager;
        _notificationService = notificationService;
    }

    public async Task<List<CategoryDto>> GetCategoryTreeAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var categories = await context.Categories
            .AsNoTracking()
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Description,
                c.Slug,
                c.ParentId,
                c.Depth,
                PostPermission = (int)c.PostPermission,
                c.IsSystem,
                PollCount = c.Polls.Count(p => p.Status == PollStatus.Approved)
            })
            .ToListAsync();

        var dtoList = categories.Select(c => new CategoryDto
        {
            Id = c.Id,
            Name = c.Name,
            Description = c.Description,
            Slug = c.Slug,
            ParentId = c.ParentId,
            Depth = c.Depth,
            PostPermission = c.PostPermission,
            IsSystem = c.IsSystem,
            PollCount = c.PollCount,
            Children = new()
        }).ToList();

        var lookup = dtoList.ToDictionary(c => c.Id);
        var roots = new List<CategoryDto>();

        foreach (var c in dtoList)
        {
            if (c.ParentId.HasValue && lookup.TryGetValue(c.ParentId.Value, out var parent))
            {
                parent.Children.Add(c);
            }
            else
            {
                roots.Add(c);
            }
        }

        return roots;
    }

    public async Task<List<CategoryDto>> GetFlattenedCategoriesAsync()
    {
        var roots = await GetCategoryTreeAsync();
        var flatList = new List<CategoryDto>();

        void Flatten(IEnumerable<CategoryDto> items)
        {
            foreach (var item in items)
            {
                flatList.Add(item);
                if (item.Children.Count > 0)
                {
                    Flatten(item.Children);
                }
            }
        }

        Flatten(roots);
        return flatList;
    }

    public async Task<CategoryDto?> GetCategoryByIdAsync(int id)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var c = await context.Categories
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id);

        if (c == null) return null;

        var pollCount = await context.Polls.CountAsync(p => p.CategoryId == id && p.Status == PollStatus.Approved);

        return new CategoryDto
        {
            Id = c.Id,
            Name = c.Name,
            Description = c.Description,
            Slug = c.Slug,
            ParentId = c.ParentId,
            Depth = c.Depth,
            PostPermission = (int)c.PostPermission,
            IsSystem = c.IsSystem,
            PollCount = pollCount
        };
    }

    /// <summary>
    /// Authoritative "may this user create a post in this board?" check.
    /// </summary>
    /// <remarks>
    /// Semantics, in order: a null board means "General" and is allowed; a board that does not exist is
    /// not; <see cref="CategoryPostPermission.Anyone"/> boards are open to everybody;
    /// <see cref="CategoryPostPermission.AdminOnly"/> requires the Admin role or the root admin; and
    /// <see cref="CategoryPostPermission.ModeratorOnly"/> requires Admin/SuperModerator, or a Moderator
    /// that is assigned to that board through <c>CategoryModerators</c> (a board without explicit
    /// assignments stays open to every moderator).
    ///
    /// TODO (L5): the moderation services still re-implement the AdminOnly/ModeratorOnly rule inline --
    /// <c>PollService.CanPostInCategoryAsync</c>, deliberately without the CategoryModerators lookup --
    /// so a board-level moderator assignment has no effect on the write path. The two rules only agree
    /// while no CategoryModerators row exists (nothing currently writes one). Converging the callers on
    /// this method is what makes board-level moderators effective; this variant is also the only one
    /// that verifies the caller account actually exists.
    /// </remarks>
    public async Task<bool> CanUserPostInCategoryAsync(int? categoryId, string userId)
    {
        if (!categoryId.HasValue) return true; // Default general

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var category = await context.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == categoryId.Value);
        if (category == null) return false;

        if (category.PostPermission == CategoryPostPermission.Anyone) return true;

        var user = await _userManager.FindByIdAsync(userId);
        if (user == null) return false;

        bool isAdmin = await _userManager.IsInRoleAsync(user, "Admin") || user.IsRootAdmin;
        if (isAdmin) return true;

        if (category.PostPermission == CategoryPostPermission.AdminOnly) return false;

        if (category.PostPermission == CategoryPostPermission.ModeratorOnly)
        {
            bool isSuperMod = await _userManager.IsInRoleAsync(user, "SuperModerator");
            if (isSuperMod) return true;

            bool isMod = await _userManager.IsInRoleAsync(user, "Moderator");
            if (!isMod) return false;

            return await IsAssignedModeratorAsync(context, category.Id, userId);
        }

        return true;
    }

    /// <summary>
    /// Explains a board-posting refusal; null means the post is allowed.
    /// </summary>
    /// <remarks>
    /// Callers that have already resolved the user's moderation flags (the poll write paths, which need
    /// them for other reasons) should use this overload instead of re-deriving them, so there is exactly
    /// one definition of the rule. See <see cref="CanUserPostInCategoryAsync(int?, string)"/> for the
    /// semantics.
    /// </remarks>
    public async Task<CategoryPostDenial?> GetPostDenialAsync(
        int? categoryId, string userId, bool isAdmin, bool isAdminOrMod)
    {
        if (!categoryId.HasValue) return null; // Default general

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var category = await context.Categories
            .AsNoTracking()
            .Select(c => new { c.Id, c.PostPermission })
            .FirstOrDefaultAsync(c => c.Id == categoryId.Value);

        return await EvaluateAsync(context, category?.Id, category?.PostPermission, userId, isAdmin, isAdminOrMod);
    }

    public async Task<CategoryPostDenial?> GetPostDenialAsync(int? categoryId, string userId)
    {
        if (!categoryId.HasValue) return null;

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var category = await context.Categories
            .AsNoTracking()
            .Select(c => new { c.Id, c.PostPermission })
            .FirstOrDefaultAsync(c => c.Id == categoryId.Value);

        if (category is null) return CategoryPostDenial.BoardNotFound;

        bool isAdmin = false;
        bool isAdminOrMod = false;
        var user = await _userManager.FindByIdAsync(userId);
        if (user is not null)
        {
            isAdmin = user.IsRootAdmin || await _userManager.IsInRoleAsync(user, "Admin");
            isAdminOrMod = isAdmin
                           || await _userManager.IsInRoleAsync(user, "SuperModerator")
                           || await _userManager.IsInRoleAsync(user, "Moderator");
        }

        return await EvaluateAsync(context, category.Id, category.PostPermission, userId, isAdmin, isAdminOrMod);
    }

    private async Task<CategoryPostDenial?> EvaluateAsync(
        ApplicationDbContext context,
        int? categoryId,
        CategoryPostPermission? postPermission,
        string userId,
        bool isAdmin,
        bool isAdminOrMod)
    {
        if (categoryId is null || postPermission is null) return CategoryPostDenial.BoardNotFound;
        if (postPermission == CategoryPostPermission.Anyone) return null;

        if (isAdmin) return null;
        if (postPermission == CategoryPostPermission.AdminOnly) return CategoryPostDenial.AdministratorsOnly;

        if (postPermission != CategoryPostPermission.ModeratorOnly) return null;

        // SuperModerator (or Admin, handled above) may always post here.
        var user = await _userManager.FindByIdAsync(userId);
        if (user is not null && user.IsRootAdmin) return null;
        if (user is not null && await _userManager.IsInRoleAsync(user, "SuperModerator")) return null;

        if (!isAdminOrMod) return CategoryPostDenial.ModeratorsOnly;

        return await IsAssignedModeratorAsync(context, categoryId.Value, userId)
            ? null
            : CategoryPostDenial.NotAssignedModerator;
    }

    /// <summary>
    /// A board without any explicit <c>CategoryModerators</c> assignment stays open to every moderator;
    /// once assignments exist, only the assigned ones qualify.
    /// </summary>
    private static async Task<bool> IsAssignedModeratorAsync(
        ApplicationDbContext context, int categoryId, string userId)
    {
        bool hasSpecificMods = await context.CategoryModerators.AnyAsync(cm => cm.CategoryId == categoryId);
        if (!hasSpecificMods) return true;

        return await context.CategoryModerators.AnyAsync(cm => cm.CategoryId == categoryId && cm.UserId == userId);
    }

    public async Task<ServiceResult> SubmitCategoryRequestAsync(string userId, SubmitCategoryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return ServiceResult.Fail("Board name cannot be empty.");
        }

        var trimmedName = request.Name.Trim();
        if (trimmedName.Length > 50)
        {
            return ServiceResult.Fail("Board name cannot exceed 50 characters.");
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var user = await context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null) return ServiceResult.Fail("User not found or unauthenticated.");
        if (user.IsCurrentlyBanned) return ServiceResult.Fail("Your account is suspended. You cannot request new boards.");

        // Validate parent if provided
        int newDepth = 0;
        if (request.ParentId.HasValue)
        {
            var parent = await context.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.ParentId.Value);
            if (parent == null)
            {
                return ServiceResult.Fail("The selected parent board does not exist.");
            }
            if (parent.Depth >= 3)
            {
                return ServiceResult.Fail("Maximum sub-board hierarchy reached (depth limit of 3). Cannot create deeper sub-boards.");
            }
            newDepth = parent.Depth + 1;
        }

        // Check duplicate name under the same parent
        bool nameExists = await context.Categories.AnyAsync(c => c.ParentId == request.ParentId && c.Name.ToLower() == trimmedName.ToLower());
        if (nameExists)
        {
            return ServiceResult.Fail("A board with this name already exists under this level. Please choose a different name.");
        }

        // Check duplicate pending request
        bool pendingExists = await context.CategoryRequests.AnyAsync(r =>
            r.Status == CategoryRequestStatus.Pending &&
            r.ParentId == request.ParentId &&
            r.Name.ToLower() == trimmedName.ToLower());

        if (pendingExists)
        {
            return ServiceResult.Fail("A pending request for this board name already exists. Please do not submit duplicates.");
        }

        var newRequest = new CategoryRequest
        {
            Name = trimmedName,
            Description = request.Description?.Trim() ?? string.Empty,
            ParentId = request.ParentId,
            ApplicantId = userId,
            Status = CategoryRequestStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        context.CategoryRequests.Add(newRequest);
        await context.SaveChangesAsync();

        return ServiceResult.Ok("Board request submitted successfully! Awaiting moderator or administrator review.");
    }

    public async Task<List<CategoryRequestDto>> GetPendingCategoryRequestsAsync(string? currentUserId = null)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var requests = await context.CategoryRequests
            .Include(r => r.Applicant)
            .Include(r => r.Parent)
            .Include(r => r.Reviews)
            .Where(r => r.Status == CategoryRequestStatus.Pending)
            .OrderByDescending(r => r.CreatedAt)
            .AsNoTracking()
            .ToListAsync();

        return requests.Select(r => new CategoryRequestDto
        {
            Id = r.Id,
            Name = r.Name,
            Description = r.Description,
            ParentId = r.ParentId,
            ParentName = r.Parent?.Name,
            ApplicantId = r.ApplicantId,
            ApplicantName = PollService.FormatDisplayName(r.Applicant?.UserName, r.Applicant?.Email),
            Status = (int)r.Status,
            RejectionReason = r.RejectionReason,
            CreatedAt = r.CreatedAt,
            ApprovalsCount = r.Reviews.Count(rv => rv.IsApproved),
            RejectionsCount = r.Reviews.Count(rv => !rv.IsApproved),
            HasReviewedByCurrentUser = !string.IsNullOrEmpty(currentUserId) && r.Reviews.Any(rv => rv.ReviewerId == currentUserId)
        }).ToList();
    }

    public async Task<ServiceResult> ReviewCategoryRequestAsync(string reviewerId, int requestId, ReviewCategoryRequest review)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        var reviewer = await _userManager.FindByIdAsync(reviewerId);
        if (reviewer == null) return ServiceResult.Fail("Reviewer not found or unauthenticated.");

        bool isAdmin = await _userManager.IsInRoleAsync(reviewer, "Admin") || reviewer.IsRootAdmin;
        bool isSuperMod = await _userManager.IsInRoleAsync(reviewer, "SuperModerator");
        bool isMod = await _userManager.IsInRoleAsync(reviewer, "Moderator");

        if (!isAdmin && !isSuperMod && !isMod)
        {
            return ServiceResult.Fail("You do not have permission to review board requests.");
        }

        var req = await context.CategoryRequests
            .Include(r => r.Reviews)
            .Include(r => r.Parent)
            .FirstOrDefaultAsync(r => r.Id == requestId);

        if (req == null) return ServiceResult.Fail("Board request not found.");
        if (req.Status != CategoryRequestStatus.Pending) return ServiceResult.Fail("This request has already been processed.");

        if (req.Reviews.Any(rv => rv.ReviewerId == reviewerId))
        {
            return ServiceResult.Fail("You have already reviewed this request.");
        }

        // Add review record
        var reviewRecord = new CategoryRequestReview
        {
            RequestId = requestId,
            ReviewerId = reviewerId,
            IsApproved = review.Approve,
            Comment = review.Comment?.Trim(),
            ReviewedAt = DateTime.UtcNow
        };
        req.Reviews.Add(reviewRecord);

        // The "already reviewed" check above is a read-then-write: two concurrent reviews by the same
        // reviewer could both pass it. (RequestId, ReviewerId) is unique, so handle that race here
        // instead of letting the unique-index violation surface as an HTTP 500.
        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueConstraintViolation("CategoryRequestReviews"))
        {
            return ServiceResult.Fail("You have already reviewed this request.");
        }

        if (!review.Approve)
        {
            // Any rejection rejects the request
            req.Status = CategoryRequestStatus.Rejected;
            req.RejectionReason = review.Comment?.Trim() ?? "Rejected by reviewer";
            await context.SaveChangesAsync();

            if (_notificationService != null && !string.IsNullOrEmpty(req.ApplicantId))
            {
                _ = _notificationService.CreateNotificationAsync(
                    req.ApplicantId,
                    NotificationType.CategoryRequestRejected,
                    "Board Request Rejected",
                    $"Your request for board '{req.Name}' was rejected: {req.RejectionReason}");
            }

            return ServiceResult.Ok("Board request rejected.");
        }

        // It is an approval
        // Check if reviewer is Admin / SuperModerator (1 approval is sufficient)
        // Or if total approvals by mods >= 2
        bool shouldCreateCategory = false;

        if (isAdmin || isSuperMod)
        {
            shouldCreateCategory = true;
        }
        else
        {
            var approvalCount = req.Reviews.Count(rv => rv.IsApproved);
            if (approvalCount >= 2)
            {
                shouldCreateCategory = true;
            }
        }

        if (shouldCreateCategory)
        {
            req.Status = CategoryRequestStatus.Approved;

            int depth = req.Parent != null ? req.Parent.Depth + 1 : 0;
            if (depth > 3) depth = 3;

            var slug = GenerateSlug(req.Name);
            var baseSlug = slug;

            // Categories.Slug is unique. The previous "Ticks % 10000" fallback was a single guess that
            // could collide again — which, with the unique index in place, now surfaces as a 500.
            // Probe deterministically for the first free suffix instead.
            for (var suffix = 2; await context.Categories.AnyAsync(c => c.Slug == slug); suffix++)
            {
                if (suffix > 100)
                {
                    return ServiceResult.Fail("A board with a very similar name already exists. Please choose a different board name.");
                }

                slug = $"{baseSlug}-{suffix}";
            }

            var newCategory = new Category
            {
                Name = req.Name,
                Description = req.Description,
                Slug = slug,
                ParentId = req.ParentId,
                Depth = depth,
                PostPermission = CategoryPostPermission.Anyone,
                IsSystem = false,
                CreatedAt = DateTime.UtcNow
            };

            context.Categories.Add(newCategory);

            // The slug probe above is still a read-then-insert race: a concurrent approval can claim the
            // same slug in between. The unique index turns that into a constraint violation, which must
            // be reported as a normal failure rather than bubbling up as an HTTP 500.
            try
            {
                await context.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.IsUniqueConstraintViolation("Categories"))
            {
                return ServiceResult.Fail("A board with a similar name was just created. Please choose a different board name.");
            }

            if (_notificationService != null && !string.IsNullOrEmpty(req.ApplicantId))
            {
                _ = _notificationService.CreateNotificationAsync(
                    req.ApplicantId,
                    NotificationType.CategoryRequestApproved,
                    "Board Request Approved!",
                    $"Your request for board '{req.Name}' has been approved and created!",
                    $"/");
            }

            return ServiceResult.Ok("Board request approved and category created successfully!");
        }

        await context.SaveChangesAsync();
        return ServiceResult.Ok("Approval recorded. Awaiting second moderator review (2 moderator approvals or 1 admin approval required).");
    }

    public async Task<List<TagDto>> GetPopularTagsAsync(int count = 20)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        return await context.Tags
            .AsNoTracking()
            .OrderByDescending(t => t.UsageCount)
            .Take(count)
            .Select(t => new TagDto
            {
                Id = t.Id,
                Name = t.Name,
                UsageCount = t.UsageCount
            })
            .ToListAsync();
    }

    private static string GenerateSlug(string text)
    {
        var slug = text.Trim().ToLowerInvariant();
        slug = Regex.Replace(slug, @"\s+", "-");
        slug = Regex.Replace(slug, @"[^\w\-一-龥]", "");
        return string.IsNullOrWhiteSpace(slug) ? "category" : slug;
    }
}
