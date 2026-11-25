using AutoMapper;
using BLL.DTOs.Students;
using Entity;

namespace BLL.Mappings;

/// <summary>Define la conversión del contenido publicado a las respuestas de rutina.</summary>
public sealed class RoutineProfile : Profile
{
    /// <summary>Configura los parámetros de los constructores y el orden de días y ejercicios.</summary>
    public RoutineProfile()
    {
        CreateMap<StudentRoutine, RoutineResponse>()
            .ForCtorParam("revision", value => value.MapFrom(r => r.Revision))
            .ForCtorParam("title", value => value.MapFrom(r => r.Title))
            .ForCtorParam("instructions", value => value.MapFrom(r => r.Instructions))
            .ForCtorParam("days", value => value.MapFrom(r => r.Days.OrderBy(d => d.Position)));
        CreateMap<StudentRoutineDay, RoutineDayResponse>()
            .ForCtorParam("name", value => value.MapFrom(d => d.Name))
            .ForCtorParam("instructions", value => value.MapFrom(d => d.Instructions))
            .ForCtorParam("exercises", value => value.MapFrom(d => d.Exercises.OrderBy(e => e.Position)));
        CreateMap<StudentRoutineExercise, RoutineExerciseResponse>()
            .ForCtorParam("name", value => value.MapFrom(e => e.Name))
            .ForCtorParam("sets", value => value.MapFrom(e => e.Sets))
            .ForCtorParam("repetitions", value => value.MapFrom(e => e.Repetitions))
            .ForCtorParam("durationSeconds", value => value.MapFrom(e => e.DurationSeconds))
            .ForCtorParam("restSeconds", value => value.MapFrom(e => e.RestSeconds))
            .ForCtorParam("notes", value => value.MapFrom(e => e.Notes));
    }
}
