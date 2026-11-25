using DAL.Repositories.Models;

namespace DAL.Repositories.Interfaces;

public interface IDashboardRepository
{
    Task<DashboardStudentCounts> GetStudentCountsAsync(int gymId, CancellationToken cancellationToken);
}
