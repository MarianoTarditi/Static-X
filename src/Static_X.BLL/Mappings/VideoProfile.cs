using AutoMapper;
using BLL.DTOs.Students;
using Entity;

namespace BLL.Mappings;

/// <summary>Convierte entradas de videoteca sin exponer referencias privadas.</summary>
public sealed class VideoProfile : Profile
{
    /// <summary>Configura los parámetros explícitos de la respuesta pública.</summary>
    public VideoProfile()
    {
        CreateMap<LibraryVideo, VideoResponse>()
            .ForCtorParam("id", o => o.MapFrom(v => v.Id))
            .ForCtorParam("revision", o => o.MapFrom(v => v.Revision))
            .ForCtorParam("title", o => o.MapFrom(v => v.Title))
            .ForCtorParam("category", o => o.MapFrom(v => v.Category))
            .ForCtorParam("description", o => o.MapFrom(v => v.Description))
            .ForCtorParam("length", o => o.MapFrom(v => v.Length));
    }
}
