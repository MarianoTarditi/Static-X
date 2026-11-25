using BLL.DTOs.Students;
using Entity;

namespace BLL.Services.Interfaces;

public interface IRoutineService
{
    Task<RoutineEnvelopeResponse?> ReadForTrainerAsync(int gymId, AccountRole role, int studentId, CancellationToken cancellationToken);
    Task<RoutineEnvelopeResponse?> ReadForStudentAsync(int gymId, AccountRole role, string accountId, CancellationToken cancellationToken);
    Task<RoutineEnvelopeResponse?> PublishAsync(int gymId, AccountRole role, string trainerId, int studentId,
        PublishRoutineRequest request, CancellationToken cancellationToken);
}
