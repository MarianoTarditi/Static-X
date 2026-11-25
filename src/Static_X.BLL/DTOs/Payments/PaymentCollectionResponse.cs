namespace BLL.DTOs.Payments;

/// <summary>Paid-on collections with voids separated, never refunds or personal payment details.</summary>
public sealed class PaymentCollectionResponse
{
    public PaymentCollectionResponse(DateOnly businessDate, DateOnly from, DateOnly to, IReadOnlyList<PaymentCollectionMethodResponse> methods)
    { BusinessDate = businessDate; From = from; To = to; Methods = methods; }
    public DateOnly BusinessDate { get; }
    public DateOnly From { get; }
    public DateOnly To { get; }
    public string Currency => "ARS";
    public IReadOnlyList<PaymentCollectionMethodResponse> Methods { get; }
    public int ValidCount => Methods.Sum(m => m.ValidCount);
    public long ValidCents => Methods.Sum(m => m.ValidCents);
    public int VoidCount => Methods.Sum(m => m.VoidCount);
    public long VoidCents => Methods.Sum(m => m.VoidCents);
}
