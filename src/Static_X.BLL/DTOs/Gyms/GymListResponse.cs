namespace BLL.DTOs.Gyms;

/// <summary>A bounded administrative gym page with its total storage count.</summary>
public sealed class GymListResponse
{
    public IReadOnlyList<GymResponse> Items { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }

    public GymListResponse(IReadOnlyList<GymResponse> items, int page, int pageSize, int totalCount)
    {
        Items = items;
        Page = page;
        PageSize = pageSize;
        TotalCount = totalCount;
    }
}
