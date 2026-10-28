using DAL.Repositories.Interfaces;
using Entity;

namespace DAL.Repositories.Implementation;

/// <summary>Persiste las altas de gimnasios mediante el contexto de datos.</summary>
public sealed class GymRepository(StaticXDbContext context) : IGymRepository
{
    /// <summary>Agrega el gimnasio al contexto, guarda los cambios y devuelve la entidad persistida.</summary>
    public async Task<Gimnasio> AddAsync(Gimnasio gym, CancellationToken cancellationToken)
    {
        await context.Gimnasios.AddAsync(gym, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        return gym;
    }
}
