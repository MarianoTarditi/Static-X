using Entity;

namespace DAL.Repositories.Models;

public sealed class TrainerAccountPage
{
    public IReadOnlyList<TrainerAccountProjection> Items { get; }
    public int TotalCount { get; }

    public TrainerAccountPage(IReadOnlyList<TrainerAccountProjection> items, int totalCount)
    {
        Items = items;
        TotalCount = totalCount;
    }
}
