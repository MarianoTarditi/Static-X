using Entity;

namespace DAL.Repositories.Models;

public sealed class PaymentCollection
{
    public PaymentCollection() { }
    public PaymentMethod Method { get; set; }
    public int ValidCount { get; set; }
    public long ValidCents { get; set; }
    public int VoidCount { get; set; }
    public long VoidCents { get; set; }
}
