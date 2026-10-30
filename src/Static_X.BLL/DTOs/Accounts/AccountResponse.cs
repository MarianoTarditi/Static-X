using Entity;

namespace BLL.DTOs.Accounts;

/// <summary>Authenticated account identity without credential material.</summary>
public sealed class AccountResponse
{
    public string Id { get; init; }
    public int? GymId { get; init; }
    public string Email { get; init; }
    public AccountRole Role { get; init; }

    public AccountResponse(string Id, int? GymId, string Email, AccountRole Role)
    {
        this.Id = Id;
        this.GymId = GymId;
        this.Email = Email;
        this.Role = Role;
    }
}
