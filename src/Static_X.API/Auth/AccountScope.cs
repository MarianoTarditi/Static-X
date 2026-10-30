using System.Globalization;
using System.Security.Claims;
using Entity;

namespace Static_X.API.Auth;

internal static class AccountScope
{
    public static bool TryGetGymId(ClaimsPrincipal principal, out int? gymId)
    {
        gymId = null;
        var roles = principal.FindAll(ClaimTypes.Role).ToArray();
        var gyms = principal.FindAll("gym_id").ToArray();
        if (roles.Length != 1) return false;
        if (roles[0].Value == nameof(AccountRole.SuperAdmin)) return gyms.Length == 0;
        if (!Enum.TryParse<AccountRole>(roles[0].Value, out var role) || !Enum.IsDefined(role) ||
            role == AccountRole.SuperAdmin || gyms.Length != 1 ||
            !int.TryParse(gyms[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            return false;
        gymId = id;
        return true;
    }
}
