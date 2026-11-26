using Entity;

namespace DAL.Repositories.Models;

/// <summary>A materialized student page with its filtered storage count.</summary>
public sealed class StudentPage
{
    public IReadOnlyList<Student> Items { get; }
    public int TotalCount { get; }

    public StudentPage(IReadOnlyList<Student> items, int totalCount)
    {
        Items = items;
        TotalCount = totalCount;
    }
}
