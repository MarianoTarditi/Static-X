namespace BLL.DTOs.Activations;

/// <summary>Identifies the persisted invitation target without exposing delivery secrets.</summary>
public sealed class AccountInvitationResponse
{
    public string AccountId { get; }

    public AccountInvitationResponse(string accountId)
    {
        AccountId = accountId;
    }
}
