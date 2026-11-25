namespace DAL.Repositories.Models;

/// <summary>Contains the aggregated persisted student ficha counts for one gym.</summary>
public sealed class DashboardStudentCounts
{
    public int Total { get; }
    public int Active { get; }
    public int Inactive { get; }
    public int ActiveWithoutAccount { get; }

    public DashboardStudentCounts(int total, int active, int inactive, int activeWithoutAccount)
    {
        Total = total;
        Active = active;
        Inactive = inactive;
        ActiveWithoutAccount = activeWithoutAccount;
    }
}
