namespace Entity;

public sealed class GymPassPrice
{
    public GymPassPrice() { }
    public long Id { get; set; }
    public int GymId { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public long AmountCents { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
