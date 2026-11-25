namespace Entity;

public sealed class Payment
{
    public Payment() { }
    public long Id { get; set; }
    public int GymId { get; set; }
    public int StudentId { get; set; }
    public long Sequence { get; set; }
    public long? PreviousPaymentId { get; set; }
    public long PriceId { get; set; }
    public long AmountCents { get; set; }
    public DateOnly PaidOn { get; set; }
    public PaymentMethod Method { get; set; }
    public DateOnly ExtensionStart { get; set; }
    public DateOnly CoverageStart { get; set; }
    public DateOnly CoverageEndExclusive { get; set; }
    public string ActorId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestFingerprint { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public string? VoidedBy { get; set; }
    public string? VoidReason { get; set; }
}
