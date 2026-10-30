using System.ComponentModel.DataAnnotations;

namespace BLL.DTOs.Accounts;

/// <summary>Credentials for an existing gym account, or a global account when gym is omitted.</summary>
public sealed class LoginRequest
{
    [Range(1, int.MaxValue)] public int? GymId { get; init; }
    [Required, EmailAddress] public required string Email { get; init; }
    [Required] public required string Password { get; init; }

    public LoginRequest()
    {
    }
}
