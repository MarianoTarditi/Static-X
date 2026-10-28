using AutoMapper;
using BLL.DTOs.Gyms;
using Entity;

namespace BLL.Mappings;

/// <summary>Define la conversión de gimnasios persistidos a sus respuestas de negocio.</summary>
public sealed class GymProfile : Profile
{
    /// <summary>Configura los parámetros del constructor de la respuesta sin aplicar reglas de negocio.</summary>
    public GymProfile()
    {
        CreateMap<Gimnasio, GymResponse>()
            .ForCtorParam("id", options => options.MapFrom(source => source.Id))
            .ForCtorParam("nombre", options => options.MapFrom(source => source.Nombre));
    }
}
