using System.ComponentModel.DataAnnotations;

namespace BLL.DTOs.Students;

/// <summary>Metadata accompanying one uploaded video.</summary>
public sealed class CreateVideoRequest
{
    [Required, StringLength(200)]
    public string Title { get; set; } = string.Empty;
    [Required, RegularExpression("^(informational|exercise)$")]
    public string Category { get; set; } = string.Empty;
    [StringLength(2000)]
    public string? Description { get; set; }
    public CreateVideoRequest() { }
}
