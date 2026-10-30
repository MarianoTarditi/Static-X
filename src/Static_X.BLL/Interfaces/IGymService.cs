using BLL.DTOs.Gyms;

namespace BLL.Services.Interfaces;

public interface IGymService
{
    Task<GymResponse> AddAsync(CreateGymRequest request, CancellationToken cancellationToken = default);
    Task<GymListResponse> ListAsync(GymListRequest request, CancellationToken cancellationToken = default);
    Task<PendingAdministratorListResponse?> ListPendingAdministratorsAsync(int gymId, GymListRequest request, CancellationToken cancellationToken = default);
}
