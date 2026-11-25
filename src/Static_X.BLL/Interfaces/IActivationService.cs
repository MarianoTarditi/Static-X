using BLL.DTOs.Activations;

namespace BLL.Services.Interfaces;

public interface IActivationService
{
    Task<InvitationListResponse> ListInvitationsAsync(int gymId, Entity.AccountRole actorRole, Entity.AccountRole targetRole,
        InvitationListRequest request, CancellationToken ct = default);
    Task<bool> CancelInvitationAsync(int gymId, Entity.AccountRole actorRole, Entity.AccountRole targetRole, string accountId,
        CancellationToken ct = default);
    Task<bool> ResendInvitationAsync(int gymId, Entity.AccountRole actorRole, Entity.AccountRole targetRole, string accountId,
        CancellationToken ct = default);
    Task<AccountInvitationResponse?> InviteAsync(int gymId, Entity.AccountRole actorRole, InviteAccountRequest request, CancellationToken ct = default);
    Task<int> ProvisionAsync(ProvisionGymRequest request, CancellationToken ct = default);
    Task<ActivationResponse?> ActivateAsync(ActivateAccountRequest request, CancellationToken ct = default);
    Task<bool> ReissueAsync(int gymId, string accountId, CancellationToken ct = default);
}
