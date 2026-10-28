using AutoMapper;
using BLL.Services.Interfaces;
using BLL.DTOs.Gyms;
using DAL.Repositories.Interfaces;
using Entity;

namespace BLL;

// Valida el nombre del gimnasio y coordina su alta mediante el repositorio
/// <summary>Valida los datos del gimnasio y devuelve su representación de negocio.</summary>
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
}
