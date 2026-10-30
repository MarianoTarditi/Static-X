namespace BLL.DTOs.Gyms;

/// <summary>A bounded advisory page of administrators currently eligible for invitation reissue.</summary>
public sealed class PendingAdministratorListResponse
{
    public IReadOnlyList<PendingAdministratorResponse> Items { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }

    public PendingAdministratorListResponse(IReadOnlyList<PendingAdministratorResponse> items, int page, int pageSize, int totalCount)
    {
        Items = items;
        Page = page;
        PageSize = pageSize;
        TotalCount = totalCount;
    }
}
