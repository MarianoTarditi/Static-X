using Entity;
using DAL.Repositories.Models;

namespace DAL.Repositories.Interfaces;

public interface IGymRepository
{
    Task<Gimnasio> AddAsync(Gimnasio gym, CancellationToken cancellationToken);
    Task<GymPage> ListAsync(int offset, int pageSize, CancellationToken cancellationToken);
    Task<PendingAdministratorPage?> ListPendingAdministratorsAsync(int gymId, int offset, int pageSize, CancellationToken cancellationToken);
}
