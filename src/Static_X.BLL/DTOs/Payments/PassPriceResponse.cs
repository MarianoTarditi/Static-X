namespace BLL.DTOs.Payments;

/// <summary>Returns an immutable effective-date ARS tariff.</summary>
public sealed class PassPriceResponse
{
    public long Id { get; }
    public long AmountCents { get; }
    public DateOnly EffectiveFrom { get; }
    public string Currency { get; } = "ARS";
    public PassPriceResponse(long id, long amountCents, DateOnly effectiveFrom)
    { Id = id; AmountCents = amountCents; EffectiveFrom = effectiveFrom; }
}
