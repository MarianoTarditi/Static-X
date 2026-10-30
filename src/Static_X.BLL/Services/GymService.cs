using AutoMapper;
using BLL.Services.Interfaces;
using BLL.DTOs.Gyms;
using DAL.Repositories.Interfaces;
using Entity;

namespace BLL;

// Valida el nombre del gimnasio y coordina su alta mediante el repositorio
/// <summary>Valida altas y consultas administrativas paginadas y devuelve representaciones públicas seguras.</summary>
public sealed class GymService : IGymService
{
    private readonly IGymRepository repository;
    private readonly IMapper mapper;

    /// <summary>Recibe el repositorio de gimnasios y el mapeador de sus respuestas de negocio.</summary>
    public GymService(IGymRepository repository, IMapper mapper)
    {
        this.repository = repository;
        this.mapper = mapper;
    }

    /// <summary>Rechaza nombres vacíos y devuelve el gimnasio creado sin exponer su entidad.</summary>
    public async Task<GymResponse> AddAsync(CreateGymRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name;
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A gym name is required.", nameof(name));

        var gym = await repository.AddAsync(new Gimnasio { Nombre = name.Trim() }, cancellationToken);
        return mapper.Map<GymResponse>(gym);
    }

    /// <summary>Valida los límites y devuelve una página de todos los gimnasios persistidos.</summary>
    public async Task<GymListResponse> ListAsync(GymListRequest request, CancellationToken cancellationToken = default)
    {
        var offset = ValidatePage(request);
        var result = await repository.ListAsync(offset, request.PageSize, cancellationToken);
        return new GymListResponse(mapper.Map<GymResponse[]>(result.Items), request.Page, request.PageSize, result.TotalCount);
    }

    /// <summary>Valida el gimnasio seleccionado y devuelve únicamente identificadores y correos de administradores pendientes.</summary>
    public async Task<PendingAdministratorListResponse?> ListPendingAdministratorsAsync(int gymId, GymListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gymId);
        var offset = ValidatePage(request);
        var result = await repository.ListPendingAdministratorsAsync(gymId, offset, request.PageSize, cancellationToken);
        return result is null ? null : new PendingAdministratorListResponse(
            mapper.Map<PendingAdministratorResponse[]>(result.Items), request.Page, request.PageSize, result.TotalCount);
    }

    /// <summary>Exige páginas positivas, tamaños acotados y desplazamientos representables sin desbordamiento.</summary>
    private static int ValidatePage(GymListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Page);
        if (request.PageSize < 1 || request.PageSize > 100)
            throw new ArgumentOutOfRangeException(nameof(request.PageSize));
        var offset = ((long)request.Page - 1) * request.PageSize;
        if (offset > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(request.Page));
        return (int)offset;
    }
}
