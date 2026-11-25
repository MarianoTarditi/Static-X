namespace BLL.DTOs.Students;

/// <summary>A single byte interval or suffix; null bounds request the complete object.</summary>
public sealed class VideoRangeRequest
{
    public long? Start { get; set; }
    public long? End { get; set; }
    public long? Suffix { get; set; }
    public VideoRangeRequest() { }
}
