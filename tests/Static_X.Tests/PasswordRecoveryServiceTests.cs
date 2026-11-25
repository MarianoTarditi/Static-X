using BLL;
using BLL.DTOs.Accounts;
using DAL.Repositories.Interfaces;
using DAL.Repositories.Models;
using Entity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Static_X.Tests;

public sealed class PasswordRecoveryServiceTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" ", null)]
    [InlineData("invalid", null)]
    [InlineData("Name <a@example.invalid>", null)]
    [InlineData("a@example.invalid", 0)]
    [InlineData("a@example.invalid", -1)]
    public async Task RecoveryService_ForgotValidatesIndependentlyBeforeCallingRepository(string? email, int? gymId)
    {
        var repository = new Repository();
        var service = Create(repository);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ForgotAsync(new ForgotPasswordRequest { Email = email!, GymId = gymId }));
        Assert.Equal(0, repository.Calls);
    }

    [Theory]
    [InlineData(null, "NewPassword2!")]
    [InlineData("", "NewPassword2!")]
    [InlineData(" ", "NewPassword2!")]
    [InlineData("opaque", null)]
    [InlineData("opaque", "")]
    [InlineData("opaque", " ")]
    public async Task RecoveryService_ResetValidatesIndependentlyWithoutPersistence(string? token, string? password)
    {
        var repository = new Repository();
        Assert.Null(await Create(repository).ResetAsync(new ResetPasswordRequest { Token = token, NewPassword = password }));
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task RecoveryService_PreservesPasswordWhitespaceAndCancellationToken()
    {
        var repository = new Repository();
        using var cancellation = new CancellationTokenSource();
        var result = await Create(repository).ResetAsync(new ResetPasswordRequest { Token = "opaque", NewPassword = " NewPassword2! " }, cancellation.Token);
        Assert.Equal("account-id", result);
        Assert.Equal(" NewPassword2! ", repository.Password);
        Assert.Equal(cancellation.Token, repository.Cancellation);
        Assert.Equal(1, repository.Calls);
    }

    private static AccountService Create(Repository repository) => new(repository, TestMapper.Instance, new EphemeralDataProtectionProvider(),
        new ConfigurationBuilder().Build(), TimeProvider.System);

    private sealed class Repository : IAccountRepository
    {
        public Task<TrainerAccountPage> ListTrainersAsync(int gymId, int offset, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SetTrainerEnabledAsync(int gymId, string accountId, bool enabled, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Account> CreateAsync(int gymId, string email, string password, AccountRole role, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Account?> FindByEmailAsync(int gymId, string email, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Account?> FindByIdAsync(int? gymId, string accountId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Account?> SignInAsync(int? gymId, string email, string password, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ChangePasswordAsync(int? gymId, string accountId, string currentPassword, string newPassword, CancellationToken cancellationToken) => throw new NotSupportedException();
        public int Calls { get; private set; }
        public string? Password { get; private set; }
        public CancellationToken Cancellation { get; private set; }
        public Task EnqueueAsync(int? gymId, string email, DateTimeOffset now, CancellationToken cancellationToken)
        { Calls++; return Task.CompletedTask; }
        public Task<string?> ResetAsync(string token, string password, DateTimeOffset now, CancellationToken cancellationToken)
        { Calls++; Password = password; Cancellation = cancellationToken; return Task.FromResult<string?>("account-id"); }
        public Task<PasswordRecoveryDelivery?> LeaseDueAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CleanupAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> FreezeDeliveryAsync(PasswordRecoveryDelivery delivery, string protectedSnapshot, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsCurrentAsync(PasswordRecoveryDelivery delivery, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteAsync(PasswordRecoveryDelivery delivery, DateTimeOffset now, bool accepted, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
