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
        CreateMap<Student, StudentResponse>()
            .ForCtorParam("id", options => options.MapFrom(source => source.Id))
            .ForCtorParam("nombre", options => options.MapFrom(source => source.FirstName))
            .ForCtorParam("apellido", options => options.MapFrom(source => source.LastName))
            .ForCtorParam("email", options => options.MapFrom(source => source.Email))
            .ForCtorParam("activo", options => options.MapFrom(source => source.IsActive));
    }
}
