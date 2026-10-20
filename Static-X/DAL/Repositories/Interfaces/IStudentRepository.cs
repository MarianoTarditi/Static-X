using Entity;

namespace DAL.Repositories.Interfaces;

// The gym identifier must come from a trusted caller, never from an unauthenticated request.
public interface IStudentRepository
{
    Task<Alumno> AddAsync(Alumno student, CancellationToken cancellationToken);
    Task<Alumno?> FindAsync(int gimnasioId, int studentId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Alumno>> ListAsync(int gimnasioId, CancellationToken cancellationToken);
}
