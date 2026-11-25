namespace BLL.DTOs.Payments;

/// <summary>Supplies the mandatory administrative reason for voiding the latest payment.</summary>
public sealed class VoidPaymentRequest
{
    public string Reason { get; set; } = string.Empty;
    public VoidPaymentRequest() { }
}
