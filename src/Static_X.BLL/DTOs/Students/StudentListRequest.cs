using System.ComponentModel.DataAnnotations;

namespace BLL.DTOs.Students;

/// <summary>Filters and page bounds for listing students in a trusted session gym.</summary>
public sealed class StudentListRequest
{
    public StudentListRequest() { }

    [Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    [Range(1, 100)]
    public int PageSize { get; init; } = 20;

    [MaxLength(320)]
    [DisplayFormat(ConvertEmptyStringToNull = false)]
    public string? Search { get; init; }

    public bool? Activo { get; init; }
}
