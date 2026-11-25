namespace BLL.DTOs.Accounts;

/// <summary>Exposes only the Trainer identifier, email and administrative state.</summary>
public sealed class TrainerResponse
{
    public string AccountId { get; }
    public string Email { get; }
    public bool IsEnabled { get; }
    public bool EmailConfirmed { get; }

    public TrainerResponse(string accountId, string email, bool isEnabled, bool emailConfirmed)
    {
        AccountId = accountId;
        Email = email;
        IsEnabled = isEnabled;
        EmailConfirmed = emailConfirmed;
    }
}
