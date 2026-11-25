using Entity;

namespace DAL.Repositories.Models;

public sealed class PaymentStudentPage
{
    public IReadOnlyList<PaymentSnapshot> Items { get; }
    public int TotalCount { get; }
    public PaymentStudentPage(IReadOnlyList<PaymentSnapshot> items, int totalCount)
    {
        Items = items;
        TotalCount = totalCount;
    }
}
