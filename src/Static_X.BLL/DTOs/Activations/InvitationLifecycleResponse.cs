namespace BLL.DTOs.Activations;

/// <summary>Reports queue state, not provider delivery or account activation.</summary>
public sealed class InvitationLifecycleResponse
{
    public string AccountId { get; }
    public string Email { get; }
    public Entity.AccountRole Role { get; }
    public string Status { get; }
    public DateTimeOffset QueuedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public DateTimeOffset NextResendAt { get; }

    public InvitationLifecycleResponse(string accountId, string email, Entity.AccountRole role, string status,
        DateTimeOffset queuedAt, DateTimeOffset expiresAt, DateTimeOffset nextResendAt)
    {
        AccountId = accountId;
        Email = email;
        Role = role;
        Status = status;
        QueuedAt = queuedAt;
        ExpiresAt = expiresAt;
        NextResendAt = nextResendAt;
    }
}
