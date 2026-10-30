namespace BLL.DTOs.Gyms;

/// <summary>The safe account identifier and email needed to select an invitation reissue target.</summary>
public sealed class PendingAdministratorResponse
{
    public string AccountId { get; init; }
    public string Email { get; init; }

    public PendingAdministratorResponse(string accountId, string email)
    {
        AccountId = accountId;
        Email = email;
    }
}
