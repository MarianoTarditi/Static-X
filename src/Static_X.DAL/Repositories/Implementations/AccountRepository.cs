using DAL.Repositories.Interfaces;
using Entity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using DAL.Repositories.Models;
using Microsoft.AspNetCore.DataProtection;

namespace DAL.Repositories.Implementation;

/// <summary>Persiste cuentas, autenticación, recuperación y acceso de entrenadores con aislamiento de gimnasio.</summary>
public sealed class AccountRepository : IAccountRepository
{
    private readonly StaticXDbContext context;
    private readonly UserManager<GymAccount> users;
    private readonly SignInManager<GymAccount> signIn;
    private readonly IDataProtectionProvider protection;

    /// <summary>Recibe el contexto compartido, los administradores de Identity y la protección persistente de secretos.</summary>
    public AccountRepository(StaticXDbContext context, UserManager<GymAccount> users,
        SignInManager<GymAccount> signIn, IDataProtectionProvider protection)
    {
        this.context = context;
        this.users = users;
        this.signIn = signIn;
        this.protection = protection;
    }

    /// <summary>Consulta una página ordenada de todos los entrenadores del gimnasio mediante una proyección sin credenciales ni seguimiento.</summary>
    public async Task<DAL.Repositories.Models.TrainerAccountPage> ListTrainersAsync(int gymId, int offset, int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gymId);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var query = context.Users.AsNoTracking().Where(u => u.GymId == gymId && u.Role == AccountRole.Trainer);
        var count = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(u => u.Id).Skip(offset).Take(pageSize)
            .Select(u => new TrainerAccountProjection(u.Id, u.Email!, u.IsEnabled, u.EmailConfirmed)).ToListAsync(cancellationToken);
        return new DAL.Repositories.Models.TrainerAccountPage(items, count);
    }

    /// <summary>Serializa el cambio de acceso del entrenador y actualiza únicamente su estado y sellos de forma atómica.</summary>
    public async Task<bool> SetTrainerEnabledAsync(int gymId, string accountId, bool enabled, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gymId);
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > 450)
            throw new ArgumentException("Invalid account identifier.", nameof(accountId));
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (await TenantAccountLock.AcquireAsync(context, gymId, cancellationToken) != 1) return false;
        var target = context.Users.Where(u => u.Id == accountId && u.GymId == gymId && u.Role == AccountRole.Trainer);
        var transition = target.Where(u => u.IsEnabled != enabled);
        var concurrencyStamp = Guid.NewGuid().ToString("N");
        var securityStamp = enabled ? null : Guid.NewGuid().ToString("N");
        // Rotate on disable only. Re-enabling never restores an old cookie or changes activation/lockout state.
        var changed = enabled
            ? await transition.ExecuteUpdateAsync(s => s.SetProperty(u => u.IsEnabled, true)
                .SetProperty(u => u.ConcurrencyStamp, concurrencyStamp), cancellationToken)
            : await transition.ExecuteUpdateAsync(s => s.SetProperty(u => u.IsEnabled, false)
                .SetProperty(u => u.SecurityStamp, securityStamp)
                .SetProperty(u => u.ConcurrencyStamp, concurrencyStamp), cancellationToken);
        if (changed == 0 && !await target.AsNoTracking().AnyAsync(cancellationToken)) return false;
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>Crea una cuenta con contraseña mediante Identity, un nombre de usuario único y el gimnasio y rol indicados.</summary>
    public async Task<Account> CreateAsync(int gymId, string email, string password, AccountRole role,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (gymId <= 0 || !Enum.IsDefined(role) || role == AccountRole.SuperAdmin)
            throw new ArgumentOutOfRangeException(nameof(role));
        var id = Guid.NewGuid().ToString("N");
        var user = new GymAccount
        {
            Id = id,
            UserName = id, 
            Email = email,
            GymId = gymId,
            Role = role
        };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Account creation failed: {string.Join(", ", result.Errors.Select(e => e.Code))}");

        return new Account(user.Id, user.GymId, user.Email!, user.Role);
    }

    /// <summary>Normaliza el correo y consulta sin seguimiento la cuenta correspondiente dentro del gimnasio indicado.</summary>
    public async Task<Account?> FindByEmailAsync(int gymId, string email, CancellationToken cancellationToken)
    {
        var normalized = users.NormalizeEmail(email);
        var user = await context.Users.AsNoTracking()
            .Where(u => u.GymId == gymId && u.NormalizedEmail == normalized)
            .Select(u => new Account(u.Id, u.GymId, u.Email!, u.Role))
            .SingleOrDefaultAsync(cancellationToken);
        return user;
    }

    /// <summary>Consulta una cuenta confirmada por identificador dentro del ámbito global o de gimnasio seleccionado.</summary>
    public async Task<Account?> FindByIdAsync(int? gymId, string accountId, CancellationToken cancellationToken)
    {
        var user = await context.Users.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == accountId && u.GymId == gymId && u.EmailConfirmed && u.IsEnabled &&
                (gymId != null || u.Role == AccountRole.SuperAdmin),
                cancellationToken);
        return user is null ? null : new Account(user.Id, user.GymId, user.Email!, user.Role);
    }

    /// <summary>Autentica por correo normalizado y ámbito explícito mediante Identity con control de bloqueo por fallos.</summary>
    public async Task<Account?> SignInAsync(int? gymId, string email, string password,
        CancellationToken cancellationToken)
    {
        var normalized = users.NormalizeEmail(email);
        var user = await context.Users.SingleOrDefaultAsync(
            u => u.GymId == gymId && u.NormalizedEmail == normalized && u.IsEnabled &&
                (gymId != null || u.Role == AccountRole.SuperAdmin), cancellationToken);
        if (user is null)
            return null;

        // Identity owns password verification, confirmed-email checks, lockout and cookie issuance.
        var result = await signIn.PasswordSignInAsync(user, password, isPersistent: false,
            lockoutOnFailure: true);
        return result.Succeeded ? new Account(user.Id, user.GymId, user.Email!, user.Role) : null;
    }

    /// <summary>Cambia la contraseña de la cuenta confirmada y rota su sello mediante la actualización concurrente de Identity.</summary>
    public async Task<bool> ChangePasswordAsync(int? gymId, string accountId, string currentPassword, string newPassword,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (gymId <= 0 || string.IsNullOrWhiteSpace(accountId))
            return false;
        var user = await context.Users.SingleOrDefaultAsync(u => u.Id == accountId && u.GymId == gymId &&
            u.EmailConfirmed && u.IsEnabled && (gymId != null || u.Role == AccountRole.SuperAdmin), cancellationToken);
        if (user is null)
            return false;

        // Identity validates both passwords and persists the hash and new security stamp in one concurrency-checked update.
        cancellationToken.ThrowIfCancellationRequested();
        return (await users.ChangePasswordAsync(user, currentPassword, newPassword)).Succeeded;
    }

    /// <summary>Encola un token para una cuenta elegible o conserva su envío pendiente sin modificar credenciales.</summary>
    public async Task EnqueueAsync(int? gymId, string email, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Resolve token infrastructure before lookup for eligible and ineligible requests alike.
        if (!users.Options.Tokens.ProviderMap.ContainsKey(PasswordResetTokenProvider.ProviderName))
            throw new InvalidOperationException("Recovery token provider is unavailable.");
        var normalized = users.NormalizeEmail(email);
        var utc = now.UtcDateTime;
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var user = await context.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == normalized && u.GymId == gymId,
            cancellationToken);
        if (user is null || !Eligible(user))
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        var binding = Bind(user.SecurityStamp);
        var pending = await context.PasswordRecoveryOutbox.AsNoTracking().Where(o => o.AccountId == user.Id &&
            o.SentAt == null && o.ConsumedAt == null && o.RevokedAt == null && o.ExpiresAt > utc &&
            o.StampBinding == binding && o.GymId == user.GymId && o.Role == user.Role).AnyAsync(cancellationToken);
        if (!pending)
        {
            var id = Guid.NewGuid();
            var identityToken = await users.GeneratePasswordResetTokenAsync(user);
            var envelope = protection.CreateProtector("Static-X.PasswordRecovery.v1")
                .Protect(id.ToString("N") + ":" + identityToken);
            context.PasswordRecoveryOutbox.Add(new PasswordRecoveryOutbox
            {
                Id = id, AccountId = user.Id, GymId = user.GymId, Role = user.Role, StampBinding = binding,
                CreatedAt = utc, ExpiresAt = utc.AddMinutes(30), NextAttemptAt = utc,
                ProtectedPayload = protection.CreateProtector("Static-X.PasswordRecoveryDelivery.v1", id.ToString("N")).Protect(envelope)
            });
            await context.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Consume una solicitud vigente y guarda contraseña y sello de Identity en la misma transacción.</summary>
    public async Task<string?> ResetAsync(string token, string password, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (token.Length > 8192 || string.IsNullOrWhiteSpace(password)) return null;
        string payload;
        try { payload = protection.CreateProtector("Static-X.PasswordRecovery.v1").Unprotect(token); }
        catch (CryptographicException) { return null; }
        if (payload.Length < 34 || payload[32] != ':' || !Guid.TryParseExact(payload[..32], "N", out var id)) return null;
        var identityToken = payload[33..];
        var utc = now.UtcDateTime;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Claim before reading Identity. Concurrent replays serialize on this conditional write.
            var claimed = await context.PasswordRecoveryOutbox.Where(o => o.Id == id && o.ExpiresAt > utc &&
                o.ConsumedAt == null && o.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.ConsumedAt, utc), cancellationToken);
            if (claimed != 1) return null;
            var request = await context.PasswordRecoveryOutbox.AsNoTracking().SingleAsync(o => o.Id == id, cancellationToken);
            var user = await context.Users.SingleOrDefaultAsync(u => u.Id == request.AccountId, cancellationToken);
            if (user is null || !Matches(user, request)) return null;
            cancellationToken.ThrowIfCancellationRequested();
            var result = await users.ResetPasswordAsync(user, identityToken, password);
            if (!result.Succeeded) return null;
            // ResetPasswordAsync rotates the stamp but deliberately does not reset lockout state.
            await context.PasswordRecoveryOutbox.Where(o => o.AccountId == user.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.ProtectedPayload, string.Empty)
                    .SetProperty(o => o.NextAttemptAt, (DateTime?)null)
                    .SetProperty(o => o.LeaseOwner, (Guid?)null)
                    .SetProperty(o => o.LeaseExpiresAt, (DateTime?)null)
                    .SetProperty(o => o.RevokedAt, o => o.Id != id && o.RevokedAt == null ? utc : o.RevokedAt), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return user.Id;
        }
        finally
        {
            // Disposal rolls back unsuccessful claims and Identity writes; discard their tracked copies too.
            context.ChangeTracker.Clear();
        }
    }

    /// <summary>Borra un lote acotado de secretos terminados o vinculados a cuentas ya no vigentes sin depender del correo.</summary>
    public async Task CleanupAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var utc = now.UtcDateTime;
        // Oldest expiry first bounds each iteration and prevents newer requests from starving older secrets.
        var candidates = await context.PasswordRecoveryOutbox.AsNoTracking().Where(o => o.ProtectedPayload != string.Empty)
            .OrderBy(o => o.ExpiresAt).ThenBy(o => o.Id).Take(100).ToListAsync(cancellationToken);
        var accountIds = candidates.Select(o => o.AccountId).Distinct().ToArray();
        var accounts = await context.Users.AsNoTracking().Where(u => accountIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, cancellationToken);
        var terminal = candidates.Where(o => o.ExpiresAt <= utc || o.ConsumedAt != null || o.RevokedAt != null || o.SentAt != null ||
            !accounts.TryGetValue(o.AccountId, out var user) || !Matches(user, o)).Select(o => o.Id).ToArray();
        await context.PasswordRecoveryOutbox.Where(o => terminal.Contains(o.Id) && o.ProtectedPayload != string.Empty)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ProtectedPayload, string.Empty)
                .SetProperty(o => o.NextAttemptAt, (DateTime?)null)
                .SetProperty(o => o.LeaseOwner, (Guid?)null).SetProperty(o => o.LeaseExpiresAt, (DateTime?)null)
                .SetProperty(o => o.RevokedAt, o => o.ExpiresAt > utc && o.ConsumedAt == null && o.SentAt == null
                    ? o.RevokedAt ?? utc : o.RevokedAt), cancellationToken);
    }

    /// <summary>Reserva un envío vigente con propietario exclusivo sin ejecutar tareas de mantenimiento.</summary>
    public async Task<PasswordRecoveryDelivery?> LeaseDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var utc = now.UtcDateTime;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var id = await context.PasswordRecoveryOutbox.AsNoTracking().Where(o => o.SentAt == null &&
                o.ProtectedPayload != string.Empty && o.ConsumedAt == null && o.RevokedAt == null && o.ExpiresAt > utc &&
                o.NextAttemptAt <= utc && (o.LeaseExpiresAt == null || o.LeaseExpiresAt <= utc))
                .OrderBy(o => o.NextAttemptAt).ThenBy(o => o.Id).Select(o => (Guid?)o.Id).FirstOrDefaultAsync(cancellationToken);
            if (id is null) return null;
            var owner = Guid.NewGuid();
            var claimed = await context.PasswordRecoveryOutbox.Where(o => o.Id == id && o.SentAt == null &&
                o.ConsumedAt == null && o.RevokedAt == null && o.ExpiresAt > utc && o.ProtectedPayload != string.Empty &&
                o.NextAttemptAt <= utc && (o.LeaseExpiresAt == null || o.LeaseExpiresAt <= utc))
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.LeaseOwner, owner)
                    .SetProperty(o => o.LeaseExpiresAt, utc.AddMinutes(5)), cancellationToken);
            if (claimed != 1) continue;
            var request = await context.PasswordRecoveryOutbox.AsNoTracking().SingleAsync(o => o.Id == id, cancellationToken);
            var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == request.AccountId, cancellationToken);
            if (user is null || !Matches(user, request))
            {
                await RevokeAsync(request.Id, owner, utc, cancellationToken);
                continue;
            }
            return new PasswordRecoveryDelivery(request.Id, owner, user.Email!, request.ProtectedPayload);
        }
        return null;
    }

    /// <summary>Persiste la instantánea protegida mediante comparación condicional de contenido y reserva antes del envío.</summary>
    public async Task<bool> FreezeDeliveryAsync(PasswordRecoveryDelivery delivery, string protectedSnapshot, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!protectedSnapshot.StartsWith(PasswordRecoveryDelivery.SnapshotPrefix, StringComparison.Ordinal)) return false;
        if (!await IsCurrentAsync(delivery, now, cancellationToken)) return false;
        var utc = now.UtcDateTime;
        return await context.PasswordRecoveryOutbox.Where(o => o.Id == delivery.RequestId && o.LeaseOwner == delivery.LeaseOwner &&
            o.LeaseExpiresAt > utc && o.ExpiresAt > utc && o.SentAt == null && o.ConsumedAt == null && o.RevokedAt == null &&
            o.ProtectedPayload == delivery.ProtectedPayload && !o.ProtectedPayload.StartsWith(PasswordRecoveryDelivery.SnapshotPrefix))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ProtectedPayload, protectedSnapshot), cancellationToken) == 1;
    }

    /// <summary>Verifica la reserva y el sello actual inmediatamente antes de entregar el enlace.</summary>
    public async Task<bool> IsCurrentAsync(PasswordRecoveryDelivery delivery, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var utc = now.UtcDateTime;
        var request = await context.PasswordRecoveryOutbox.AsNoTracking().SingleOrDefaultAsync(o => o.Id == delivery.RequestId &&
            o.LeaseOwner == delivery.LeaseOwner && o.LeaseExpiresAt > utc && o.SentAt == null && o.ExpiresAt > utc &&
            o.ConsumedAt == null && o.RevokedAt == null && o.ProtectedPayload != string.Empty, cancellationToken);
        if (request is null) return false;
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == request.AccountId, cancellationToken);
        if (user is not null && Matches(user, request)) return true;
        await RevokeAsync(request.Id, delivery.LeaseOwner, utc, cancellationToken);
        return false;
    }

    /// <summary>Confirma la aceptación o programa reintentos sin liberar reservas ajenas ni recuperar secretos borrados.</summary>
    public async Task CompleteAsync(PasswordRecoveryDelivery delivery, DateTimeOffset now, bool accepted, CancellationToken cancellationToken)
    {
        var utc = now.UtcDateTime;
        var attempts = await context.PasswordRecoveryOutbox.AsNoTracking().Where(o => o.Id == delivery.RequestId &&
            o.LeaseOwner == delivery.LeaseOwner).Select(o => o.AttemptCount).SingleOrDefaultAsync(cancellationToken);
        var next = utc.AddMinutes(Math.Min(30, Math.Pow(2, Math.Min(attempts, 5))));
        await context.PasswordRecoveryOutbox.Where(o => o.Id == delivery.RequestId && o.LeaseOwner == delivery.LeaseOwner &&
            o.LeaseExpiresAt > utc && o.SentAt == null && o.ConsumedAt == null && o.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.AttemptCount, o => o.AttemptCount + 1)
                .SetProperty(o => o.SentAt, accepted ? utc : (DateTime?)null)
                .SetProperty(o => o.ProtectedPayload, o => accepted || o.ExpiresAt <= utc ? string.Empty : o.ProtectedPayload)
                .SetProperty(o => o.NextAttemptAt, o => !accepted && o.ExpiresAt > utc ? next : (DateTime?)null)
                .SetProperty(o => o.LeaseOwner, (Guid?)null)
                .SetProperty(o => o.LeaseExpiresAt, (DateTime?)null), cancellationToken);
    }

    /// <summary>Revoca y borra el secreto solamente cuando la reserva pertenece al intento indicado.</summary>
    private Task<int> RevokeAsync(Guid id, Guid owner, DateTime utc, CancellationToken cancellationToken) =>
        context.PasswordRecoveryOutbox.Where(o => o.Id == id && o.LeaseOwner == owner)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.RevokedAt, utc)
                .SetProperty(o => o.ProtectedPayload, string.Empty).SetProperty(o => o.NextAttemptAt, (DateTime?)null)
                .SetProperty(o => o.LeaseOwner, (Guid?)null).SetProperty(o => o.LeaseExpiresAt, (DateTime?)null), cancellationToken);

    /// <summary>Exige correo confirmado, contraseña y una combinación persistida válida de gimnasio y rol.</summary>
    private static bool Eligible(GymAccount user) => user.IsEnabled && user.EmailConfirmed && !string.IsNullOrEmpty(user.PasswordHash) &&
        !string.IsNullOrEmpty(user.SecurityStamp) && (user.GymId is null ? user.Role == AccountRole.SuperAdmin :
            user.GymId > 0 && user.Role is AccountRole.GymAdmin or AccountRole.Trainer or AccountRole.Student);

    /// <summary>Comprueba elegibilidad, ámbito, rol y vínculo criptográfico con el sello original.</summary>
    private static bool Matches(GymAccount user, PasswordRecoveryOutbox request) => Eligible(user) &&
        user.GymId == request.GymId && user.Role == request.Role && Bind(user.SecurityStamp) == request.StampBinding;

    /// <summary>Vincula la solicitud al sello sin almacenar otra copia en claro.</summary>
    private static string Bind(string? stamp) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp ?? string.Empty)));
}
