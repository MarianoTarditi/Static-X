using Entity;

namespace BLL.DTOs.Payments;

/// <summary>Requests a full-price thirty-day manual payment preview.</summary>
public sealed class PaymentPreviewRequest
{
    public DateOnly PaidOn { get; set; }
    public PaymentMethod Method { get; set; }
    public PaymentPreviewRequest() { }
}
