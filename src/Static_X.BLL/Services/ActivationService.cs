using System.Security.Cryptography;
using System.Text;
using BLL.Services.Interfaces;
using BLL.DTOs.Activations;
using DAL.Repositories.Interfaces;
using Microsoft.AspNetCore.DataProtection;

namespace BLL;

// Valida las solicitudes de activación y prepara los tokens para el alta y la reemisión de invitaciones.
/// <summary>Valida las solicitudes de activación y prepara invitaciones protegidas para su persistencia.</summary>
public sealed class ActivationService : IActivationService
{
    private readonly IActivationRepository repository;
    private readonly IDataProtectionProvider protection;
    private readonly IConfiguration configuration;
    private readonly TimeProvider clock;

    /// <summary>Recibe la persistencia, la protección de tokens, la configuración y el reloj de activación.</summary>
    public ActivationService(IActivationRepository repository, IDataProtectionProvider protection, IConfiguration configuration, TimeProvider clock)
    {
        this.repository = repository;
        this.protection = protection;
        this.configuration = configuration;
        this.clock = clock;
    }

    // Valida los datos y el almacén de claves, genera un token y solicita el alta con su hash y su copia protegida.
    /// <summary>Valida los datos de provisión y persiste una invitación con su token protegido.</summary>
    public async Task<int> ProvisionAsync(ProvisionGymRequest request, CancellationToken ct = default)
    {
        RequireKeyRing();
        ArgumentNullException.ThrowIfNull(request);
        var key = request.Key;
        var gymName = request.GymName;
        var email = request.Email;
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || string.IsNullOrWhiteSpace(gymName)
            || gymName.Length > 200 || string.IsNullOrWhiteSpace(email) || email.Length > 256 ||
            !System.Net.Mail.MailAddress.TryCreate(email.Trim(), out var address) || address.Address != email.Trim())
            throw new ArgumentException("Invalid provisioning input.");

        var id = Guid.NewGuid();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var hash = Hash(token);
        var protectedToken = protection.CreateProtector("Static-X.ActivationDelivery.v1", id.ToString("N"))
            .Protect(token);
        var now = clock.GetUtcNow();
        try
        {
            return await repository.ProvisionAsync(key, gymName.Trim(), email.Trim(), hash, id,
                protectedToken, now, now.AddHours(24), ct);
        }
        catch (DAL.Repositories.Exceptions.ProvisioningConflictException)
        {
            throw new BLL.Exceptions.ProvisioningConflictException();
        }
    }

    // Comprueba el formato del token y la presencia de contraseña antes de solicitar la activación mediante su hash.
    /// <summary>Valida las credenciales y devuelve el gimnasio de la cuenta cuya activación fue confirmada.</summary>
    public async Task<ActivationResponse?> ActivateAsync(ActivateAccountRequest request, CancellationToken ct = default)
    {
        if (request is null) return null;
        var token = request.Token;
        var password = request.Password;
        if (string.IsNullOrWhiteSpace(token) || token.Length != 64 ||
            !token.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(password))
            return null;
        var account = await repository.ActivateAsync(Hash(token.ToUpperInvariant()), password, clock.GetUtcNow(), ct);
        return account?.GymId is int gymId && gymId > 0 ? new ActivationResponse(gymId) : null;
    }

    // Genera un nuevo token protegido y solicita reemplazar la invitación de la cuenta y el gimnasio indicados
    /// <summary>Reemite una invitación para la cuenta y el gimnasio identificados por el operador.</summary>
    public async Task<bool> ReissueAsync(int gymId, string accountId, CancellationToken ct = default)
    {
        if (gymId <= 0 || string.IsNullOrWhiteSpace(accountId) || accountId.Length > 450)
            throw new ArgumentException("A gym and account are required.");
        RequireKeyRing();
        var id = Guid.NewGuid();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var protectedToken = protection.CreateProtector("Static-X.ActivationDelivery.v1", id.ToString("N"))
            .Protect(token);
        var now = clock.GetUtcNow();
        return await repository.ReissueAsync(gymId, accountId, Hash(token), id, protectedToken,
            now, now.AddHours(24), ct);
    }

    /// <summary>Exige que la ruta configurada para el almacén de claves sea absoluta y corresponda a un directorio existente.</summary>
    private void RequireKeyRing()
    {
        var ring = configuration["Activation:KeyRingPath"];
        if (string.IsNullOrWhiteSpace(ring) || !Path.IsPathFullyQualified(ring) || !Directory.Exists(ring))
            throw new InvalidOperationException("A persistent activation key ring is required.");
    }

    /// <summary>Calcula el hash SHA-256 del token en ASCII y lo representa en hexadecimal.</summary>
    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
}
