namespace DAL;

public sealed class GymProvisioningRecord
{
    public string IdempotencyKey { get; set; } = string.Empty;
    public int GymId { get; set; }
    public string InitialAdminAccountId { get; set; } = string.Empty;
}
