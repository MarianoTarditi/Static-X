using BLL;
using Entity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;

namespace DAL;

public sealed class ActivationStore(dbcontext context, UserManager<GymAccount> users) : IActivationStore
{
    public async Task<int> ProvisionAsync(string key, string gymName, string email, string tokenHash,
        Guid invitationId, string protectedToken, DateTimeOffset now, DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var existing = await context.ProvisioningRequests.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Key == key, cancellationToken);
        if (existing is not null)
        {
            return await VerifyExistingAsync(existing, gymName, email, cancellationToken);
        }

        var gym = new Gimnasio { Nombre = gymName };
        context.Gimnasios.Add(gym);
        await context.SaveChangesAsync(cancellationToken);
        var id = Guid.NewGuid().ToString("N");
        var accountToCreate = new GymAccount
        {
            Id = id, UserName = id, GymId = gym.Id, Email = email, Role = AccountRole.GymAdmin
        };
        var created = await users.CreateAsync(accountToCreate);
        if (!created.Succeeded)
            throw new InvalidOperationException("Account creation failed.");
        context.ProvisioningRequests.Add(new ProvisioningRequest { Key = key, GymId = gym.Id, AccountId = id });
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
                .SingleOrDefaultAsync(r => r.Key == key, cancellationToken);
            if (winner is null) throw;
            return await VerifyExistingAsync(winner, gymName, email, cancellationToken);
        }
        return gym.Id;
    }

    private async Task<int> VerifyExistingAsync(ProvisioningRequest existing, string gymName, string email,
        CancellationToken cancellationToken)
    {
        var account = await context.Users.AsNoTracking().SingleAsync(u => u.Id == existing.AccountId, cancellationToken);
        if (account.Role != AccountRole.GymAdmin ||
            account.NormalizedEmail != users.NormalizeEmail(email) ||
            await context.Gimnasios.Where(g => g.Id == existing.GymId)
                .Select(g => g.Nombre).SingleAsync(cancellationToken) != gymName)
            throw new InvalidOperationException("Provisioning key belongs to a different request.");
        return existing.GymId;
    }

    public async Task<bool> ActivateAsync(string tokenHash, string password, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var invitation = await context.ActivationInvitations.AsNoTracking()
            .SingleOrDefaultAsync(i => i.TokenHash == tokenHash && i.ConsumedAt == null && i.RevokedAt == null && i.ExpiresAt > now.UtcDateTime,
                cancellationToken);
        if (invitation is null)
            return false;
        var account = await context.Users.SingleOrDefaultAsync(u => u.Id == invitation.AccountId &&
            u.GymId == invitation.GymId && u.Role == AccountRole.GymAdmin && !u.EmailConfirmed &&
            u.PasswordHash == null, cancellationToken);
        if (account is null)
            return false;
        // Conditional SQL claim serializes contenders before Identity writes; rollback restores it on failure.
        var claimed = await context.ActivationInvitations
            .Where(i => i.Id == invitation.Id && i.ConsumedAt == null && i.RevokedAt == null && i.ExpiresAt > now.UtcDateTime)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ConsumedAt, now.UtcDateTime), cancellationToken);
        if (claimed != 1)
            return false;
        var result = await users.AddPasswordAsync(account, password);
        if (!result.Succeeded)
            return false;
        account.EmailConfirmed = true;
        result = await users.UpdateAsync(account);
        if (!result.Succeeded)
            return false;
        // Delivery secret is no longer needed after a successful claim.
        await context.ActivationOutbox.Where(o => o.InvitationId == invitation.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ProtectedToken, string.Empty), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ReissueAsync(int gymId, string accountId, string tokenHash, Guid invitationId,
        string protectedToken, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        // Serialize reissues for the same account; the condition excludes activated and cross-gym accounts.
        var claimed = await context.Users.Where(u => u.Id == accountId && u.GymId == gymId &&
                u.Role == AccountRole.GymAdmin && !u.EmailConfirmed && u.PasswordHash == null)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, Guid.NewGuid().ToString("N")), cancellationToken);
        if (claimed != 1 || !await context.ProvisioningRequests.AsNoTracking()
                .AnyAsync(r => r.GymId == gymId && r.AccountId == accountId, cancellationToken))
            return false;
        var invitations = await context.ActivationInvitations
            .Where(i => i.GymId == gymId && i.AccountId == accountId && i.ConsumedAt == null && i.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var invitation in invitations)
            invitation.RevokedAt = now.UtcDateTime;
        var oldIds = invitations.Select(i => i.Id).ToArray();
        await context.ActivationOutbox.Where(o => oldIds.Contains(o.InvitationId) && o.SentAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ProtectedToken, string.Empty)
                .SetProperty(o => o.NextAttemptAt, (DateTime?)null), cancellationToken);
        context.ActivationInvitations.Add(new ActivationInvitation
        {
            Id = invitationId, GymId = gymId, AccountId = accountId,
            TokenHash = tokenHash, ExpiresAt = expiresAt.UtcDateTime
        });
        context.ActivationOutbox.Add(new ActivationOutbox
        {
            Id = Guid.NewGuid(), InvitationId = invitationId, ProtectedToken = protectedToken,
            CreatedAt = now.UtcDateTime, NextAttemptAt = now.UtcDateTime
        });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
