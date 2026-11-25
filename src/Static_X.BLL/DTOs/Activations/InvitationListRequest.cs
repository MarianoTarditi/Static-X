using System.ComponentModel.DataAnnotations;

namespace BLL.DTOs.Activations;

/// <summary>Bounds the requested invitation page.</summary>
public sealed class InvitationListRequest
{
    [Range(0, 100000)]
    public int Offset { get; set; }
    [Range(1, 100)]
    public int PageSize { get; set; }

    public InvitationListRequest() { Offset = 0; PageSize = 25; }
}
