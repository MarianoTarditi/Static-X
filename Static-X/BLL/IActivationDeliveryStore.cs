namespace BLL;

public sealed record ActivationDelivery(Guid JobId, Guid InvitationId, Guid LeaseOwner,
    string Email, string ProtectedToken);

public interface IActivationDeliveryStore
{
    Task<ActivationDelivery?> LeaseDueAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> IsCurrentAsync(ActivationDelivery delivery, DateTimeOffset now, CancellationToken cancellationToken);
    Task CompleteAsync(ActivationDelivery delivery, DateTimeOffset now, bool accepted, CancellationToken cancellationToken);
}
