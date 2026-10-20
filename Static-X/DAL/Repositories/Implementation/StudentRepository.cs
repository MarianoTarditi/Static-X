using DAL.Repositories.Interfaces;
using Entity;
using Microsoft.EntityFrameworkCore;

namespace DAL.Repositories.Implementation;

public sealed class StudentRepository(dbcontext context) : IStudentRepository
{
    public async Task<Alumno> AddAsync(Alumno student, CancellationToken cancellationToken)
    {
        await context.Alumnos.AddAsync(student, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        return student;
    }

    public Task<Alumno?> FindAsync(int gimnasioId, int studentId, CancellationToken cancellationToken) =>
        context.Alumnos.AsNoTracking()
            .SingleOrDefaultAsync(s => s.GimnasioId == gimnasioId && s.Id == studentId, cancellationToken);

    public async Task<IReadOnlyList<Alumno>> ListAsync(int gimnasioId, CancellationToken cancellationToken) =>
        await context.Alumnos.AsNoTracking()
            .Where(s => s.GimnasioId == gimnasioId)
            .OrderBy(s => s.Id)
            .ToListAsync(cancellationToken);
}
