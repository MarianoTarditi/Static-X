using BLL;

namespace DAL.Repositories.Interfaces;

// Gym IDs for lookups and provisioning must come from a trusted caller.
public interface IAccountRepository
{
    Task<Account> CreateAsync(int gymId, string email, string password, AccountRole role,
        CancellationToken cancellationToken);
    Task<Account?> FindByEmailAsync(int gymId, string email, CancellationToken cancellationToken);
    Task<Account?> FindByIdAsync(int gymId, string accountId, CancellationToken cancellationToken);
    Task<Account?> SignInAsync(int gymId, string email, string password, CancellationToken cancellationToken);
}
