using AutoMapper;
using BLL.DTOs.Students;
using DAL.Repositories.Models;

namespace BLL.Mappings;

/// <summary>Convierte los conteos persistidos del panel en su sección pública de alumnos.</summary>
public sealed class DashboardProfile : Profile
{
    /// <summary>Configura los parámetros del constructor de los conteos públicos sin alterar su alcance.</summary>
    public DashboardProfile()
    {
        CreateMap<DashboardStudentCounts, DashboardStudentsResponse>()
            .ForCtorParam("total", options => options.MapFrom(source => source.Total))
            .ForCtorParam("active", options => options.MapFrom(source => source.Active))
            .ForCtorParam("inactive", options => options.MapFrom(source => source.Inactive))
            .ForCtorParam("activeWithoutAccount", options => options.MapFrom(source => source.ActiveWithoutAccount));
    }
}
