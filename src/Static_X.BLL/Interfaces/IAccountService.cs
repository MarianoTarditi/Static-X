using Entity;
using BLL.DTOs.Accounts;

namespace BLL.Services.Interfaces;

public interface IAccountService
{
    Task<AccountResponse> CreateAsync(int gymId, CreateAccountRequest request, AccountRole role,
        CancellationToken cancellationToken = default);
    Task<AccountResponse?> FindByEmailAsync(int gymId, string email, CancellationToken cancellationToken = default);
    Task<AccountResponse?> SignInAsync(int? gymId, LoginRequest request,
        CancellationToken cancellationToken = default);
    Task<AccountResponse?> FindByIdAsync(int? gymId, string accountId, CancellationToken cancellationToken = default);
}
