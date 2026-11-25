namespace BLL.DTOs.Payments;

/// <summary>Returns a bounded staff table with independently filtered count.</summary>
public sealed class PaymentListResponse
{
    public IReadOnlyList<PaymentStudentResponse> Items { get; }
    public int Page { get; }
    public int PageSize { get; }
    public int TotalCount { get; }
    public PaymentListResponse(IReadOnlyList<PaymentStudentResponse> items, int page, int pageSize, int totalCount)
    { Items = items; Page = page; PageSize = pageSize; TotalCount = totalCount; }
}
