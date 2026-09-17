using PdnodeVote.Client.Models;

namespace PdnodeVote.Services;

public interface IReportService
{
    Task<ServiceResult> SubmitReportAsync(string reporterId, SubmitReportRequest request);
    Task<PagedResult<ContentReportDto>> GetPendingReportsAsync(int page = 1, int pageSize = 15);
    Task<ServiceResult> ResolveReportAsync(string moderatorId, int reportId, ResolveReportRequest request);
}
