namespace BLL.DTOs.Students;

/// <summary>Returns the available dashboard sections for the authenticated gym.</summary>
public sealed class DashboardResponse
{
    public DashboardStudentsResponse Students { get; }

    public DashboardResponse(DashboardStudentsResponse students)
    {
        Students = students;
    }
}
