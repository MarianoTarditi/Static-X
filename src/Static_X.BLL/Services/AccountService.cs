using AutoMapper;
using BLL.Services.Interfaces;
using DAL.Repositories.Interfaces;
using Entity;
using BLL.DTOs.Accounts;
using Microsoft.AspNetCore.DataProtection;

namespace BLL;

/// <summary>Valida cuentas y coordina autenticación, recuperación y administración acotada de entrenadores por gimnasio.</summary>
public sealed class AccountService : IAccountService
{
    private readonly IAccountRepository repository;
    private readonly IMapper mapper;
    private readonly IDataProtectionProvider protection;
    private readonly IConfiguration configuration;
    private readonly TimeProvider clock;

    /// <summary>Recibe persistencia de cuentas, mapeo público, protección, configuración y reloj de recuperación.</summary>
    public AccountService(IAccountRepository repository, IMapper mapper, IDataProtectionProvider protection,
        IConfiguration configuration, TimeProvider clock)
    {
        this.repository = repository;
        this.mapper = mapper;
        this.protection = protection;
        this.configuration = configuration;
        this.clock = clock;
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

    /// <summary>Valida las contraseñas y solicita el cambio únicamente para la identidad y el ámbito confiables.</summary>
    public Task<bool> ChangePasswordAsync(int? gymId, string accountId, ChangePasswordRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (gymId <= 0 || string.IsNullOrWhiteSpace(accountId) || request is null ||
            string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
            return Task.FromResult(false);

        return repository.ChangePasswordAsync(gymId, accountId, request.CurrentPassword, request.NewPassword, cancellationToken);
    }

    /// <summary>Comprueba dependencias antes de buscar y acepta uniformemente solicitudes válidas.</summary>
    public async Task<ForgotPasswordResponse> ForgotAsync(ForgotPasswordRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || request.GymId <= 0 || string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 256 ||
            !System.Net.Mail.MailAddress.TryCreate(request.Email.Trim(), out var email) || email.Address != request.Email.Trim())
            throw new ArgumentException("Invalid recovery request.");
        var ring = configuration["Activation:KeyRingPath"];
        if (string.IsNullOrWhiteSpace(ring) || !Path.IsPathFullyQualified(ring) || !Directory.Exists(ring))
            throw new InvalidOperationException("Persistent recovery keys are unavailable.");
        // Exercise the same key dependency before any account-dependent branch, including unknown addresses.
        var probe = protection.CreateProtector("Static-X.PasswordRecovery.Readiness.v1");
        if (probe.Unprotect(probe.Protect("ready")) != "ready")
            throw new InvalidOperationException("Recovery protection is unavailable.");
        await repository.EnqueueAsync(request.GymId, request.Email.Trim(), clock.GetUtcNow(), cancellationToken);
        return new ForgotPasswordResponse();
    }

    /// <summary>Rechaza entradas inválidas y solicita el consumo atómico del token y la actualización de Identity.</summary>
    public Task<string?> ResetAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Token) || request.Token.Length > 8192 ||
            string.IsNullOrWhiteSpace(request.NewPassword))
            return Task.FromResult<string?>(null);
        return repository.ResetAsync(request.Token, request.NewPassword, clock.GetUtcNow(), cancellationToken);
    }

    /// <summary>Valida la autoridad y los límites y convierte la página persistida de entrenadores sin credenciales.</summary>
    public async Task<TrainerListResponse> ListTrainersAsync(int gymId, AccountRole actorRole, TrainerListRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateTrainerActor(gymId, actorRole);
        ArgumentNullException.ThrowIfNull(request);
        var offset = ((long)request.Page - 1) * request.PageSize;
        if (request.Page < 1 || request.PageSize is < 1 or > 100 || offset > int.MaxValue)
            throw new ArgumentException("Invalid page request.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        var page = await repository.ListTrainersAsync(gymId, (int)offset, request.PageSize, cancellationToken);
        return new TrainerListResponse(mapper.Map<TrainerResponse[]>(page.Items), request.Page, request.PageSize, page.TotalCount);
    }

    /// <summary>Valida el administrador y el identificador opaco antes de cambiar el acceso de un entrenador del gimnasio.</summary>
    public Task<bool> SetTrainerEnabledAsync(int gymId, AccountRole actorRole, string accountId, bool enabled,
        CancellationToken cancellationToken = default)
    {
        ValidateTrainerActor(gymId, actorRole);
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > 450)
            throw new ArgumentException("Invalid account identifier.", nameof(accountId));
        cancellationToken.ThrowIfCancellationRequested();
        return repository.SetTrainerEnabledAsync(gymId, accountId, enabled, cancellationToken);
    }

    /// <summary>Exige el rol de administrador y un gimnasio confiable válido para administrar entrenadores.</summary>
    private static void ValidateTrainerActor(int gymId, AccountRole actorRole)
    {
        if (actorRole != AccountRole.GymAdmin) throw new UnauthorizedAccessException();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gymId);
    }

    /// <summary>Exige un correo no vacío y elimina los espacios de sus extremos.</summary>
    private static string RequiredEmail(string? email) =>
        string.IsNullOrWhiteSpace(email)
            ? throw new ArgumentException("An email is required.", nameof(email))
            : email.Trim();
}
