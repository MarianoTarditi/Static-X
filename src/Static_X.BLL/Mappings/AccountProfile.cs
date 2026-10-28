using AutoMapper;
using BLL.DTOs.Accounts;
using Entity;

namespace BLL.Mappings;

/// <summary>Define la conversión de la cuenta de dominio a su identidad pública sin credenciales.</summary>
public sealed class AccountProfile : Profile
{
    /// <summary>Configura los parámetros del constructor de la respuesta a partir de la cuenta de dominio.</summary>
    public AccountProfile()
    {
        CreateMap<Account, AccountResponse>()
            .ForCtorParam("Id", options => options.MapFrom(source => source.Id))
            .ForCtorParam("GymId", options => options.MapFrom(source => source.GymId))
            .ForCtorParam("Email", options => options.MapFrom(source => source.Email))
            .ForCtorParam("Role", options => options.MapFrom(source => source.Role));
    }
}
