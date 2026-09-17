using PdnodeVote.Client.Models;
using PdnodeVote.Data;

namespace PdnodeVote.Services;

public interface ICategoryService
{
    Task<List<CategoryDto>> GetCategoryTreeAsync();
    Task<List<CategoryDto>> GetFlattenedCategoriesAsync();
    Task<CategoryDto?> GetCategoryByIdAsync(int id);
    Task<bool> CanUserPostInCategoryAsync(int? categoryId, string userId);

    /// <summary>
    /// Same rule as <see cref="CanUserPostInCategoryAsync(int?, string)"/> but reports *why* posting is
    /// refused so callers can keep their existing, more specific error messages.
    /// </summary>
    Task<CategoryPostDenial?> GetPostDenialAsync(int? categoryId, string userId);

    /// <summary>
    /// As <see cref="GetPostDenialAsync(int?, string)"/> but reuses moderation flags the caller has
    /// already resolved, avoiding duplicate role lookups on the poll write paths.
    /// </summary>
    Task<CategoryPostDenial?> GetPostDenialAsync(int? categoryId, string userId, bool isAdmin, bool isAdminOrMod);
    Task<ServiceResult> SubmitCategoryRequestAsync(string userId, SubmitCategoryRequest request);
    Task<List<CategoryRequestDto>> GetPendingCategoryRequestsAsync(string? currentUserId = null);
    Task<ServiceResult> ReviewCategoryRequestAsync(string reviewerId, int requestId, ReviewCategoryRequest request);
    Task<List<TagDto>> GetPopularTagsAsync(int count = 20);
}
