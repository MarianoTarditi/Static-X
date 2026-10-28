using BLL.DTOs.Gyms;

namespace BLL.Services.Interfaces;

public interface IGymService
{
    Task<GymResponse> AddAsync(CreateGymRequest request, CancellationToken cancellationToken = default);
}
