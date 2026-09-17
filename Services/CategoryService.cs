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

            // Check section moderator mapping
            bool isAssignedMod = await context.CategoryModerators
                .AnyAsync(cm => cm.CategoryId == category.Id && cm.UserId == userId);

            // If no specific assignments exist for this category, any moderator can post
            bool hasSpecificMods = await context.CategoryModerators.AnyAsync(cm => cm.CategoryId == category.Id);
            return !hasSpecificMods || isAssignedMod;
        }

        return true;
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
            if (await context.Categories.AnyAsync(c => c.Slug == slug))
            {
                slug = $"{slug}-{DateTime.UtcNow.Ticks % 10000}";
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
            await context.SaveChangesAsync();

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
