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

    /// <summary>Valida el ámbito y devuelve una página segura del estado de las últimas invitaciones persistidas.</summary>
    public async Task<InvitationListResponse> ListInvitationsAsync(int gymId, Entity.AccountRole actorRole, Entity.AccountRole targetRole,
        InvitationListRequest request, CancellationToken ct = default)
    {
        ValidateInvitationAuthority(gymId, actorRole, targetRole);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Offset is < 0 or > 100000 || request.PageSize is < 1 or > 100) throw new ArgumentException("Invalid page.");
        var page = await repository.ListInvitationsAsync(gymId, targetRole, request.Offset, request.PageSize, ct);
        var now = clock.GetUtcNow();
        var items = page.Items.Select(i =>
        {
            var queued = new DateTimeOffset(DateTime.SpecifyKind(i.QueuedAt, DateTimeKind.Utc));
            var expires = new DateTimeOffset(DateTime.SpecifyKind(i.ExpiresAt, DateTimeKind.Utc));
            return new InvitationLifecycleResponse(i.AccountId, i.Email, i.Role,
                i.RevokedAt != null ? "Cancelled" : expires <= now ? "Expired" : "Pending", queued, expires, queued.AddHours(1));
        }).ToArray();
        return new InvitationListResponse(items, page.TotalCount);
    }

    /// <summary>Valida la autoridad y solicita revocar las invitaciones pendientes sin modificar la cuenta ni la ficha.</summary>
    public Task<bool> CancelInvitationAsync(int gymId, Entity.AccountRole actorRole, Entity.AccountRole targetRole, string accountId,
        CancellationToken ct = default)
    {
        if (accountId is null) throw new ArgumentException("Invalid invitation target.");
        ValidateInvitationAuthority(gymId, actorRole, targetRole, accountId);
        return repository.CancelInvitationAsync(gymId, accountId, targetRole, clock.GetUtcNow(), ct);
    }

    /// <summary>Genera un enlace de reemplazo para un destino autorizado sujeto al intervalo persistido de reenvío.</summary>
    public async Task<bool> ResendInvitationAsync(int gymId, Entity.AccountRole actorRole, Entity.AccountRole targetRole, string accountId,
        CancellationToken ct = default)
    {
        if (accountId is null) throw new ArgumentException("Invalid invitation target.");
        ValidateInvitationAuthority(gymId, actorRole, targetRole, accountId);
        RequireKeyRing();
        var id = Guid.NewGuid();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var payload = protection.CreateProtector("Static-X.ActivationDelivery.v1", id.ToString("N")).Protect(token);
        var now = clock.GetUtcNow();
        try { return await repository.ResendInvitationAsync(gymId, accountId, targetRole, Hash(token), id, payload, now, now.AddHours(24), ct); }
        catch (DAL.Repositories.Models.InvitationCooldownException) { throw new BLL.Exceptions.InvitationCooldownException(); }
    }

    /// <summary>Exige un gimnasio válido y la matriz explícita de permisos sobre el rol de destino.</summary>
    private static void ValidateInvitationAuthority(int gymId, Entity.AccountRole actorRole, Entity.AccountRole targetRole, string? accountId = null)
    {
        if (!(actorRole == Entity.AccountRole.SuperAdmin && targetRole == Entity.AccountRole.GymAdmin ||
            actorRole == Entity.AccountRole.GymAdmin && targetRole is Entity.AccountRole.Trainer or Entity.AccountRole.Student ||
            actorRole == Entity.AccountRole.Trainer && targetRole == Entity.AccountRole.Student)) throw new UnauthorizedAccessException();
        if (gymId <= 0 || accountId != null && (string.IsNullOrWhiteSpace(accountId) || accountId.Length > 450))
            throw new ArgumentException("Invalid invitation target.");
    }

    /// <summary>Recibe la persistencia, la protección de tokens, la configuración y el reloj de activación.</summary>
    public ActivationService(IActivationRepository repository, IDataProtectionProvider protection, IConfiguration configuration, TimeProvider clock)
    {
        this.repository = repository;
        this.protection = protection;
        this.configuration = configuration;
        this.clock = clock;
    }

    /// <summary>Valida los permisos y el destino explícito antes de persistir la invitación de una cuenta del gimnasio.</summary>
    public async Task<AccountInvitationResponse?> InviteAsync(int gymId, Entity.AccountRole actorRole, InviteAccountRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (actorRole is Entity.AccountRole.GymAdmin or Entity.AccountRole.Trainer && !Enum.IsDefined(request.Role))
            throw new ArgumentException("Invalid invitation role.");
        if (actorRole is not (Entity.AccountRole.GymAdmin or Entity.AccountRole.Trainer) ||
            request.Role is not (Entity.AccountRole.Trainer or Entity.AccountRole.Student) ||
            actorRole == Entity.AccountRole.Trainer && request.Role != Entity.AccountRole.Student)
            throw new UnauthorizedAccessException();
        if (gymId <= 0 || string.IsNullOrWhiteSpace(request.Key) || request.Key.Length > 128 ||
            string.IsNullOrWhiteSpace(request.Email) || request.Email.Trim().Length > 256 ||
            !System.Net.Mail.MailAddress.TryCreate(request.Email.Trim(), out var address) || address.Address != request.Email.Trim() ||
            (request.Role == Entity.AccountRole.Student ? request.StudentId is null or <= 0 : request.StudentId is not null))
            throw new ArgumentException("Invalid invitation input.");
        RequireKeyRing();
        var id = Guid.NewGuid();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var protectedToken = protection.CreateProtector("Static-X.ActivationDelivery.v1", id.ToString("N")).Protect(token);
        var now = clock.GetUtcNow();
        try
        {
            var account = await repository.InviteAsync(gymId, request.Key, request.Email.Trim(), request.Role, request.StudentId,
                Hash(token), id, protectedToken, now, now.AddHours(24), ct);
            return account is null ? null : new AccountInvitationResponse(account.Id);
        }
        catch (DAL.Repositories.Exceptions.ProvisioningConflictException)
        {
            throw new BLL.Exceptions.ProvisioningConflictException();
        }
        catch (DAL.Repositories.Models.InvitationCooldownException) { throw new BLL.Exceptions.InvitationCooldownException(); }
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
        try { return await repository.ReissueAsync(gymId, accountId, Hash(token), id, protectedToken,
            now, now.AddHours(24), ct); }
        catch (DAL.Repositories.Models.InvitationCooldownException) { throw new BLL.Exceptions.InvitationCooldownException(); }
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
