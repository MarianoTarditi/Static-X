namespace BLL.DTOs.Activations;

/// <summary>Operator-supplied provisioning data, distinct from the persisted idempotency entity.</summary>
public sealed class ProvisionGymRequest
{
    public string Key { get; init; }
    public string GymName { get; init; }
    public string Email { get; init; }

    public ProvisionGymRequest(string key, string gymName, string email)
    {
        Key = key;
        GymName = gymName;
        Email = email;
    }
}
