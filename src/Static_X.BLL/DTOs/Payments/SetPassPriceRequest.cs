namespace BLL.DTOs.Payments;

/// <summary>Sets an ARS price in integer cents; only the first price accepts an effective date.</summary>
public sealed class SetPassPriceRequest
{
    public long AmountCents { get; set; }
    public DateOnly? InitialEffectiveFrom { get; set; }
    public SetPassPriceRequest() { }
}
