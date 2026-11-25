namespace DAL.Repositories.Models;

public sealed class PaymentSummary
{
    public PaymentSummary() { }
    public int ActiveStudents { get; set; }
    public int NoValidPayment { get; set; }
    public int Expired { get; set; }
    public int DueSoon { get; set; }
    public int Current { get; set; }
    public int RenewalCount { get; set; }
    public long? RenewalCents { get; set; }
}
