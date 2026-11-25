using Entity;

namespace BLL.DTOs.Payments;

/// <summary>Returns immutable recorded payment terms and its current void state without operator audit.</summary>
public sealed class PaymentResponse
{
    public long Id { get; }
    public long Sequence { get; }
    public long PriceId { get; }
    public long AmountCents { get; }
    public string Currency { get; } = "ARS";
    public DateOnly PaidOn { get; }
    public PaymentMethod Method { get; }
    public DateOnly ExtensionStart { get; }
    public DateOnly CoverageStart { get; }
    public DateOnly CoverageEndExclusive { get; }
    public bool IsVoid { get; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PaymentAuditResponse? Audit { get; internal set; }
    public PaymentResponse(long id, long sequence, long priceId, long amountCents, DateOnly paidOn, PaymentMethod method,
        DateOnly extensionStart, DateOnly coverageStart, DateOnly coverageEndExclusive, bool isVoid)
    {
        Id = id; Sequence = sequence; PriceId = priceId; AmountCents = amountCents; PaidOn = paidOn; Method = method;
        ExtensionStart = extensionStart; CoverageStart = coverageStart; CoverageEndExclusive = coverageEndExclusive; IsVoid = isVoid;
    }
}
