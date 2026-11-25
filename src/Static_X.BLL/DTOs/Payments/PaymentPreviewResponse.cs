namespace BLL.DTOs.Payments;

/// <summary>Returns trusted payment terms and a protected confirmation token.</summary>
public sealed class PaymentPreviewResponse
{
    public long Revision { get; }
    public long AmountCents { get; }
    public string Currency { get; } = "ARS";
    public DateOnly ExtensionStart { get; }
    public DateOnly CoverageStart { get; }
    public DateOnly CoverageEndExclusive { get; }
    public string PreviewToken { get; }
    public PaymentPreviewResponse(long revision, long amountCents, DateOnly extensionStart, DateOnly coverageStart, DateOnly coverageEndExclusive, string previewToken)
    { Revision = revision; AmountCents = amountCents; ExtensionStart = extensionStart; CoverageStart = coverageStart; CoverageEndExclusive = coverageEndExclusive; PreviewToken = previewToken; }
}
