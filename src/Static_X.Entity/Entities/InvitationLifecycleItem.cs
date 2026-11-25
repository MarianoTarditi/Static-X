namespace Entity;

/// <summary>A safe persisted invitation summary without credentials or delivery secrets.</summary>
public sealed class InvitationLifecycleItem
{
    public string AccountId { get; }
    public string Email { get; }
    public AccountRole Role { get; }
    public DateTime QueuedAt { get; }
    public DateTime ExpiresAt { get; }
    public DateTime? RevokedAt { get; }

    public InvitationLifecycleItem(string accountId, string email, AccountRole role, DateTime queuedAt, DateTime expiresAt, DateTime? revokedAt)
    {
        AccountId = accountId;
        Email = email;
        Role = role;
        QueuedAt = queuedAt;
        ExpiresAt = expiresAt;
        RevokedAt = revokedAt;
    }
}
