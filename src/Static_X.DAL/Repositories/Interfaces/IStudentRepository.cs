using Entity;
using DAL.Repositories.Models;

namespace DAL.Repositories.Interfaces;

// The gym identifier must come from a trusted caller, never from an unauthenticated request.
public interface IStudentRepository
{
    Task<Alumno> AddAsync(Alumno student, CancellationToken cancellationToken);
    Task<Alumno?> FindAsync(int gimnasioId, int studentId, CancellationToken cancellationToken);
    Task<Alumno?> UpdateAsync(int gimnasioId, int studentId, string nombre, string apellido, string email,
        CancellationToken cancellationToken);
    Task<Alumno?> SetActiveAsync(int gimnasioId, int studentId, bool active, CancellationToken cancellationToken);
    Task<StudentPage> ListAsync(int gimnasioId, bool? activo, string? search, int offset, int pageSize, CancellationToken cancellationToken);
}
