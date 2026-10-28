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
    public async Task<Alumno> AddAsync(Alumno student, CancellationToken cancellationToken)
    {
        student.Activo = true;
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
    public async Task<Alumno?> UpdateAsync(int gimnasioId, int studentId, string nombre, string apellido, string email,
        CancellationToken cancellationToken)
    {
        var student = await context.Alumnos.SingleOrDefaultAsync(
            s => s.GimnasioId == gimnasioId && s.Id == studentId, cancellationToken);
        if (student is null) return null;

        student.Nombre = nombre;
        student.Apellido = apellido;
        student.Email = email;
        // Issue a write even for identical fields so a deletion after retrieval is not reported as success.
        context.Entry(student).Property(s => s.Nombre).IsModified = true;
        context.Entry(student).Property(s => s.Apellido).IsModified = true;
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
    public Task<Alumno?> FindAsync(int gimnasioId, int studentId, CancellationToken cancellationToken) =>
        context.Alumnos.AsNoTracking()
            .SingleOrDefaultAsync(s => s.GimnasioId == gimnasioId && s.Id == studentId, cancellationToken);

    /// <summary>Cuenta las coincidencias del gimnasio y obtiene una página ordenada sin seguimiento mediante consultas secuenciales.</summary>
    public async Task<StudentPage> ListAsync(int gimnasioId, bool? activo, string? search, int offset, int pageSize, CancellationToken cancellationToken)
    {
        var query = context.Alumnos.Where(s => s.GimnasioId == gimnasioId);
        if (activo.HasValue) query = query.Where(s => s.Activo == activo.Value);
        if (search is not null)
            query = query.Where(s => s.Nombre.ToUpper().Contains(search) ||
                s.Apellido.ToUpper().Contains(search) || s.Email.ToUpper().Contains(search));
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query.AsNoTracking().OrderBy(s => s.Id).Skip(offset).Take(pageSize).ToListAsync(cancellationToken);
        return new StudentPage(items, totalCount);
    }

    /// <summary>Actualiza únicamente el estado y consulta la ficha persistida dentro del gimnasio solicitado.</summary>
    public async Task<Alumno?> SetActiveAsync(int gimnasioId, int studentId, bool active, CancellationToken cancellationToken)
    {
        var student = await context.Alumnos.SingleOrDefaultAsync(
            s => s.GimnasioId == gimnasioId && s.Id == studentId, cancellationToken);
        if (student is null) return null;
        student.Activo = active;
        // Force an UPDATE even for an idempotent call so a concurrent deletion cannot produce false success.
        context.Entry(student).Property(s => s.Activo).IsModified = true;
        await context.SaveChangesAsync(cancellationToken);
        return await FindAsync(gimnasioId, studentId, cancellationToken)
            ?? throw new DbUpdateConcurrencyException("The student is no longer available in the requested gym after the state update.");
    }
}
