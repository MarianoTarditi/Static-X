using BLL.DTOs.Activations;

namespace BLL.Services.Interfaces;

public interface IActivationService
{
    Task<int> ProvisionAsync(ProvisionGymRequest request, CancellationToken ct = default);
    Task<bool> ActivateAsync(ActivateAccountRequest request, CancellationToken ct = default);
    Task<bool> ReissueAsync(int gymId, string accountId, CancellationToken ct = default);
}
