using DAL.Repositories.Interfaces;
using Entity;

namespace DAL.Repositories.Implementation;

public sealed class GymRepository(dbcontext context) : IGymRepository
{
    public async Task<Gimnasio> AddAsync(Gimnasio gym, CancellationToken cancellationToken)
    {
        await context.Gimnasios.AddAsync(gym, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        return gym;
    }
}
