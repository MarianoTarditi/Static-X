using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DAL;

/// <summary>Uses an isolated Identity protector and lifetime without changing other token providers.</summary>
public sealed class PasswordResetTokenProvider : DataProtectorTokenProvider<GymAccount>
{
    public const string ProviderName = "Static-X.PasswordReset";

    public PasswordResetTokenProvider(IDataProtectionProvider protection,
        IOptions<PasswordResetTokenProviderOptions> options, ILogger<DataProtectorTokenProvider<GymAccount>> logger)
        : base(protection, options, logger) { }
}

public sealed class PasswordResetTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public PasswordResetTokenProviderOptions()
    {
        Name = "Static-X.PasswordReset.Identity.v1";
        TokenLifespan = TimeSpan.FromMinutes(30);
    }
}
