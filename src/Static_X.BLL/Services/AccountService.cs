using AutoMapper;
using BLL.Services.Interfaces;
using DAL.Repositories.Interfaces;
using Entity;
using BLL.DTOs.Accounts;

namespace BLL;

/// <summary>Valida cuentas de gimnasio y autentica identidades con ámbito de gimnasio o global explícito.</summary>
public sealed class AccountService : IAccountService
{
    private readonly IAccountRepository repository;
    private readonly IMapper mapper;

    /// <summary>Recibe el repositorio de cuentas y el mapeador de sus identidades públicas.</summary>
    public AccountService(IAccountRepository repository, IMapper mapper)
    {
        this.repository = repository;
        this.mapper = mapper;
    }

    /// <summary>Valida el gimnasio, el rol y las credenciales antes de solicitar la creación de la cuenta.</summary>
    public async Task<AccountResponse> CreateAsync(int gymId, CreateAccountRequest request, AccountRole role,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gymId);
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(role) || role == AccountRole.SuperAdmin)
            throw new ArgumentOutOfRangeException(nameof(role));
        if (string.IsNullOrWhiteSpace(request.Password))
            throw new ArgumentException("A password is required.", "password");
        return mapper.Map<AccountResponse>(await repository.CreateAsync(gymId, RequiredEmail(request.Email), request.Password, role, cancellationToken));
    }

    /// <summary>Valida el gimnasio y el correo antes de buscar la cuenta dentro de ese gimnasio.</summary>
    public async Task<AccountResponse?> FindByEmailAsync(int gymId, string email, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gymId);
        return mapper.Map<AccountResponse?>(await repository.FindByEmailAsync(gymId, RequiredEmail(email), cancellationToken));
    }

    /// <summary>Valida las credenciales y selecciona únicamente el ámbito global o el gimnasio indicado.</summary>
    public async Task<AccountResponse?> SignInAsync(int? gymId, LoginRequest request,
        CancellationToken cancellationToken = default)
    {
        if (gymId <= 0 || request is null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            return null;
        // GymId in the HTTP request is a credential selector, never proof of membership.
        return mapper.Map<AccountResponse?>(await repository.SignInAsync(gymId, request.Email.Trim(), request.Password, cancellationToken));
    }

    /// <summary>Consulta una cuenta confirmada dentro del ámbito global o del gimnasio confiable indicado.</summary>
    public async Task<AccountResponse?> FindByIdAsync(int? gymId, string accountId, CancellationToken cancellationToken = default)
    {
        if (gymId <= 0 || string.IsNullOrWhiteSpace(accountId))
            return null;

        return mapper.Map<AccountResponse?>(await repository.FindByIdAsync(gymId, accountId, cancellationToken));
    }

    /// <summary>Exige un correo no vacío y elimina los espacios de sus extremos.</summary>
    private static string RequiredEmail(string? email) =>
        string.IsNullOrWhiteSpace(email)
            ? throw new ArgumentException("An email is required.", nameof(email))
            : email.Trim();
}
