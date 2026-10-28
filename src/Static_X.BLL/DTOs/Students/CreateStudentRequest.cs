using System.ComponentModel.DataAnnotations;

namespace BLL.DTOs.Students;

/// <summary>Student ficha input; gym ownership comes exclusively from the authenticated session.</summary>
public sealed class CreateStudentRequest
{
    [Required] public string Nombre { get; init; }
    [Required] public string Apellido { get; init; }
    [Required] public string Email { get; init; }

    public CreateStudentRequest()
    {
        Nombre = string.Empty;
        Apellido = string.Empty;
        Email = string.Empty;
    }
}
