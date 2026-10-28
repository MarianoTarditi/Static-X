using System.ComponentModel.DataAnnotations;

namespace BLL.DTOs.Students;

/// <summary>Replacement ficha fields; identity and gym ownership are not client-editable.</summary>
public sealed class UpdateStudentRequest
{
    [Required] public string Nombre { get; init; }
    [Required] public string Apellido { get; init; }
    [Required] public string Email { get; init; }

    public UpdateStudentRequest()
    {
        Nombre = string.Empty;
        Apellido = string.Empty;
        Email = string.Empty;
    }
}
