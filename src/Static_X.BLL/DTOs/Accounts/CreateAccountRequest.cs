namespace BLL.DTOs.Accounts;

/// <summary>Account credentials; gym membership and the authorized role are supplied separately.</summary>
public sealed class CreateAccountRequest
{
    public string Email { get; init; }
    public string Password { get; init; }

    public CreateAccountRequest(string email, string password)
    {
        Email = email;
        Password = password;
    }
}
