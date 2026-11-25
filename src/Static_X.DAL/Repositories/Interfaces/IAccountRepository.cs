using Entity;
using DAL.Repositories.Models;

namespace DAL.Repositories.Interfaces;

// Gym IDs for lookups and provisioning must come from a trusted caller.
public interface IAccountRepository
{
    Task<TrainerAccountPage> ListTrainersAsync(int gymId, int offset, int pageSize, CancellationToken cancellationToken);
    Task<bool> SetTrainerEnabledAsync(int gymId, string accountId, bool enabled, CancellationToken cancellationToken);
    Task EnqueueAsync(int? gymId, string email, DateTimeOffset now, CancellationToken cancellationToken);
    Task<string?> ResetAsync(string token, string password, DateTimeOffset now, CancellationToken cancellationToken);
    Task CleanupAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<PasswordRecoveryDelivery?> LeaseDueAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> FreezeDeliveryAsync(PasswordRecoveryDelivery delivery, string protectedSnapshot, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> IsCurrentAsync(PasswordRecoveryDelivery delivery, DateTimeOffset now, CancellationToken cancellationToken);
    Task CompleteAsync(PasswordRecoveryDelivery delivery, DateTimeOffset now, bool accepted, CancellationToken cancellationToken);
    Task<Account> CreateAsync(int gymId, string email, string password, AccountRole role,
        CancellationToken cancellationToken);
    Task<Account?> FindByEmailAsync(int gymId, string email, CancellationToken cancellationToken);
    Task<Account?> FindByIdAsync(int? gymId, string accountId, CancellationToken cancellationToken);
    Task<Account?> SignInAsync(int? gymId, string email, string password, CancellationToken cancellationToken);
    Task<bool> ChangePasswordAsync(int? gymId, string accountId, string currentPassword, string newPassword,
        CancellationToken cancellationToken);
}
