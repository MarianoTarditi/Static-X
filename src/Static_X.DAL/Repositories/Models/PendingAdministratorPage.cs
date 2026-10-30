using Entity;

namespace DAL.Repositories.Models;

/// <summary>A materialized page of pure accounts eligible for invitation reissue.</summary>
public sealed class PendingAdministratorPage
{
    public IReadOnlyList<Account> Items { get; }
    public int TotalCount { get; }

    public PendingAdministratorPage(IReadOnlyList<Account> items, int totalCount)
    {
        Items = items;
        TotalCount = totalCount;
    }
}
