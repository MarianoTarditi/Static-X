using Entity;
using DAL.Repositories.Models;

namespace DAL.Repositories.Interfaces;

public interface IGymRepository
{
    Task<Gym> AddAsync(Gym gym, CancellationToken cancellationToken);
    Task<GymPage> ListAsync(int offset, int pageSize, CancellationToken cancellationToken);
    Task<PendingAdministratorPage?> ListPendingAdministratorsAsync(int gymId, int offset, int pageSize, CancellationToken cancellationToken);
}
