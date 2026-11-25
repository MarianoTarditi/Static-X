namespace BLL.DTOs.Students;

/// <summary>A stable, bounded page of available gym videos.</summary>
public sealed class VideoListResponse
{
    public IReadOnlyList<VideoResponse> Items { get; }
    public VideoListResponse(IReadOnlyList<VideoResponse> items) { Items = items; }
}
