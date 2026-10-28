namespace BLL.DTOs.Students;

/// <summary>Student ficha without account, credential, or gym information.</summary>
public sealed class StudentResponse
{
    public int Id { get; }
    public string Nombre { get; }
    public string Apellido { get; }
    public string Email { get; }
    public bool Activo { get; }

    public StudentResponse(int id, string nombre, string apellido, string email, bool activo)
    {
        Id = id;
        Nombre = nombre;
        Apellido = apellido;
        Email = email;
        Activo = activo;
    }
}
