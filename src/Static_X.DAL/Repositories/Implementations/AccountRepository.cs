using DAL.Repositories.Interfaces;
using Entity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace DAL.Repositories.Implementation;

/// <summary>Persiste cuentas de gimnasio y autentica identidades con ámbito global o de gimnasio mediante Identity.</summary>
public sealed class AccountRepository(StaticXDbContext context, UserManager<GymAccount> users,
    SignInManager<GymAccount> signIn) : IAccountRepository
{
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
            .SingleOrDefaultAsync(u => u.Id == accountId && u.GymId == gymId && u.EmailConfirmed &&
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
            u => u.GymId == gymId && u.NormalizedEmail == normalized &&
                (gymId != null || u.Role == AccountRole.SuperAdmin), cancellationToken);
        if (user is null)
            return null;

        // Identity owns password verification, confirmed-email checks, lockout and cookie issuance.
        var result = await signIn.PasswordSignInAsync(user, password, isPersistent: false,
            lockoutOnFailure: true);
        return result.Succeeded ? new Account(user.Id, user.GymId, user.Email!, user.Role) : null;
    }
}
