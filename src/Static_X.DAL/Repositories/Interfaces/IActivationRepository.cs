namespace DAL.Repositories.Interfaces;

public interface IActivationRepository
{
    Task<int> ProvisionAsync(string key, string gymName, string email, string tokenHash,
        Guid invitationId, string protectedToken, DateTimeOffset now, DateTimeOffset expiresAt,
        CancellationToken cancellationToken);
    Task<bool> ActivateAsync(string tokenHash, string password, DateTimeOffset now,
        CancellationToken cancellationToken);
    Task<bool> ReissueAsync(int gymId, string accountId, string tokenHash, Guid invitationId,
        string protectedToken, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken);
}
