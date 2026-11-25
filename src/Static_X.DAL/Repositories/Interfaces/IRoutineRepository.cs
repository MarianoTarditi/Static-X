using DAL.Repositories.Models;
using Entity;

namespace DAL.Repositories.Interfaces;

public interface IRoutineRepository
{
    Task<RoutineReadResult?> FindForTrainerAsync(int gymId, int studentId, CancellationToken cancellationToken);
    Task<RoutineReadResult?> FindForStudentAsync(int gymId, string accountId, CancellationToken cancellationToken);
    Task<RoutineWriteResult> PublishAsync(int gymId, string trainerId, StudentRoutine publication,
        Guid? expectedRevision, CancellationToken cancellationToken);
}
