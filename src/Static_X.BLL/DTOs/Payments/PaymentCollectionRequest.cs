using System.ComponentModel.DataAnnotations;

namespace BLL.DTOs.Payments;

/// <summary>Required inclusive paid-on reporting dates, at most 366 calendar days.</summary>
public sealed class PaymentCollectionRequest
{
    public PaymentCollectionRequest() { }
    [Required]
    public DateOnly? From { get; set; }
    [Required]
    public DateOnly? To { get; set; }
}
