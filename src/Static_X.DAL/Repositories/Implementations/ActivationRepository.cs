using DAL.Repositories.Interfaces;
using DAL.Repositories.Exceptions;
using Entity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;

namespace DAL.Repositories.Implementation;

/// <summary>Persiste el alta de gimnasios y administra sus invitaciones de activación mediante transacciones.</summary>
public sealed class ActivationRepository : IActivationRepository
{
    private readonly StaticXDbContext context;
    private readonly UserManager<IdentityAccount> users;

    /// <summary>Recibe el contexto transaccional y el administrador de cuentas Identity.</summary>
    public ActivationRepository(StaticXDbContext context, UserManager<IdentityAccount> users)
    {
        this.context = context;
        this.users = users;
    }

    /// <summary>Selecciona cuentas pendientes con procedencia documentada y vínculo de ficha explícito cuando corresponde.</summary>
    private IQueryable<IdentityAccount> PendingTargets(int gymId, AccountRole role, bool requireActive) => context.Users.Where(u =>
        u.GymId == gymId && u.Role == role && !u.EmailConfirmed && u.PasswordHash == null && (!requireActive || u.IsEnabled) &&
        (role == AccountRole.GymAdmin
            ? context.ProvisioningRequests.Any(r => r.GymId == gymId && r.InitialAdminAccountId == u.Id)
            : (role == AccountRole.Trainer || role == AccountRole.Student) &&
                context.TenantInvitationRequests.Any(r => r.GymId == gymId && r.AccountId == u.Id && r.Role == role &&
                    r.NormalizedEmail == u.NormalizedEmail && (role == AccountRole.Trainer ||
                        context.Alumnos.Any(s => s.Id == r.StudentId && s.GymId == gymId && s.AccountId == u.Id && (!requireActive || s.IsActive))))));

    /// <summary>Obtiene el historial de emisión con la fecha inmutable del envío, sin depender de su entrega ni de su vigencia.</summary>
    private IQueryable<InvitationLifecycleItem> History(int gymId, string accountId, AccountRole role) =>
        from i in context.ActivationInvitations.AsNoTracking()
        join o in context.ActivationOutbox.AsNoTracking() on i.Id equals o.InvitationId
        where i.GymId == gymId && i.AccountId == accountId && i.ExpectedRole == role
        select new InvitationLifecycleItem(i.AccountId, string.Empty, role, o.CreatedAt, i.ExpiresAt, i.RevokedAt);

    /// <summary>Consulta una página del último envío de cada cuenta pendiente, conservando estados cancelados y vencidos.</summary>
    public async Task<DAL.Repositories.Models.InvitationLifecyclePage> ListInvitationsAsync(int gymId, AccountRole role, int offset,
        int pageSize, CancellationToken cancellationToken)
    {
        if (gymId <= 0 || offset is < 0 or > 100000 || pageSize is < 1 or > 100) throw new ArgumentException("Invalid invitation page.");
        var query = from u in PendingTargets(gymId, role, false).AsNoTracking()
            join i in context.ActivationInvitations.AsNoTracking() on u.Id equals i.AccountId
            join o in context.ActivationOutbox.AsNoTracking() on i.Id equals o.InvitationId
            where i.GymId == gymId && i.ExpectedRole == role && i.ConsumedAt == null &&
                o.Id == (from last in context.ActivationOutbox
                    join invitation in context.ActivationInvitations on last.InvitationId equals invitation.Id
                    where invitation.GymId == gymId && invitation.AccountId == u.Id && invitation.ExpectedRole == role
                    orderby last.CreatedAt descending, last.Id descending
                    select last.Id).FirstOrDefault()
            select new { u.Id, u.Email, o.CreatedAt, i.ExpiresAt, i.RevokedAt };
        var count = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(i => i.Id).Skip(offset).Take(pageSize)
            .Select(i => new InvitationLifecycleItem(i.Id, i.Email!, role, i.CreatedAt, i.ExpiresAt, i.RevokedAt)).ToListAsync(cancellationToken);
        return new DAL.Repositories.Models.InvitationLifecyclePage(items, count);
    }

    /// <summary>Exige una hora desde el último envío histórico, incluso si fue cancelado, enviado o vencido.</summary>
    private async Task RequireResendIntervalAsync(int gymId, string accountId, AccountRole role, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var last = await (from i in context.ActivationInvitations
            join o in context.ActivationOutbox on i.Id equals o.InvitationId
            where i.GymId == gymId && i.AccountId == accountId && i.ExpectedRole == role
            select (DateTime?)o.CreatedAt).MaxAsync(cancellationToken);
        if (last is null) throw new ProvisioningConflictException();
        if (last.Value > now.UtcDateTime.AddHours(-1)) throw new DAL.Repositories.Models.InvitationCooldownException();
    }

    /// <summary>Revoca enlaces no consumidos y elimina secretos y reservas sin borrar el historial de emisión.</summary>
    private async Task RevokePendingAsync(int gymId, string accountId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ids = context.ActivationInvitations.Where(i => i.GymId == gymId && i.AccountId == accountId && i.ConsumedAt == null).Select(i => i.Id);
        await context.ActivationInvitations.Where(i => i.GymId == gymId && i.AccountId == accountId && i.ConsumedAt == null && i.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.RevokedAt, now.UtcDateTime), cancellationToken);
        await context.ActivationOutbox.Where(o => ids.Contains(o.InvitationId))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ProtectedToken, string.Empty).SetProperty(o => o.NextAttemptAt, (DateTime?)null)
                .SetProperty(o => o.LeaseOwner, (Guid?)null).SetProperty(o => o.LeaseExpiresAt, (DateTime?)null), cancellationToken);
    }

    /// <summary>Cancela de forma idempotente bajo el mismo bloqueo utilizado por activación y reenvío.</summary>
    public async Task<bool> CancelInvitationAsync(int gymId, string accountId, AccountRole role, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (await TenantAccountLock.AcquireAsync(context, gymId, cancellationToken) != 1 ||
            !await PendingTargets(gymId, role, false).AnyAsync(u => u.Id == accountId, cancellationToken) ||
            !await History(gymId, accountId, role).AnyAsync(cancellationToken)) return false;
        await RevokePendingAsync(gymId, accountId, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>Reemplaza una invitación documentada bajo bloqueo del gimnasio y aplica el intervalo antes de escribir.</summary>
    public async Task<bool> ResendInvitationAsync(int gymId, string accountId, AccountRole role, string tokenHash, Guid invitationId,
        string protectedToken, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (await TenantAccountLock.AcquireAsync(context, gymId, cancellationToken) != 1 ||
            !await PendingTargets(gymId, role, true).AnyAsync(u => u.Id == accountId, cancellationToken) ||
            !await History(gymId, accountId, role).AnyAsync(cancellationToken)) return false;
        await RequireResendIntervalAsync(gymId, accountId, role, now, cancellationToken);
        await RevokePendingAsync(gymId, accountId, now, cancellationToken);
        context.ActivationInvitations.Add(new ActivationInvitation
        { Id = invitationId, GymId = gymId, AccountId = accountId, ExpectedRole = role, TokenHash = tokenHash, ExpiresAt = expiresAt.UtcDateTime });
        context.ActivationOutbox.Add(new ActivationOutbox
        { Id = Guid.NewGuid(), InvitationId = invitationId, ProtectedToken = protectedToken, CreatedAt = now.UtcDateTime, NextAttemptAt = now.UtcDateTime });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>Crea o reemite una invitación con auditoría de entrada y vínculo explícito, serializando los escritores del gimnasio.</summary>
    public async Task<Account?> InviteAsync(int gymId, string key, string email, AccountRole role, int? studentId, string tokenHash,
        Guid invitationId, string protectedToken, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        if (gymId <= 0 || role is not (AccountRole.Trainer or AccountRole.Student) ||
            (role == AccountRole.Student ? studentId is null or <= 0 : studentId is not null))
            throw new ArgumentException("Invalid invitation target.");
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (await TenantAccountLock.AcquireAsync(context, gymId, cancellationToken) != 1) return null;
        var normalized = users.NormalizeEmail(email);
        if (string.IsNullOrEmpty(normalized) || normalized.Length > 256)
            throw new ArgumentException("Invalid normalized invitation email.");
        var previous = await context.TenantInvitationRequests.AsNoTracking()
            .SingleOrDefaultAsync(r => r.GymId == gymId && r.Key == key, cancellationToken);
        if (previous is not null)
        {
            if (previous.NormalizedEmail != normalized || previous.Role != role || previous.StudentId != studentId)
                throw new ProvisioningConflictException();
            return new Account(previous.AccountId, previous.GymId, previous.NormalizedEmail, previous.Role);
        }

        Student? student = null;
        if (studentId is int selectedId)
        {
            student = await context.Alumnos.SingleOrDefaultAsync(s => s.Id == selectedId && s.GymId == gymId, cancellationToken);
            if (student is null) return null;
            if (!student.IsActive) throw new ProvisioningConflictException();
        }
        var account = await context.Users.SingleOrDefaultAsync(u => u.GymId == gymId && u.NormalizedEmail == normalized, cancellationToken);
        if (account is null)
        {
            if (student?.AccountId is not null) throw new ProvisioningConflictException();
            var id = Guid.NewGuid().ToString("N");
            account = new IdentityAccount { Id = id, UserName = id, Email = email, GymId = gymId, Role = role };
            if (!(await users.CreateAsync(account)).Succeeded) throw new ProvisioningConflictException();
            if (student is not null)
            {
                // Pair the invitation claim with the unlinked writer's AccountId concurrency check.
                // The active predicate is rechecked in the write, not just the earlier selection.
                var linked = await context.Alumnos.Where(s => s.Id == studentId && s.GymId == gymId && s.IsActive && s.AccountId == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(s => s.AccountId, id), cancellationToken);
                if (linked != 1) throw new ProvisioningConflictException();
                await context.Entry(student).ReloadAsync(cancellationToken);
            }
        }
        else
        {
            if (account.Role != role || !account.IsEnabled || account.EmailConfirmed || account.PasswordHash is not null ||
                (role == AccountRole.Student && student?.AccountId != account.Id) ||
                !await context.TenantInvitationRequests.AsNoTracking().AnyAsync(r => r.GymId == gymId && r.AccountId == account.Id &&
                    r.Role == role && r.StudentId == studentId && r.NormalizedEmail == normalized, cancellationToken))
                throw new ProvisioningConflictException();
            await RequireResendIntervalAsync(gymId, account.Id, role, now, cancellationToken);
            await RevokePendingAsync(gymId, account.Id, now, cancellationToken);
        }
        context.TenantInvitationRequests.Add(new TenantInvitationRequest
        { GymId = gymId, Key = key, NormalizedEmail = normalized, Role = role, StudentId = studentId, AccountId = account.Id });
        context.ActivationInvitations.Add(new ActivationInvitation
        { Id = invitationId, GymId = gymId, AccountId = account.Id, ExpectedRole = role, TokenHash = tokenHash, ExpiresAt = expiresAt.UtcDateTime });
        context.ActivationOutbox.Add(new ActivationOutbox
        { Id = Guid.NewGuid(), InvitationId = invitationId, ProtectedToken = protectedToken, CreatedAt = now.UtcDateTime, NextAttemptAt = now.UtcDateTime });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new Account(account.Id, gymId, account.Email!, role);
    }

    /// <summary>Crea gimnasio, administrador, invitación y envío pendiente en una transacción, o valida una solicitud previa con la misma clave.</summary>
    public async Task<int> ProvisionAsync(string key, string gymName, string email, string tokenHash,
        Guid invitationId, string protectedToken, DateTimeOffset now, DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var existing = await context.ProvisioningRequests.AsNoTracking()
            .SingleOrDefaultAsync(r => r.IdempotencyKey == key, cancellationToken);
        if (existing is not null)
        {
            return await VerifyExistingAsync(existing, gymName, email, cancellationToken);
        }

        var gym = new Gym { Name = gymName };
        context.Gimnasios.Add(gym);
        await context.SaveChangesAsync(cancellationToken);
        var id = Guid.NewGuid().ToString("N");
        var accountToCreate = new IdentityAccount
        {
            Id = id, UserName = id, GymId = gym.Id, Email = email, Role = AccountRole.GymAdmin
        };
        var created = await users.CreateAsync(accountToCreate);
        if (!created.Succeeded)
            throw new InvalidOperationException("Account creation failed.");
        context.ProvisioningRequests.Add(new GymProvisioningRecord { IdempotencyKey = key, GymId = gym.Id, InitialAdminAccountId = id });
        context.ActivationInvitations.Add(new ActivationInvitation
        {
            Id = invitationId, GymId = gym.Id, AccountId = id, TokenHash = tokenHash, ExpiresAt = expiresAt.UtcDateTime
        });
        context.ActivationOutbox.Add(new ActivationOutbox
        {
            Id = Guid.NewGuid(), InvitationId = invitationId, ProtectedToken = protectedToken,
            CreatedAt = now.UtcDateTime, NextAttemptAt = now.UtcDateTime
        });
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            await transaction.RollbackAsync(cancellationToken);
            // A competing transaction committed the same key while this transaction was open.
            // Recheck the committed request; do not turn an unrelated unique violation into success.
            var winner = await context.ProvisioningRequests.AsNoTracking()
                .SingleOrDefaultAsync(r => r.IdempotencyKey == key, cancellationToken);
            if (winner is null) throw;
            return await VerifyExistingAsync(winner, gymName, email, cancellationToken);
        }
        return gym.Id;
    }

    /// <summary>Comprueba que la clave previa corresponda al nombre de gimnasio, correo normalizado y rol de administrador solicitados.</summary>
    private async Task<int> VerifyExistingAsync(GymProvisioningRecord existing, string gymName, string email,
        CancellationToken cancellationToken)
    {
        var account = await context.Users.AsNoTracking().SingleAsync(u => u.Id == existing.InitialAdminAccountId, cancellationToken);
        if (account.Role != AccountRole.GymAdmin ||
            account.NormalizedEmail != users.NormalizeEmail(email) ||
            await context.Gimnasios.Where(g => g.Id == existing.GymId)
                .Select(g => g.Name).SingleAsync(cancellationToken) != gymName)
            throw new ProvisioningConflictException();
        return existing.GymId;
    }

    /// <summary>Activa una invitación vigente y devuelve la cuenta persistida solo después de confirmar la transacción.</summary>
    public async Task<Account?> ActivateAsync(string tokenHash, string password, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var invitation = await context.ActivationInvitations.AsNoTracking()
            .SingleOrDefaultAsync(i => i.TokenHash == tokenHash && i.ConsumedAt == null && i.RevokedAt == null && i.ExpiresAt > now.UtcDateTime,
                cancellationToken);
        if (invitation is null)
            return null;
        await TenantAccountLock.AcquireAsync(context, invitation.GymId, cancellationToken);
        var account = await context.Users.SingleOrDefaultAsync(u => u.Id == invitation.AccountId &&
            u.GymId == invitation.GymId && u.Role == invitation.ExpectedRole && u.IsEnabled && !u.EmailConfirmed &&
            u.PasswordHash == null, cancellationToken);
        if (account is null || account.GymId is not int gymId || gymId <= 0)
            return null;
        if (invitation.ExpectedRole is not (AccountRole.GymAdmin or AccountRole.Trainer or AccountRole.Student)) return null;
        if (invitation.ExpectedRole != AccountRole.GymAdmin && !await context.TenantInvitationRequests.AsNoTracking()
            .AnyAsync(r => r.GymId == gymId && r.AccountId == account.Id && r.Role == invitation.ExpectedRole &&
                r.NormalizedEmail == account.NormalizedEmail && (r.Role == AccountRole.Trainer ||
                    context.Alumnos.Any(s => s.Id == r.StudentId && s.GymId == gymId && s.IsActive && s.AccountId == account.Id)), cancellationToken))
            return null;
        // Conditional SQL claim serializes contenders before Identity writes; rollback restores it on failure.
        var claimed = await context.ActivationInvitations
            .Where(i => i.Id == invitation.Id && i.ConsumedAt == null && i.RevokedAt == null && i.ExpiresAt > now.UtcDateTime)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ConsumedAt, now.UtcDateTime), cancellationToken);
        if (claimed != 1)
            return null;
        var result = await users.AddPasswordAsync(account, password);
        if (!result.Succeeded)
            return null;
        account.EmailConfirmed = true;
        result = await users.UpdateAsync(account);
        if (!result.Succeeded)
            return null;
        // Delivery secret is no longer needed after a successful claim.
        await context.ActivationOutbox.Where(o => o.InvitationId == invitation.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ProtectedToken, string.Empty), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new Account(account.Id, gymId, account.Email!, account.Role);
    }

    /// <summary>Para un administrador pendiente del gimnasio indicado, revoca invitaciones previas y crea una nueva con su envío en una transacción.</summary>
    public Task<bool> ReissueAsync(int gymId, string accountId, string tokenHash, Guid invitationId,
        string protectedToken, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken)
        => ResendInvitationAsync(gymId, accountId, AccountRole.GymAdmin, tokenHash, invitationId, protectedToken, now, expiresAt, cancellationToken);
}
