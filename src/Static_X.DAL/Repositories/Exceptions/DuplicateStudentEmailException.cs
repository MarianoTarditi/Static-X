namespace DAL.Repositories.Exceptions;

/// <summary>Indica que el correo del alumno ya existe dentro del mismo gimnasio.</summary>
public sealed class DuplicateStudentEmailException : Exception
{
    /// <summary>Representa el conflicto de integridad sin exponer detalles del proveedor.</summary>
    public DuplicateStudentEmailException() : base("The student email already exists in this gym.")
    {
    }
}
