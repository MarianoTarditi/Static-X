using Entity;
using BLL.DTOs.Accounts;

namespace BLL.Services.Interfaces;

public interface IAccountService
{
    Task<TrainerListResponse> ListTrainersAsync(int gymId, AccountRole actorRole, TrainerListRequest request,
        CancellationToken cancellationToken = default);
    Task<bool> SetTrainerEnabledAsync(int gymId, AccountRole actorRole, string accountId, bool enabled,
        CancellationToken cancellationToken = default);
    Task<ForgotPasswordResponse> ForgotAsync(ForgotPasswordRequest request, CancellationToken cancellationToken = default);
    Task<string?> ResetAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default);
    Task<AccountResponse> CreateAsync(int gymId, CreateAccountRequest request, AccountRole role,
        CancellationToken cancellationToken = default);
    Task<AccountResponse?> FindByEmailAsync(int gymId, string email, CancellationToken cancellationToken = default);
    Task<AccountResponse?> SignInAsync(int? gymId, LoginRequest request,
        CancellationToken cancellationToken = default);
    Task<AccountResponse?> FindByIdAsync(int? gymId, string accountId, CancellationToken cancellationToken = default);
    Task<bool> ChangePasswordAsync(int? gymId, string accountId, ChangePasswordRequest request,
        CancellationToken cancellationToken = default);
}
