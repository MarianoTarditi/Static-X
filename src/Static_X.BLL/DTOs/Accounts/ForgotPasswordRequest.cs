using System.ComponentModel.DataAnnotations;

namespace BLL.DTOs.Accounts;

/// <summary>Selects credentials for an anonymous recovery request, never authorization.</summary>
public sealed class ForgotPasswordRequest
{
    [Required, EmailAddress, MaxLength(256)]
    public string Email { get; init; } = string.Empty;
    [Range(1, int.MaxValue)]
    public int? GymId { get; init; }

    public ForgotPasswordRequest() { }
}
