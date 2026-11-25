using System.ComponentModel.DataAnnotations;
using Entity;

namespace BLL.DTOs.Activations;

/// <summary>Requests a pending tenant identity; role and student selection never authorize the caller.</summary>
public sealed class InviteAccountRequest
{
    [Required, MaxLength(128)]
    public string Key { get; init; }
    [Required]
    public string Email { get; init; }
    public AccountRole Role { get; init; }
    public int? StudentId { get; init; }

    public InviteAccountRequest(string key, string email, AccountRole role, int? studentId = null)
    {
        Key = key;
        Email = email;
        Role = role;
        StudentId = studentId;
    }
}
