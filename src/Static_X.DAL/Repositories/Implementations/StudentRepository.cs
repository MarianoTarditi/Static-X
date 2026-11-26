using DAL.Repositories.Interfaces;
using DAL.Repositories.Models;
using DAL.Repositories.Exceptions;
using Entity;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DAL.Repositories.Implementation;

/// <summary>Persiste alumnos y consulta sus datos con filtros por gimnasio.</summary>
public sealed class StudentRepository : IStudentRepository
{
    private readonly StaticXDbContext context;

    /// <summary>Recibe el contexto que persiste y consulta los alumnos.</summary>
    public StudentRepository(StaticXDbContext context)
    {
        this.context = context;
    }

    /// <summary>Agrega el alumno al contexto, guarda los cambios y devuelve la entidad persistida.</summary>
    public async Task<Student> AddAsync(Student student, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        student.IsActive = true;
        await context.Alumnos.AddAsync(student, cancellationToken);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsStudentEmailDuplicate(exception))
        {
            context.Entry(student).State = EntityState.Detached;
            throw new DuplicateStudentEmailException();
        }
        return student;
    }

    /// <summary>Actualiza únicamente los campos de ficha de un alumno localizado por gimnasio e identificador.</summary>
    public async Task<Student?> UpdateAsync(int gimnasioId, int studentId, string nombre, string apellido, string email,
        CancellationToken cancellationToken)
    {
        var student = await context.Alumnos.SingleOrDefaultAsync(
            s => s.GymId == gimnasioId && s.Id == studentId, cancellationToken);
        if (student is null) return null;

        student.FirstName = nombre;
        student.LastName = apellido;
        student.Email = email;
        // Issue a write even for identical fields so a deletion after retrieval is not reported as success.
        context.Entry(student).Property(s => s.FirstName).IsModified = true;
        context.Entry(student).Property(s => s.LastName).IsModified = true;
        context.Entry(student).Property(s => s.Email).IsModified = true;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsStudentEmailDuplicate(exception))
        {
            context.Entry(student).State = EntityState.Detached;
            throw new DuplicateStudentEmailException();
        }
        return student;
    }

    /// <summary>Reconoce únicamente la violación del índice único de correo por gimnasio en los proveedores admitidos.</summary>
    private static bool IsStudentEmailDuplicate(DbUpdateException exception)
    {
        // Require both the provider error code and the exact affected index/columns;
        // unrelated unique, primary-key, foreign-key and connectivity errors must propagate.
        return exception.InnerException switch
        {
            SqlException sql => sql.Errors.Cast<SqlError>().Any(IsSqlStudentEmailDuplicate),
            SqliteException sqlite => sqlite.SqliteErrorCode == 19 && sqlite.SqliteExtendedErrorCode == 2067 &&
                sqlite.Message.Equals("SQLite Error 19: 'UNIQUE constraint failed: Alumnos.GimnasioId, Alumnos.Email'.", StringComparison.Ordinal),
            _ => false
        };
    }

    /// <summary>Comprueba el código SQL Server y los primeros identificadores citados del objeto y del índice afectados.</summary>
    private static bool IsSqlStudentEmailDuplicate(SqlError error)
    {
        // SQL Server localizes the surrounding text, but quotes the object and index
        // before the duplicate value. Never match identifiers inside that value.
        var quoted = error.Message.Split('\'');
        return error.Number == 2601 && quoted.Length >= 5 &&
            quoted[1] == "dbo.Alumnos" && quoted[3] == "IX_Alumnos_GimnasioId_Email";
    }

    /// <summary>Busca sin seguimiento un alumno por su identificador y el gimnasio al que pertenece.</summary>
    public Task<Student?> FindAsync(int gimnasioId, int studentId, CancellationToken cancellationToken) =>
        context.Alumnos.AsNoTracking()
            .SingleOrDefaultAsync(s => s.GymId == gimnasioId && s.Id == studentId, cancellationToken);

    /// <summary>Cuenta las coincidencias del gimnasio y obtiene una página ordenada sin seguimiento mediante consultas secuenciales.</summary>
    public async Task<StudentPage> ListAsync(int gimnasioId, bool? activo, string? search, int offset, int pageSize, CancellationToken cancellationToken)
    {
        var query = context.Alumnos.Where(s => s.GymId == gimnasioId);
        if (activo.HasValue) query = query.Where(s => s.IsActive == activo.Value);
        if (search is not null)
            query = query.Where(s => s.FirstName.ToUpper().Contains(search) ||
                s.LastName.ToUpper().Contains(search) || s.Email.ToUpper().Contains(search));
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query.AsNoTracking().OrderBy(s => s.Id).Skip(offset).Take(pageSize).ToListAsync(cancellationToken);
        return new StudentPage(items, totalCount);
    }

    /// <summary>Sincroniza atómicamente el estado de la ficha y el acceso de su cuenta Student explícitamente vinculada.</summary>
    public async Task<Student?> SetActiveAsync(int gimnasioId, int studentId, bool active, CancellationToken cancellationToken)
    {
        var student = await context.Alumnos.SingleOrDefaultAsync(
            s => s.GymId == gimnasioId && s.Id == studentId, cancellationToken);
        if (student is null) return null;
        // Preserve the historical unlinked save/refresh and post-save cancellation contract.
        // AccountId is a concurrency token: an invitation that binds after this read makes this write fail closed.
        if (student.AccountId is null)
            return await SaveStateAsync(student, active, cancellationToken);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await TenantAccountLock.AcquireAsync(context, gimnasioId, cancellationToken);
        await context.Entry(student).ReloadAsync(cancellationToken);
        if (context.Entry(student).State == EntityState.Detached || student.GymId != gimnasioId)
            throw new DbUpdateConcurrencyException("The student is no longer available in the requested gym.");
        if (student.AccountId is not null)
        {
            var account = await context.Users.SingleOrDefaultAsync(u => u.Id == student.AccountId &&
                u.GymId == gimnasioId && u.Role == AccountRole.Student, cancellationToken);
            if (account is null) throw new InvalidOperationException("The student account link is invalid.");
            if (!active && account.IsEnabled)
            {
                account.IsEnabled = false;
                account.StudentAccessSuspended = true;
                account.SecurityStamp = Guid.NewGuid().ToString("N");
                account.ConcurrencyStamp = Guid.NewGuid().ToString("N");
            }
            else if (active && account.StudentAccessSuspended)
            {
                account.IsEnabled = true;
                account.StudentAccessSuspended = false;
                account.ConcurrencyStamp = Guid.NewGuid().ToString("N");
            }
        }
        var persisted = await SaveStateAsync(student, active, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return persisted;
    }

    /// <summary>Guarda únicamente el estado de la ficha y actualiza su lectura, comprobando el vínculo original frente a cambios concurrentes.</summary>
    private async Task<Student> SaveStateAsync(Student student, bool active, CancellationToken cancellationToken)
    {
        student.IsActive = active;
        // Force an UPDATE even for an idempotent call so a concurrent deletion cannot produce false success.
        context.Entry(student).Property(s => s.IsActive).IsModified = true;
        await context.SaveChangesAsync(cancellationToken);
        return await FindAsync(student.GymId, student.Id, cancellationToken)
            ?? throw new DbUpdateConcurrencyException("The student is no longer available in the requested gym after the state update.");
    }
}
