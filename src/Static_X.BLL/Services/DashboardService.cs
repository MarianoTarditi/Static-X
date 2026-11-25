using AutoMapper;
using BLL.DTOs.Students;
using BLL.Services.Interfaces;
using DAL.Repositories.Interfaces;
using Entity;

namespace BLL;

/// <summary>Autoriza la consulta del panel y convierte los conteos persistidos en respuestas sin datos personales.</summary>
public sealed class DashboardService : IDashboardService
{
    private readonly IDashboardRepository repository;
    private readonly IMapper mapper;

    /// <summary>Recibe el repositorio de lectura y el mapeador de respuestas del panel.</summary>
    public DashboardService(IDashboardRepository repository, IMapper mapper)
    {
        this.repository = repository;
        this.mapper = mapper;
    }

    /// <summary>Valida el rol y el gimnasio confiables antes de consultar los conteos de fichas.</summary>
    public async Task<DashboardResponse> GetAsync(int gymId, AccountRole actorRole, CancellationToken cancellationToken = default)
    {
        if (actorRole != AccountRole.GymAdmin || gymId <= 0)
            throw new UnauthorizedAccessException("Dashboard access requires a gym administrator and a valid gym.");
        cancellationToken.ThrowIfCancellationRequested();
        var counts = await repository.GetStudentCountsAsync(gymId, cancellationToken);
        return new DashboardResponse(mapper.Map<DashboardStudentsResponse>(counts));
    }
}
