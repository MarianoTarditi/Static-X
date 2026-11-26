namespace DAL.Repositories.Interfaces;

public sealed class ActivationDelivery
{
    public Guid OutboxEntryId { get; }
    public Guid InvitationId { get; }
    public Guid LeaseId { get; }
    public string RecipientEmail { get; }
    public string ProtectedToken { get; }

    public ActivationDelivery(Guid OutboxEntryId, Guid InvitationId, Guid LeaseId,
        string RecipientEmail, string ProtectedToken)
    {
        this.OutboxEntryId = OutboxEntryId;
        this.InvitationId = InvitationId;
        this.LeaseId = LeaseId;
        this.RecipientEmail = RecipientEmail;
        this.ProtectedToken = ProtectedToken;
    }
}
