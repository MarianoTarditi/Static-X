using Entity;

namespace BLL.DTOs.Payments;

/// <summary>Bounds a payment table query and its optional name and coverage filters.</summary>
public sealed class PaymentListRequest
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? Search { get; set; }
    public PaymentStatus? Status { get; set; }
    public PaymentListRequest() { }
}
