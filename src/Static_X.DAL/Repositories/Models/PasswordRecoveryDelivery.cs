namespace DAL.Repositories.Models;

/// <summary>Internal delivery projection, never an HTTP response.</summary>
public sealed class PasswordRecoveryDelivery
{
    public const string SnapshotPrefix = "snapshot.v1:";
    public Guid RequestId { get; }
    public Guid LeaseOwner { get; }
    public string Email { get; }
    public string ProtectedPayload { get; }

    public PasswordRecoveryDelivery(Guid requestId, Guid leaseOwner, string email, string protectedPayload)
    {
        RequestId = requestId;
        LeaseOwner = leaseOwner;
        Email = email;
        ProtectedPayload = protectedPayload;
    }
}
