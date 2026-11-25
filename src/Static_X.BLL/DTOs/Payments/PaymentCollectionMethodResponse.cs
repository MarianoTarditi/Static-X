using Entity;

namespace BLL.DTOs.Payments;

/// <summary>Valid and void original ledger amounts for one manual payment method.</summary>
public sealed class PaymentCollectionMethodResponse
{
    public PaymentCollectionMethodResponse(PaymentMethod method, int validCount, long validCents, int voidCount, long voidCents)
    { Method = method; ValidCount = validCount; ValidCents = validCents; VoidCount = voidCount; VoidCents = voidCents; }
    public PaymentMethod Method { get; }
    public int ValidCount { get; }
    public long ValidCents { get; }
    public int VoidCount { get; }
    public long VoidCents { get; }
}
