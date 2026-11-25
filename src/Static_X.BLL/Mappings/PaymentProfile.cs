using AutoMapper;
using BLL.DTOs.Payments;
using Entity;

namespace BLL.Mappings;

/// <summary>Convierte pagos y tarifas persistidos a respuestas sin credenciales ni auditoría interna.</summary>
public sealed class PaymentProfile : Profile
{
    /// <summary>Configura los parámetros explícitos de las respuestas de pagos y tarifas.</summary>
    public PaymentProfile()
    {
        CreateMap<Payment, PaymentResponse>()
            .ForMember(p => p.Audit, o => o.Ignore())
            .ForCtorParam("id", o => o.MapFrom(p => p.Id))
            .ForCtorParam("sequence", o => o.MapFrom(p => p.Sequence))
            .ForCtorParam("priceId", o => o.MapFrom(p => p.PriceId))
            .ForCtorParam("amountCents", o => o.MapFrom(p => p.AmountCents))
            .ForCtorParam("paidOn", o => o.MapFrom(p => p.PaidOn))
            .ForCtorParam("method", o => o.MapFrom(p => p.Method))
            .ForCtorParam("extensionStart", o => o.MapFrom(p => p.ExtensionStart))
            .ForCtorParam("coverageStart", o => o.MapFrom(p => p.CoverageStart))
            .ForCtorParam("coverageEndExclusive", o => o.MapFrom(p => p.CoverageEndExclusive))
            .ForCtorParam("isVoid", o => o.MapFrom(p => p.VoidedAt != null));
        CreateMap<GymPassPrice, PassPriceResponse>()
            .ForCtorParam("id", o => o.MapFrom(p => p.Id))
            .ForCtorParam("amountCents", o => o.MapFrom(p => p.AmountCents))
            .ForCtorParam("effectiveFrom", o => o.MapFrom(p => p.EffectiveFrom));
    }
}
