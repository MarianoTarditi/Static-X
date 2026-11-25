namespace BLL.DTOs.Accounts;

/// <summary>Provides a protected recovery token and the unmodified replacement password.</summary>
public sealed class ResetPasswordRequest
{
    // Business validation deliberately returns the same error for every invalid reset input.
    public string? Token { get; init; }
    public string? NewPassword { get; init; }

    public ResetPasswordRequest() { }
}
