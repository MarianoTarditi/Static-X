using DAL.Repositories.Interfaces;
using DAL.Repositories.Models;
using Entity;
using Microsoft.EntityFrameworkCore;

namespace DAL.Repositories.Implementation;

/// <summary>Consulta y publica rutinas de fichas dentro del gimnasio autorizado.</summary>
public sealed class RoutineRepository : IRoutineRepository
{
    private readonly StaticXDbContext context;

    /// <summary>Recibe el contexto que administra las fichas y sus rutinas.</summary>
    public RoutineRepository(StaticXDbContext context)
    {
        this.context = context;
    }

    /// <summary>Busca la rutina actual de una ficha por gimnasio e identificador.</summary>
    public Task<RoutineReadResult?> FindForTrainerAsync(int gymId, int studentId, CancellationToken cancellationToken) =>
        FindAsync(context.Alumnos.Where(s => s.GimnasioId == gymId && s.Id == studentId), cancellationToken);

    /// <summary>Busca la rutina actual de la ficha activa vinculada explícitamente a la cuenta.</summary>
    public Task<RoutineReadResult?> FindForStudentAsync(int gymId, string accountId, CancellationToken cancellationToken) =>
        FindAsync(context.Alumnos.Where(s => s.GimnasioId == gymId && s.AccountId == accountId && s.Activo), cancellationToken);

    /// <summary>Distingue una ficha inexistente de una ficha sin rutina y carga el contenido en orden.</summary>
    private Task<RoutineReadResult?> FindAsync(IQueryable<Alumno> students, CancellationToken cancellationToken)
    {
        var routines = context.StudentRoutines.Include(r => r.Days.OrderBy(d => d.Position))
            .ThenInclude(d => d.Exercises.OrderBy(e => e.Position));
        return (from student in students
                join routine in routines on student.Id equals routine.StudentId into matches
                from routine in matches.DefaultIfEmpty()
                select new RoutineReadResult(routine))
            .AsNoTracking().AsSingleQuery().SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>Verifica el entrenador y la revisión bajo el bloqueo del gimnasio y guarda la publicación atómicamente.</summary>
    public async Task<RoutineWriteResult> PublishAsync(int gymId, string trainerId, StudentRoutine publication,
        Guid? expectedRevision, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await TenantAccountLock.AcquireAsync(context, gymId, cancellationToken);

        if (!await context.Users.AnyAsync(u => u.Id == trainerId && u.GymId == gymId &&
                u.Role == AccountRole.Trainer && u.IsEnabled, cancellationToken))
            return new RoutineWriteResult(RoutineWriteOutcome.Forbidden);

        var studentId = publication.StudentId;
        var target = context.Alumnos.Where(s => s.GimnasioId == gymId && s.Id == studentId);
        if (!await target.AnyAsync(cancellationToken))
            return new RoutineWriteResult(RoutineWriteOutcome.NotFound);

        var existing = context.StudentRoutines.Where(r => target.Any(s => s.Id == r.StudentId));
        publication.Revision = Guid.NewGuid();
        if (expectedRevision is null)
        {
            if (await existing.AnyAsync(cancellationToken))
                return new RoutineWriteResult(RoutineWriteOutcome.RevisionConflict);
            context.StudentRoutines.Add(publication);
        }
        else
        {
            var updated = await existing.Where(r => r.Revision == expectedRevision.Value)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.Revision, publication.Revision)
                    .SetProperty(r => r.Title, publication.Title)
                    .SetProperty(r => r.Instructions, publication.Instructions), cancellationToken);
            if (updated == 0)
                return new RoutineWriteResult(RoutineWriteOutcome.RevisionConflict);

            // Delete before reusing position keys. The FK removes the old exercises in the same transaction.
            await context.Set<StudentRoutineDay>().Where(d => target.Any(s => s.Id == d.StudentId))
                .ExecuteDeleteAsync(cancellationToken);
            context.Set<StudentRoutineDay>().AddRange(publication.Days);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RoutineWriteResult(RoutineWriteOutcome.Published, publication);
    }
}
