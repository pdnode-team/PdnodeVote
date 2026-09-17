using PdnodeVote.Client.Models;
using PdnodeVote.Data;

namespace PdnodeVote.Services;

public interface ICategoryService
{
    Task<List<CategoryDto>> GetCategoryTreeAsync();
    Task<List<CategoryDto>> GetFlattenedCategoriesAsync();
    Task<CategoryDto?> GetCategoryByIdAsync(int id);
    Task<bool> CanUserPostInCategoryAsync(int? categoryId, string userId);
    Task<ServiceResult> SubmitCategoryRequestAsync(string userId, SubmitCategoryRequest request);
    Task<List<CategoryRequestDto>> GetPendingCategoryRequestsAsync(string? currentUserId = null);
    Task<ServiceResult> ReviewCategoryRequestAsync(string reviewerId, int requestId, ReviewCategoryRequest request);
    Task<List<TagDto>> GetPopularTagsAsync(int count = 20);
}
