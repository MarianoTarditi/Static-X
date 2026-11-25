namespace Entity;

/// <summary>Contains only the account fields needed for internal Trainer administration reads.</summary>
public sealed class TrainerAccountProjection
{
    public string AccountId { get; }
    public string Email { get; }
    public bool IsEnabled { get; }
    public bool EmailConfirmed { get; }

    public TrainerAccountProjection(string accountId, string email, bool isEnabled, bool emailConfirmed)
    {
        AccountId = accountId;
        Email = email;
        IsEnabled = isEnabled;
        EmailConfirmed = emailConfirmed;
    }
}
