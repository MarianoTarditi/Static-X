using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace BLL;

public sealed class ActivationService(IActivationStore store, IDataProtectionProvider protection,
    IConfiguration configuration, TimeProvider clock)
{
    public async Task<int> ProvisionAsync(string key, string gymName, string email, CancellationToken ct = default)
    {
        // A missing durable shared key ring must never produce an undeliverable invitation.
        var ring = configuration["Activation:KeyRingPath"];
        if (string.IsNullOrWhiteSpace(ring) || !Path.IsPathFullyQualified(ring) || !Directory.Exists(ring))
            throw new InvalidOperationException("A persistent activation key ring is required.");
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || string.IsNullOrWhiteSpace(gymName)
            || gymName.Length > 200 || string.IsNullOrWhiteSpace(email) || email.Length > 256)
            throw new ArgumentException("Invalid provisioning input.");

        var id = Guid.NewGuid();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var hash = Hash(token);
        var protectedToken = protection.CreateProtector("Static-X.ActivationDelivery.v1", id.ToString("N"))
            .Protect(token);
        var now = clock.GetUtcNow();
        return await store.ProvisionAsync(key, gymName.Trim(), email.Trim(), hash, id,
            protectedToken, now, now.AddHours(24), ct);
    }

    public Task<bool> ActivateAsync(string token, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != 64 ||
            !token.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(password))
            return Task.FromResult(false);
        return store.ActivateAsync(Hash(token.ToUpperInvariant()), password, clock.GetUtcNow(), ct);
    }

    // Internal administrative operation; never expose this through an anonymous HTTP endpoint.
    public async Task<bool> ReissueAsync(int gymId, string accountId, CancellationToken ct = default)
    {
        if (gymId <= 0 || string.IsNullOrWhiteSpace(accountId))
            throw new ArgumentException("A gym and account are required.");
        RequireKeyRing();
        var id = Guid.NewGuid();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var protectedToken = protection.CreateProtector("Static-X.ActivationDelivery.v1", id.ToString("N"))
            .Protect(token);
        var now = clock.GetUtcNow();
        return await store.ReissueAsync(gymId, accountId, Hash(token), id, protectedToken,
            now, now.AddHours(24), ct);
    }

    private void RequireKeyRing()
    {
        var ring = configuration["Activation:KeyRingPath"];
        if (string.IsNullOrWhiteSpace(ring) || !Path.IsPathFullyQualified(ring) || !Directory.Exists(ring))
            throw new InvalidOperationException("A persistent activation key ring is required.");
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
}
