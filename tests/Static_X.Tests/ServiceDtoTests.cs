using BLL;
using BLL.DTOs.Accounts;
using BLL.DTOs.Gyms;
using DAL.Repositories.Interfaces;
using Entity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Static_X.Tests;

public sealed class ServiceDtoTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData(7, true)]
    [InlineData(7, false)]
    public async Task ChangePassword_Service_PreservesTrustedScopePasswordsCancellationAndRepositoryOutcome(int? gym, bool outcome)
    {
        var repository = new Accounts { ChangeResult = outcome };
        using var cancellation = new CancellationTokenSource();
        var result = await CreateAccountService(repository).ChangePasswordAsync(gym, "trusted-id",
            new ChangePasswordRequest { CurrentPassword = " current ", NewPassword = " replacement " }, cancellation.Token);
        Assert.Equal(outcome, result);
        Assert.Equal("change-password", repository.Operation);
        Assert.Equal(gym, repository.Gym);
        Assert.Equal("trusted-id", repository.AccountId);
        Assert.True(repository.Password == " current " && repository.NewPassword == " replacement ", "Passwords must be forwarded unchanged.");
        Assert.Equal(cancellation.Token, repository.Token);
        Assert.Equal(1, repository.Calls);
    }

    [Theory]
    [InlineData("null-request")]
    [InlineData("null-current")]
    [InlineData("null-new")]
    [InlineData("empty-current")]
    [InlineData("empty-new")]
    [InlineData("blank-current")]
    [InlineData("blank-new")]
    [InlineData("null-id")]
    [InlineData("blank-id")]
    [InlineData("zero-gym")]
    [InlineData("negative-gym")]
    public async Task ChangePassword_Service_IndependentlyRejectsIncompleteInputWithoutRepository(string scenario)
    {
        var repository = new Accounts();
        var request = scenario == "null-request" ? null : new ChangePasswordRequest
        {
            CurrentPassword = scenario == "null-current" ? null! : scenario == "empty-current" ? "" : scenario == "blank-current" ? " " : "current",
            NewPassword = scenario == "null-new" ? null! : scenario == "empty-new" ? "" : scenario == "blank-new" ? " " : "replacement"
        };
        Assert.False(await CreateAccountService(repository).ChangePasswordAsync(
            scenario == "zero-gym" ? 0 : scenario == "negative-gym" ? -1 : 7,
            scenario == "null-id" ? null! : scenario == "blank-id" ? " " : "id", request!));
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task ChangePassword_Service_CancellationPropagatesWithoutRepository()
    {
        var repository = new Accounts();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => CreateAccountService(repository)
            .ChangePasswordAsync(7, "id", new ChangePasswordRequest { CurrentPassword = "current", NewPassword = "replacement" }, cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, repository.Calls);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("email")]
    [InlineData("id")]
    [InlineData("signin")]
    public async Task Account_AllOperations_MapRepositoryIdentityAndPreserveTrustedArguments(string operation)
    {
        var repository = new Accounts { Result = new Account("persisted-id", 7, "persisted@example.com", AccountRole.Trainer) };
        var service = CreateAccountService(repository);
        using var cancellation = new CancellationTokenSource();
        var result = operation switch
        {
            "create" => await service.CreateAsync(gymId: 7, request: new CreateAccountRequest(" input@example.com ", " password "), role: AccountRole.GymAdmin, cancellationToken: cancellation.Token),
            "email" => await service.FindByEmailAsync(7, " input@example.com ", cancellation.Token),
            "id" => await service.FindByIdAsync(7, " target-id ", cancellation.Token),
            _ => await service.SignInAsync(7, new LoginRequest { GymId = 999, Email = " input@example.com ", Password = " password " }, cancellation.Token)
        };
        Assert.NotNull(result);
        Assert.Equal(("persisted-id", 7, "persisted@example.com", AccountRole.Trainer),
            (result.Id, result.GymId, result.Email, result.Role));
        Assert.Equal(operation, repository.Operation);
        Assert.Equal(7, repository.Gym);
        Assert.Equal(cancellation.Token, repository.Token);
        Assert.Equal(1, repository.Calls);
        if (operation == "id") Assert.Equal(" target-id ", repository.AccountId);
        else Assert.Equal("input@example.com", repository.Email);
        if (operation is "signin" or "create") Assert.Equal(" password ", repository.Password);
        if (operation == "create") Assert.Equal(AccountRole.GymAdmin, repository.Role);
    }

    [Theory]
    [InlineData("email")]
    [InlineData("id")]
    [InlineData("signin")]
    public async Task Account_AbsentRepositoryResult_RemainsNull(string operation)
    {
        var repository = new Accounts();
        var service = CreateAccountService(repository);
        var result = operation switch
        {
            "email" => await service.FindByEmailAsync(7, "a@example.com"),
            "id" => await service.FindByIdAsync(7, "missing-id"),
            _ => await service.SignInAsync(7, new LoginRequest { GymId = 7, Email = "a@example.com", Password = "wrong" })
        };
        Assert.Null(result);
        Assert.Equal(1, repository.Calls);
        Assert.Equal(default, repository.Token);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("email-null")]
    [InlineData("email-blank")]
    [InlineData("password-null")]
    [InlineData("password-blank")]
    [InlineData("gym-zero")]
    [InlineData("gym-negative")]
    public async Task Account_IncompleteSignIn_ReturnsNullWithoutRepository(string scenario)
    {
        var repository = new Accounts();
        var request = scenario == "null" ? null : new LoginRequest
        {
            GymId = 7,
            Email = scenario == "email-null" ? null! : scenario == "email-blank" ? " " : "a@example.com",
            Password = scenario == "password-null" ? null! : scenario == "password-blank" ? " " : "password"
        };
        Assert.Null(await CreateAccountService(repository).SignInAsync(scenario == "gym-zero" ? 0 : scenario == "gym-negative" ? -1 : 7, request!));
        Assert.Equal(0, repository.Calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("email-null")]
    [InlineData("email-blank")]
    [InlineData("password-null")]
    [InlineData("password-blank")]
    [InlineData("role")]
    [InlineData("gym")]
    public async Task Account_InvalidCreation_RejectsWithoutRepository(string scenario)
    {
        var repository = new Accounts();
        var service = CreateAccountService(repository);
        var request = scenario == "null" ? null : new CreateAccountRequest(
            scenario == "email-null" ? null! : scenario == "email-blank" ? " " : "a@example.com",
            scenario == "password-null" ? null! : scenario == "password-blank" ? " " : "password");
        var exception = await Record.ExceptionAsync(() => service.CreateAsync(scenario == "gym" ? 0 : 7, request!,
            scenario == "role" ? (AccountRole)99 : AccountRole.Student));
        if (scenario == "null") Assert.IsType<ArgumentNullException>(exception);
        else if (scenario is "gym" or "role") Assert.IsType<ArgumentOutOfRangeException>(exception);
        else Assert.IsType<ArgumentException>(exception);
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task Account_InvalidLookups_PreserveExceptionVersusNullSemantics()
    {
        var repository = new Accounts();
        var service = CreateAccountService(repository);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.FindByEmailAsync(0, "a@example.com"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.FindByEmailAsync(7, null!));
        await Assert.ThrowsAsync<ArgumentException>(() => service.FindByEmailAsync(7, " "));
        Assert.Null(await service.FindByIdAsync(0, "id"));
        Assert.Null(await service.FindByIdAsync(7, null!));
        Assert.Null(await service.FindByIdAsync(7, " "));
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task Gym_Create_MapsPersistedEntityAndForwardsCancellation()
    {
        var repository = new Gyms();
        using var cancellation = new CancellationTokenSource();
        var result = await new GymService(repository, TestMapper.Instance).AddAsync(request: new CreateGymRequest(" North "), cancellationToken: cancellation.Token);
        Assert.Equal((29, "Persisted gym"), (result.Id, result.Nombre));
        Assert.NotNull(repository.Added);
        Assert.Equal((0, "North"), (repository.Added.Id, repository.Added.Name));
        Assert.Equal(cancellation.Token, repository.Token);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("name-null")]
    [InlineData("blank")]
    public async Task Gym_InvalidRequest_RejectsWithoutPersistence(string scenario)
    {
        var repository = new Gyms();
        var request = scenario == "null" ? null : new CreateGymRequest(scenario == "name-null" ? null! : " ");
        var exception = await Record.ExceptionAsync(() => new GymService(repository, TestMapper.Instance).AddAsync(request!));
        if (scenario == "null") Assert.IsType<ArgumentNullException>(exception);
        else Assert.IsType<ArgumentException>(exception);
        Assert.Null(repository.Added);
    }

    [Fact]
    public async Task Account_GlobalSelector_MapsNullGymAndIgnoresDtoGymForTrustedScope()
    {
        var repository = new Accounts { Result = new Account("global", null, "global@example.com", AccountRole.SuperAdmin) };
        var service = CreateAccountService(repository);
        var result = await service.SignInAsync(null, new LoginRequest { GymId = 999, Email = " global@example.com ", Password = "GlobalPassword1!" });
        Assert.Null(repository.Gym);
        Assert.Equal("global@example.com", repository.Email);
        Assert.Null(result!.GymId);
        Assert.Equal(AccountRole.SuperAdmin, result.Role);
        Assert.Equal("global", result.Id);
        Assert.Equal("global", (await service.FindByIdAsync(null, "global"))!.Id);
        Assert.Null(repository.Gym);
    }

    private static AccountService CreateAccountService(IAccountRepository repository) =>
        new(repository, TestMapper.Instance, new EphemeralDataProtectionProvider(), new ConfigurationBuilder().Build(), TimeProvider.System);

    private sealed class Gyms : IGymRepository
    {
        public Task<global::DAL.Repositories.Models.GymPage> ListAsync(int offset, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<global::DAL.Repositories.Models.PendingAdministratorPage?> ListPendingAdministratorsAsync(int gymId, int offset, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Gym? Added { get; private set; }
        public CancellationToken Token { get; private set; }

        public Task<Gym> AddAsync(Gym gym, CancellationToken cancellationToken)
        {
            Added = gym;
            Token = cancellationToken;
            return Task.FromResult(new Gym { Id = 29, Name = "Persisted gym" });
        }
    }

    private sealed class Accounts : IAccountRepository
    {
        public Task<global::DAL.Repositories.Models.TrainerAccountPage> ListTrainersAsync(int gymId, int offset, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SetTrainerEnabledAsync(int gymId, string accountId, bool enabled, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task EnqueueAsync(int? gymId, string email, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> ResetAsync(string token, string password, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CleanupAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<global::DAL.Repositories.Models.PasswordRecoveryDelivery?> LeaseDueAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> FreezeDeliveryAsync(global::DAL.Repositories.Models.PasswordRecoveryDelivery delivery, string protectedSnapshot, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsCurrentAsync(global::DAL.Repositories.Models.PasswordRecoveryDelivery delivery, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteAsync(global::DAL.Repositories.Models.PasswordRecoveryDelivery delivery, DateTimeOffset now, bool accepted, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> ChangePasswordAsync(int? gymId, string accountId, string currentPassword, string newPassword,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Capture("change-password", gymId, null, currentPassword, cancellationToken);
            AccountId = accountId;
            NewPassword = newPassword;
            return Task.FromResult(ChangeResult);
        }

        public bool ChangeResult { get; init; }
        public string? NewPassword { get; private set; }
        public Account? Result { get; init; }
        public string? Operation { get; private set; }
        public int? Gym { get; private set; }
        public string? Email { get; private set; }
        public string? Password { get; private set; }
        public string? AccountId { get; private set; }
        public AccountRole Role { get; private set; }
        public CancellationToken Token { get; private set; }
        public int Calls { get; private set; }

        public Task<Account> CreateAsync(int gymId, string email, string password, AccountRole role, CancellationToken cancellationToken)
        {
            Capture("create", gymId, email, password, cancellationToken);
            Role = role;
            return Task.FromResult(Result ?? throw new InvalidOperationException("Set a created account for this test."));
        }

        public Task<Account?> FindByEmailAsync(int gymId, string email, CancellationToken cancellationToken)
        {
            Capture("email", gymId, email, null, cancellationToken);
            return Task.FromResult(Result);
        }

        public Task<Account?> SignInAsync(int? gymId, string email, string password, CancellationToken cancellationToken)
        {
            Capture("signin", gymId, email, password, cancellationToken);
            return Task.FromResult(Result);
        }

        public Task<Account?> FindByIdAsync(int? gymId, string accountId, CancellationToken cancellationToken)
        {
            Capture("id", gymId, null, null, cancellationToken);
            AccountId = accountId;
            return Task.FromResult(Result);
        }

        private void Capture(string operation, int? gym, string? email, string? password, CancellationToken token)
        {
            Operation = operation;
            Gym = gym;
            Email = email;
            Password = password;
            Token = token;
            Calls++;
        }
    }
}
