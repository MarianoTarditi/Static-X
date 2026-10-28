using System.ComponentModel.DataAnnotations;

namespace BLL.DTOs.Activations;

/// <summary>One-time activation token and the new administrator password.</summary>
public sealed class ActivateAccountRequest
{
    [Required] public required string Token { get; init; }
    [Required] public required string Password { get; init; }

    public ActivateAccountRequest()
    {
    }
}
