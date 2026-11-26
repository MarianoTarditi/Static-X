using DAL.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;
using Entity;

namespace DAL.Repositories.Implementation;

/// <summary>Administra las reservas temporales, la vigencia y los reintentos de los envíos de activación pendientes.</summary>
public sealed class ActivationDeliveryRepository : IActivationDeliveryRepository
{
    private readonly StaticXDbContext context;

    /// <summary>Recibe el contexto que conserva invitaciones y reservas de entrega.</summary>
    public ActivationDeliveryRepository(StaticXDbContext context)
    {
        this.context = context;
    }

    /// <summary>Selecciona invitaciones con rol y ámbito vigentes, exigiendo vínculo activo para cuentas Student.</summary>
    private IQueryable<ActivationInvitation> EligibleInvitations(DateTime utc) => context.ActivationInvitations.Where(i =>
        i.ExpiresAt > utc && i.RevokedAt == null && i.ConsumedAt == null &&
        context.Users.Any(u => u.Id == i.AccountId && u.GymId == i.GymId && u.IsEnabled &&
            u.Role == i.ExpectedRole && !u.EmailConfirmed && u.PasswordHash == null &&
            (i.ExpectedRole == AccountRole.GymAdmin ||
                context.TenantInvitationRequests.Any(r => r.GymId == i.GymId && r.AccountId == u.Id &&
                    r.Role == i.ExpectedRole && r.NormalizedEmail == u.NormalizedEmail &&
                    (r.Role == AccountRole.Trainer || r.Role == AccountRole.Student &&
                        context.Alumnos.Any(s => s.Id == r.StudentId && s.GymId == i.GymId && s.IsActive && s.AccountId == u.Id))))));

    /// <summary>Descarta tokens de invitaciones terminadas e intenta reservar durante cinco minutos un envío pendiente y vigente.</summary>
    public async Task<ActivationDelivery?> LeaseDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var utc = now.UtcDateTime;
        // Discard secrets for terminal jobs even if no future dispatch can claim them.
        await context.ActivationOutbox.Where(o => o.ProtectedToken != string.Empty &&
            context.ActivationInvitations.Any(i => i.Id == o.InvitationId &&
                (i.ExpiresAt <= utc || i.RevokedAt != null || i.ConsumedAt != null)))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ProtectedToken, string.Empty)
                .SetProperty(o => o.NextAttemptAt, (DateTime?)null), cancellationToken);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = await (from o in context.ActivationOutbox.AsNoTracking()
                join i in EligibleInvitations(utc).AsNoTracking() on o.InvitationId equals i.Id
                join u in context.Users.AsNoTracking() on i.AccountId equals u.Id
                where o.SentAt == null && o.ProtectedToken != string.Empty && o.NextAttemptAt <= utc &&
                    (o.LeaseExpiresAt == null || o.LeaseExpiresAt <= utc) &&
                    i.ExpiresAt > utc && i.RevokedAt == null && i.ConsumedAt == null &&
                    u.GymId == i.GymId && !u.EmailConfirmed && u.PasswordHash == null
                orderby o.NextAttemptAt, o.Id
                select new { o.Id, o.InvitationId }).FirstOrDefaultAsync(cancellationToken);
            if (candidate is null) return null;
            var owner = Guid.NewGuid();
            var claimed = await context.ActivationOutbox.Where(o => o.Id == candidate.Id &&
                    o.SentAt == null && o.ProtectedToken != string.Empty && o.NextAttemptAt <= utc &&
                    (o.LeaseExpiresAt == null || o.LeaseExpiresAt <= utc) &&
                    EligibleInvitations(utc).Any(i => i.Id == o.InvitationId))
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.LeaseOwner, owner)
                    .SetProperty(o => o.LeaseExpiresAt, utc.AddMinutes(5)), cancellationToken);
            if (claimed != 1) continue;
            return await (from o in context.ActivationOutbox.AsNoTracking()
                join i in EligibleInvitations(utc).AsNoTracking() on o.InvitationId equals i.Id
                join u in context.Users.AsNoTracking() on i.AccountId equals u.Id
                where o.Id == candidate.Id && o.LeaseOwner == owner && u.GymId == i.GymId
                select new ActivationDelivery(o.Id, i.Id, owner, u.Email!, o.ProtectedToken))
                .SingleOrDefaultAsync(cancellationToken);
        }
        return null;
    }

    /// <summary>Comprueba que la reserva siga vigente y que la invitación y la cuenta asociadas aún permitan el envío.</summary>
    public Task<bool> IsCurrentAsync(ActivationDelivery delivery, DateTimeOffset now, CancellationToken cancellationToken) =>
        context.ActivationOutbox.AsNoTracking().AnyAsync(o => o.Id == delivery.OutboxEntryId &&
            o.LeaseOwner == delivery.LeaseId && o.LeaseExpiresAt > now.UtcDateTime &&
            o.SentAt == null && o.ProtectedToken != string.Empty &&
            EligibleInvitations(now.UtcDateTime).Any(i => i.Id == o.InvitationId), cancellationToken);

    /// <summary>Registra la aceptación del proveedor o programa un reintento si la invitación sigue vigente, y libera la reserva del intento.</summary>
    public async Task CompleteAsync(ActivationDelivery delivery, DateTimeOffset now, bool accepted,
        CancellationToken cancellationToken)
    {
        var utc = now.UtcDateTime;
        // An unknown provider outcome is retried with the same provider idempotency key.
        var count = await context.ActivationOutbox.AsNoTracking()
            .Where(o => o.Id == delivery.OutboxEntryId && o.LeaseOwner == delivery.LeaseId)
            .Select(o => o.AttemptCount).SingleOrDefaultAsync(cancellationToken);
        var delay = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Min(count, 6))));
        await context.ActivationOutbox.Where(o => o.Id == delivery.OutboxEntryId &&
                o.LeaseOwner == delivery.LeaseId && o.SentAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.AttemptCount, o => o.AttemptCount + 1)
                .SetProperty(o => o.SentAt, accepted ? utc : (DateTime?)null)
                .SetProperty(o => o.ProtectedToken, o => !accepted && o.ProtectedToken != string.Empty &&
                    context.ActivationInvitations.Any(i => i.Id == o.InvitationId &&
                        i.ExpiresAt > utc && i.RevokedAt == null && i.ConsumedAt == null)
                    ? o.ProtectedToken : string.Empty)
                .SetProperty(o => o.NextAttemptAt, o => !accepted && o.ProtectedToken != string.Empty &&
                    context.ActivationInvitations.Any(i => i.Id == o.InvitationId &&
                        i.ExpiresAt > utc && i.RevokedAt == null && i.ConsumedAt == null)
                    ? utc.Add(delay) : (DateTime?)null)
                .SetProperty(o => o.LeaseOwner, (Guid?)null)
                .SetProperty(o => o.LeaseExpiresAt, (DateTime?)null), cancellationToken);
    }
}
