using BLL;
using DAL.Repositories.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DAL.Repositories.Implementation;

public sealed class AccountRepository(dbcontext context, UserManager<GymAccount> users,
    SignInManager<GymAccount> signIn) : IAccountRepository
{
    public async Task<Account> CreateAsync(int gymId, string email, string password, AccountRole role,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        var user = new GymAccount
        {
            Id = id,
            UserName = id, // Identity's globally unique username is not the tenant-local email.
            Email = email,
            GymId = gymId,
            Role = role
        };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Account creation failed: {string.Join(", ", result.Errors.Select(e => e.Code))}");

        return new Account(user.Id, user.GymId, user.Email!, user.Role);
    }

    public async Task<Account?> FindByEmailAsync(int gymId, string email, CancellationToken cancellationToken)
    {
        var normalized = users.NormalizeEmail(email);
        var user = await context.Users.AsNoTracking()
            .Where(u => u.GymId == gymId && u.NormalizedEmail == normalized)
            .Select(u => new Account(u.Id, u.GymId, u.Email!, u.Role))
            .SingleOrDefaultAsync(cancellationToken);
        return user;
    }

    public async Task<Account?> FindByIdAsync(int gymId, string accountId, CancellationToken cancellationToken)
    {
        var user = await context.Users.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == accountId && u.GymId == gymId && u.EmailConfirmed,
                cancellationToken);
        return user is null ? null : new Account(user.Id, user.GymId, user.Email!, user.Role);
    }

    public async Task<Account?> SignInAsync(int gymId, string email, string password,
        CancellationToken cancellationToken)
    {
        var normalized = users.NormalizeEmail(email);
        var user = await context.Users.SingleOrDefaultAsync(
            u => u.GymId == gymId && u.NormalizedEmail == normalized, cancellationToken);
        if (user is null)
            return null;

        // Identity owns password verification, confirmed-email checks, lockout and cookie issuance.
        var result = await signIn.PasswordSignInAsync(user, password, isPersistent: false,
            lockoutOnFailure: true);
        return result.Succeeded ? new Account(user.Id, user.GymId, user.Email!, user.Role) : null;
    }
}
