namespace DAL;

public sealed class ActivationOutbox
{
    public Guid Id { get; set; }
    public Guid InvitationId { get; set; }
    public string ProtectedToken { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public Guid? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
}
