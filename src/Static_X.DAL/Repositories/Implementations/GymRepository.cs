using DAL.Repositories.Interfaces;
using Entity;
using DAL.Repositories.Models;
using Microsoft.EntityFrameworkCore;

namespace DAL.Repositories.Implementation;

/// <summary>Persiste altas y consulta páginas de gimnasios y administradores pendientes sin modificar datos.</summary>
public sealed class GymRepository : IGymRepository
{
    private readonly StaticXDbContext context;

    /// <summary>Recibe el contexto de persistencia para altas y consultas administrativas.</summary>
    public GymRepository(StaticXDbContext context)
    {
        this.context = context;
    }

    /// <summary>Agrega el gimnasio al contexto, guarda los cambios y devuelve la entidad persistida.</summary>
    public async Task<Gimnasio> AddAsync(Gimnasio gym, CancellationToken cancellationToken)
    {
        await context.Gimnasios.AddAsync(gym, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        return gym;
    }

    /// <summary>Cuenta todos los gimnasios y obtiene una página ordenada por identificador sin seguimiento.</summary>
    public async Task<GymPage> ListAsync(int offset, int pageSize, CancellationToken cancellationToken)
    {
        var gyms = context.Gimnasios.AsNoTracking();
        var count = await gyms.CountAsync(cancellationToken);
        var items = await gyms.OrderBy(g => g.Id).Skip(offset).Take(pageSize).ToArrayAsync(cancellationToken);
        return new GymPage(items, count);
    }

    /// <summary>Consulta administradores elegibles para reemisión y distingue un gimnasio inexistente de una página vacía.</summary>
    public async Task<PendingAdministratorPage?> ListPendingAdministratorsAsync(int gymId, int offset, int pageSize,
        CancellationToken cancellationToken)
    {
        if (!await context.Gimnasios.AsNoTracking().AnyAsync(g => g.Id == gymId, cancellationToken))
            return null;
        var pending = context.Users.AsNoTracking().Where(u => u.GymId == gymId &&
            u.Role == AccountRole.GymAdmin && !u.EmailConfirmed && u.PasswordHash == null &&
            context.ProvisioningRequests.Any(r => r.GymId == gymId && r.AccountId == u.Id));
        var count = await pending.CountAsync(cancellationToken);
        var items = await pending.OrderBy(u => u.Id).Skip(offset).Take(pageSize)
            .Select(u => new Account(u.Id, u.GymId, u.Email!, u.Role)).ToArrayAsync(cancellationToken);
        return new PendingAdministratorPage(items, count);
    }
}
