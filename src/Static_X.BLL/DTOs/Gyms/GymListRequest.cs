using System.ComponentModel.DataAnnotations;

namespace BLL.DTOs.Gyms;

/// <summary>Page bounds for administrative gym and pending administrator queries.</summary>
public sealed class GymListRequest
{
    public GymListRequest() { }

    [Range(1, int.MaxValue)]
    public int Page { get; init; } = 1;

    [Range(1, 100)]
    public int PageSize { get; init; } = 20;
}
