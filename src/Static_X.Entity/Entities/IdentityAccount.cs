using Entity;
using Microsoft.AspNetCore.Identity;

namespace DAL;

// Identity adapter deliberately owned by the Entity assembly; Account is the pure domain model.
public sealed class IdentityAccount : IdentityUser
{
    public int? GymId { get; set; }
    public AccountRole Role { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool StudentAccessSuspended { get; set; }
}
