namespace BLL.DTOs.Payments;

/// <summary>Active ficha coverage and potential renewals, not debt or collected revenue.</summary>
public sealed class PaymentSummaryResponse
{
    public PaymentSummaryResponse(DateOnly businessDate, int activeStudents, int noValidPayment, int expired, int dueSoon, int current, int estimatedRenewalCount, long? estimatedRenewalCents)
    {
        BusinessDate = businessDate; ActiveStudents = activeStudents; NoValidPayment = noValidPayment;
        Expired = expired; DueSoon = dueSoon; Current = current;
        EstimatedRenewalCount = estimatedRenewalCount; EstimatedRenewalCents = estimatedRenewalCents;
    }
    public DateOnly BusinessDate { get; }
    public string Currency => "ARS";
    public int ActiveStudents { get; }
    public int NoValidPayment { get; }
    public int Expired { get; }
    public int DueSoon { get; }
    public int Current { get; }
    public int EstimatedRenewalCount { get; }
    public long? EstimatedRenewalCents { get; }
}
