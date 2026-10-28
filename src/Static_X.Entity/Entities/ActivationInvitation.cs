namespace DAL;

public sealed class ActivationInvitation
{
    public Guid Id { get; set; }
    public int GymId { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
