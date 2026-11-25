using System.ComponentModel.DataAnnotations;

namespace BLL.DTOs.Accounts;

/// <summary>Current and replacement passwords for the authenticated account.</summary>
public sealed class ChangePasswordRequest
{
    [Required] public required string CurrentPassword { get; init; }
    [Required] public required string NewPassword { get; init; }

    public ChangePasswordRequest()
    {
    }
}
