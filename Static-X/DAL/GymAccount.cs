using BLL;
using Microsoft.AspNetCore.Identity;

namespace DAL;

public sealed class GymAccount : IdentityUser
{
    public int GymId { get; set; }
    public AccountRole Role { get; set; }
}
