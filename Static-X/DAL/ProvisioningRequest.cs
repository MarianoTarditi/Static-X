namespace DAL;

public sealed class ProvisioningRequest
{
    public string Key { get; set; } = string.Empty;
    public int GymId { get; set; }
    public string AccountId { get; set; } = string.Empty;
}
