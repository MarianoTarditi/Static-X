using AutoMapper;
using BLL.DTOs.Accounts;
using BLL.DTOs.Gyms;
using Entity;

namespace BLL.Mappings;

/// <summary>Define respuestas públicas de cuentas y proyecciones administrativas de entrenadores sin credenciales.</summary>
public sealed class AccountProfile : Profile
{
    /// <summary>Configura los constructores de respuesta a partir de cuentas y proyecciones seguras persistidas.</summary>
    public AccountProfile()
    {
        CreateMap<Account, AccountResponse>()
            .ForCtorParam("Id", options => options.MapFrom(source => source.Id))
            .ForCtorParam("GymId", options => options.MapFrom(source => source.GymId))
            .ForCtorParam("Email", options => options.MapFrom(source => source.Email))
            .ForCtorParam("Role", options => options.MapFrom(source => source.Role));
        CreateMap<Account, PendingAdministratorResponse>()
            .ForCtorParam("accountId", options => options.MapFrom(source => source.Id))
            .ForCtorParam("email", options => options.MapFrom(source => source.Email));
        CreateMap<TrainerAccountProjection, TrainerResponse>()
            .ForCtorParam("accountId", options => options.MapFrom(source => source.AccountId))
            .ForCtorParam("email", options => options.MapFrom(source => source.Email))
            .ForCtorParam("isEnabled", options => options.MapFrom(source => source.IsEnabled))
            .ForCtorParam("emailConfirmed", options => options.MapFrom(source => source.EmailConfirmed));
    }
}
