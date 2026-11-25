using BLL.DTOs.Students;
using Entity;

namespace BLL.Services.Interfaces;

public interface IDashboardService
{
    Task<DashboardResponse> GetAsync(int gymId, AccountRole actorRole, CancellationToken cancellationToken = default);
}
