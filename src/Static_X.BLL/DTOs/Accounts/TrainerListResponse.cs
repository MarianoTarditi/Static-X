namespace BLL.DTOs.Accounts;

/// <summary>Returns a bounded Trainer page and its matching account count.</summary>
public sealed class TrainerListResponse
{
    public IReadOnlyList<TrainerResponse> Items { get; }
    public int Page { get; }
    public int PageSize { get; }
    public int TotalCount { get; }

    public TrainerListResponse(IReadOnlyList<TrainerResponse> items, int page, int pageSize, int totalCount)
    {
        Items = items;
        Page = page;
        PageSize = pageSize;
        TotalCount = totalCount;
    }
}
