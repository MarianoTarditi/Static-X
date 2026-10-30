using Entity;

namespace DAL.Repositories.Models;

/// <summary>A materialized gym page and the total storage count.</summary>
public sealed class GymPage
{
    public IReadOnlyList<Gimnasio> Items { get; }
    public int TotalCount { get; }

    public GymPage(IReadOnlyList<Gimnasio> items, int totalCount)
    {
        Items = items;
        TotalCount = totalCount;
    }
}
