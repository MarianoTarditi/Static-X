namespace BLL.DTOs.Students;

/// <summary>Returns student ficha counts without personal or account data.</summary>
public sealed class DashboardStudentsResponse
{
    public int Total { get; }
    public int Active { get; }
    public int Inactive { get; }
    public int ActiveWithoutAccount { get; }

    public DashboardStudentsResponse(int total, int active, int inactive, int activeWithoutAccount)
    {
        Total = total;
        Active = active;
        Inactive = inactive;
        ActiveWithoutAccount = activeWithoutAccount;
    }
}
