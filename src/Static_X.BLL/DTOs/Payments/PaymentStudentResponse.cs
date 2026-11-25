using Entity;

namespace BLL.DTOs.Payments;

/// <summary>Separates current tariff, recorded last payment and live calendar-day coverage.</summary>
public sealed class PaymentStudentResponse
{
    public int StudentId { get; }
    public string FullName { get; }
    public bool Active { get; }
    public long? PricePer30DaysCents { get; }
    public PaymentResponse? LastPayment { get; }
    public DateOnly BusinessDate { get; }
    public DateOnly? PaidThrough { get; }
    public DateOnly? CoverageEndExclusive { get; }
    public PaymentStatus Status { get; }
    public PaymentStudentResponse(int studentId, string fullName, bool active, long? pricePer30DaysCents, PaymentResponse? lastPayment,
        DateOnly businessDate, DateOnly? paidThrough, DateOnly? coverageEndExclusive, PaymentStatus status)
    {
        StudentId = studentId; FullName = fullName; Active = active; PricePer30DaysCents = pricePer30DaysCents; LastPayment = lastPayment;
        BusinessDate = businessDate; PaidThrough = paidThrough; CoverageEndExclusive = coverageEndExclusive; Status = status;
    }
}
