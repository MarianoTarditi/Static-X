using Entity;

namespace DAL.Repositories.Models;

public sealed class InvitationLifecyclePage
{
    public IReadOnlyList<InvitationLifecycleItem> Items { get; }
    public int TotalCount { get; }

    public InvitationLifecyclePage(IReadOnlyList<InvitationLifecycleItem> items, int totalCount)
    {
        Items = items;
        TotalCount = totalCount;
    }
}
