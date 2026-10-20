using Entity;

namespace DAL.Repositories.Interfaces;

public interface IGymRepository
{
    Task<Gimnasio> AddAsync(Gimnasio gym, CancellationToken cancellationToken);
}
