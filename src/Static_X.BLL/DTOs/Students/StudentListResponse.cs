namespace BLL.DTOs.Students;

/// <summary>A bounded student page and the total count matching its filters.</summary>
public sealed class StudentListResponse
{
    public IReadOnlyList<StudentResponse> Items { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }

    public StudentListResponse(IReadOnlyList<StudentResponse> items, int page, int pageSize, int totalCount)
    {
        Items = items;
        Page = page;
        PageSize = pageSize;
        TotalCount = totalCount;
    }
}
