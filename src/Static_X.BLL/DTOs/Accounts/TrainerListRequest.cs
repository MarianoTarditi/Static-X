using System.ComponentModel.DataAnnotations;

namespace BLL.DTOs.Accounts;

/// <summary>Specifies bounded Trainer account pagination without authorization fields.</summary>
public sealed class TrainerListRequest
{
    [Range(1, int.MaxValue)]
    public int Page { get; init; }
    [Range(1, 100)]
    public int PageSize { get; init; }

    public TrainerListRequest()
    {
        Page = 1;
        PageSize = 20;
    }
}
