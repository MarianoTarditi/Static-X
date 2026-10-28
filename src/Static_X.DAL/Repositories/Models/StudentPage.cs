using Entity;

namespace DAL.Repositories.Models;

/// <summary>A materialized student page with its filtered storage count.</summary>
public sealed class StudentPage
{
    public IReadOnlyList<Alumno> Items { get; }
    public int TotalCount { get; }

    public StudentPage(IReadOnlyList<Alumno> items, int totalCount)
    {
        Items = items;
        TotalCount = totalCount;
    }
}
