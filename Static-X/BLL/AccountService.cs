using DAL.Repositories.Interfaces;

namespace BLL;

// Trusted provisioning and gym-scoped reads only; no public registration endpoint.
public sealed class AccountService(IAccountRepository repository)
{
    public Task<Account> CreateAsync(int gymId, string email, string password, AccountRole role,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gymId);
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("A password is required.", nameof(password));
        return repository.CreateAsync(gymId, RequiredEmail(email), password, role, cancellationToken);
    }

    public Task<Account?> FindByEmailAsync(int gymId, string email, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gymId);
        return repository.FindByEmailAsync(gymId, RequiredEmail(email), cancellationToken);
    }

    public Task<Account?> SignInAsync(int gymId, string email, string password,
        CancellationToken cancellationToken = default)
    {
        if (gymId <= 0 || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return Task.FromResult<Account?>(null);
        return repository.SignInAsync(gymId, email.Trim(), password, cancellationToken);
    }

    public Task<Account?> FindByIdAsync(int gymId, string accountId, CancellationToken cancellationToken = default) =>
        gymId <= 0 || string.IsNullOrWhiteSpace(accountId)
            ? Task.FromResult<Account?>(null)
            : repository.FindByIdAsync(gymId, accountId, cancellationToken);

    private static string RequiredEmail(string? email) =>
        string.IsNullOrWhiteSpace(email)
            ? throw new ArgumentException("An email is required.", nameof(email))
            : email.Trim();
}
