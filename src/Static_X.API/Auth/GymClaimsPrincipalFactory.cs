using System.Security.Claims;
using DAL;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Static_X.API.Auth;

public sealed class GymClaimsPrincipalFactory(
    UserManager<IdentityAccount> users, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<IdentityAccount>(users, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(IdentityAccount user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (user.GymId is int gymId)
            identity.AddClaim(new Claim("gym_id", gymId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        identity.AddClaim(new Claim(ClaimTypes.Role, user.Role.ToString()));
        return identity;
    }
}
