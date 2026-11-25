using System.ComponentModel.DataAnnotations;

namespace BLL.DTOs.Students;

/// <summary>A bounded catalog query.</summary>
public sealed class VideoListRequest
{
    [Range(1, 100000)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 20;
    [RegularExpression("^(informational|exercise)$")] public string? Category { get; set; }
    public VideoListRequest() { }
}
