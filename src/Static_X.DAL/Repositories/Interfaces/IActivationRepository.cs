using Entity;

namespace DAL.Repositories.Interfaces;

public interface IActivationRepository
{
    // Fail closed for older implementations; lifecycle support must be implemented explicitly.
    Task<DAL.Repositories.Models.InvitationLifecyclePage> ListInvitationsAsync(int gymId, AccountRole role, int offset, int pageSize,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<bool> CancelInvitationAsync(int gymId, string accountId, AccountRole role, DateTimeOffset now,
        CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<bool> ResendInvitationAsync(int gymId, string accountId, AccountRole role, string tokenHash, Guid invitationId,
        string protectedToken, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken) => throw new NotSupportedException();
    Task<Account?> InviteAsync(int gymId, string key, string email, AccountRole role, int? studentId, string tokenHash,
        Guid invitationId, string protectedToken, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken);
    Task<int> ProvisionAsync(string key, string gymName, string email, string tokenHash,
        Guid invitationId, string protectedToken, DateTimeOffset now, DateTimeOffset expiresAt,
        CancellationToken cancellationToken);
    Task<Account?> ActivateAsync(string tokenHash, string password, DateTimeOffset now,
        CancellationToken cancellationToken);
    Task<bool> ReissueAsync(int gymId, string accountId, string tokenHash, Guid invitationId,
        string protectedToken, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken);
}
