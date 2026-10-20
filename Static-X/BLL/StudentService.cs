using DAL.Repositories.Interfaces;
using Entity;

namespace BLL;

public sealed class StudentService(IStudentRepository repository)
{
    public Task<Alumno> AddAsync(int gimnasioId, string nombre, string apellido, string email,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gimnasioId);
        var student = new Alumno
        {
            GimnasioId = gimnasioId,
            Nombre = Required(nombre, nameof(nombre)),
            Apellido = Required(apellido, nameof(apellido)),
            Email = Required(email, nameof(email)).ToUpperInvariant()
        };
        return repository.AddAsync(student, cancellationToken);
    }

    public Task<Alumno?> FindAsync(int gimnasioId, int studentId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gimnasioId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(studentId);
        return repository.FindAsync(gimnasioId, studentId, cancellationToken);
    }

    public Task<IReadOnlyList<Alumno>> ListAsync(int gimnasioId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gimnasioId);
        return repository.ListAsync(gimnasioId, cancellationToken);
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A value is required.", name) : value.Trim();
}
