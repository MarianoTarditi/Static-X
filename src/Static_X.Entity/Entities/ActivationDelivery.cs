namespace DAL.Repositories.Interfaces;

public sealed class ActivationDelivery
{
    public Guid JobId { get; }
    public Guid InvitationId { get; }
    public Guid LeaseOwner { get; }
    public string Email { get; }
    public string ProtectedToken { get; }

    public ActivationDelivery(Guid JobId, Guid InvitationId, Guid LeaseOwner,
        string Email, string ProtectedToken)
    {
        this.JobId = JobId;
        this.InvitationId = InvitationId;
        this.LeaseOwner = LeaseOwner;
        this.Email = Email;
        this.ProtectedToken = ProtectedToken;
    }
}
