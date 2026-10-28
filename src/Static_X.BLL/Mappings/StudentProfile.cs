using AutoMapper;
using BLL.DTOs.Students;
using Entity;

namespace BLL.Mappings;

/// <summary>Define la conversión de alumnos persistidos a sus fichas públicas.</summary>
public sealed class StudentProfile : Profile
{
    /// <summary>Configura los parámetros del constructor de la respuesta sin modificar ni validar la entidad.</summary>
    public StudentProfile()
    {
        CreateMap<Alumno, StudentResponse>()
            .ForCtorParam("id", options => options.MapFrom(source => source.Id))
            .ForCtorParam("nombre", options => options.MapFrom(source => source.Nombre))
            .ForCtorParam("apellido", options => options.MapFrom(source => source.Apellido))
            .ForCtorParam("email", options => options.MapFrom(source => source.Email))
            .ForCtorParam("activo", options => options.MapFrom(source => source.Activo));
    }
}
