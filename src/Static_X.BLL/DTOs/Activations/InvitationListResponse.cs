namespace BLL.DTOs.Activations;

/// <summary>A bounded page of the latest invitation for each pending account.</summary>
public sealed class InvitationListResponse
{
    public IReadOnlyList<InvitationLifecycleResponse> Items { get; }
    public int TotalCount { get; }

    public InvitationListResponse(IReadOnlyList<InvitationLifecycleResponse> items, int totalCount)
    {
        Items = items;
        TotalCount = totalCount;
    }
}
