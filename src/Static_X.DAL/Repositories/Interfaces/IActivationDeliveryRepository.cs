namespace DAL.Repositories.Interfaces;

public interface IActivationDeliveryRepository
{
    Task<ActivationDelivery?> LeaseDueAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> IsCurrentAsync(ActivationDelivery delivery, DateTimeOffset now, CancellationToken cancellationToken);
    Task CompleteAsync(ActivationDelivery delivery, DateTimeOffset now, bool accepted, CancellationToken cancellationToken);
}
