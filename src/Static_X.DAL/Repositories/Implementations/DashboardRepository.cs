using DAL.Repositories.Interfaces;
using DAL.Repositories.Models;
using Microsoft.EntityFrameworkCore;

namespace DAL.Repositories.Implementation;

/// <summary>Consulta los conteos del panel mediante agregación de fichas acotada por gimnasio.</summary>
public sealed class DashboardRepository : IDashboardRepository
{
    private readonly StaticXDbContext context;

    /// <summary>Recibe el contexto utilizado para agregar las fichas persistidas.</summary>
    public DashboardRepository(StaticXDbContext context)
    {
        this.context = context;
    }

    /// <summary>Obtiene total, activos, inactivos y activos sin cuenta en una sola consulta y devuelve ceros si no existen fichas.</summary>
    public async Task<DashboardStudentCounts> GetStudentCountsAsync(int gymId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gymId);
        return await context.Alumnos.AsNoTracking()
            .Where(student => student.GymId == gymId)
            .GroupBy(student => student.GymId)
            .Select(group => new DashboardStudentCounts(group.Count(), group.Count(student => student.IsActive),
                group.Count(student => !student.IsActive),
                group.Count(student => student.IsActive && student.AccountId == null)))
            .SingleOrDefaultAsync(cancellationToken) ?? new DashboardStudentCounts(0, 0, 0, 0);
    }
}
