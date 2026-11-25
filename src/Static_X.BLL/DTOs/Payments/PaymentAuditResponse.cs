namespace BLL.DTOs.Payments;

/// <summary>Exposes registration and void audit only to authorized same-gym staff.</summary>
public sealed class PaymentAuditResponse
{
    public string ActorId { get; }
    public DateTimeOffset CreatedAt { get; }
    public string? VoidedBy { get; }
    public DateTimeOffset? VoidedAt { get; }
    public string? VoidReason { get; }
    public PaymentAuditResponse(string actorId, DateTimeOffset createdAt, string? voidedBy, DateTimeOffset? voidedAt, string? voidReason)
    { ActorId = actorId; CreatedAt = createdAt; VoidedBy = voidedBy; VoidedAt = voidedAt; VoidReason = voidReason; }
}
