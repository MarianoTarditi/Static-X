using System.Security.Claims;
using DAL;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Static_X.API.Auth;

public sealed class GymClaimsPrincipalFactory(
    UserManager<GymAccount> users, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<GymAccount>(users, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(GymAccount user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim("gym_id", user.GymId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        identity.AddClaim(new Claim(ClaimTypes.Role, user.Role.ToString()));
        return identity;
    }
}
