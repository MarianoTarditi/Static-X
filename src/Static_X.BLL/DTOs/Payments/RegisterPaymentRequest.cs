using Entity;

namespace BLL.DTOs.Payments;

/// <summary>Confirms an unchanged trusted preview with a tenant-scoped idempotency key.</summary>
public sealed class RegisterPaymentRequest
{
    public DateOnly PaidOn { get; set; }
    public PaymentMethod Method { get; set; }
    public string PreviewToken { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public RegisterPaymentRequest() { }
}
