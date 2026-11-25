namespace DAL;

// Keys are never reused. Cleanup tombstones remain durable to catch a late, abandoned PUT.
public sealed class VideoObject
{
    public string Key { get; set; } = string.Empty;
    public int GymId { get; set; }
    public string State { get; set; } = "Pending";
    public DateTime PendingUntil { get; set; }
    public DateTime NextAttemptAt { get; set; }
}
