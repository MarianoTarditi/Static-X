using Entity;

namespace DAL;

public sealed class TenantInvitationRequest
{
    public int GymId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string NormalizedEmail { get; set; } = string.Empty;
    public AccountRole Role { get; set; }
    public int? StudentId { get; set; }
    public string AccountId { get; set; } = string.Empty;
}
