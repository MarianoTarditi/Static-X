using Entity;

namespace DAL;

/// <summary>Stores a recovery request and its protected durable delivery, without plaintext bearer secrets.</summary>
public sealed class PasswordRecoveryOutbox
{
    public Guid Id { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public int? GymId { get; set; }
    public AccountRole Role { get; set; }
    public string StampBinding { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string ProtectedPayload { get; set; } = string.Empty;
    public Guid? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public DateTime? SentAt { get; set; }
    public int AttemptCount { get; set; }

    public PasswordRecoveryOutbox() { }
}
