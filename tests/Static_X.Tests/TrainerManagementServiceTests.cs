using BLL;
using BLL.DTOs.Accounts;
using DAL.Repositories.Interfaces;
using DAL.Repositories.Models;
using Entity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Static_X.Tests;

public sealed class TrainerManagementServiceTests
{
    [Theory]
    [InlineData(AccountRole.Trainer)]
    [InlineData(AccountRole.Student)]
    [InlineData(AccountRole.SuperAdmin)]
    [InlineData((AccountRole)0)]
    [InlineData((AccountRole)99)]
    public async Task TrainerService_RejectsEveryNonAdminBeforeRepositoryAccess(AccountRole role)
    {
        var repository = new TrainerRepositorySpy();
        var service = Create(repository);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListTrainersAsync(7, role, new TrainerListRequest()));
        foreach (var enabled in new[] { false, true })
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SetTrainerEnabledAsync(7, role, "opaque", enabled));
        Assert.Equal(0, repository.Calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("gym-zero")]
    [InlineData("gym-negative")]
    [InlineData("page-zero")]
    [InlineData("page-negative")]
    [InlineData("size-zero")]
    [InlineData("size-negative")]
    [InlineData("size-large")]
    [InlineData("offset-overflow")]
    [InlineData("offset-adjacent")]
    public async Task TrainerService_InvalidListInputs_RejectWithoutRepository(string scenario)
    {
        var repository = new TrainerRepositorySpy();
        var request = new TrainerListRequest
        {
            Page = scenario == "page-zero" ? 0 : scenario == "page-negative" ? -1 : scenario == "offset-overflow" ? int.MaxValue : scenario == "offset-adjacent" ? 1073741825 : 1,
            PageSize = scenario == "size-zero" ? 0 : scenario == "size-negative" ? -1 : scenario == "size-large" ? 101 : scenario == "offset-adjacent" ? 2 : 20
        };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Create(repository).ListTrainersAsync(
            scenario == "gym-zero" ? 0 : scenario == "gym-negative" ? -1 : 7, AccountRole.GymAdmin, scenario == "null" ? null! : request));
        Assert.Equal(0, repository.Calls);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("blank")]
    [InlineData("long")]
    [InlineData("gym-zero")]
    [InlineData("gym-negative")]
    public async Task TrainerService_InvalidStateInputs_RejectBothTransitionsWithoutRepository(string scenario)
    {
        var repository = new TrainerRepositorySpy();
        foreach (var enabled in new[] { false, true })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => Create(repository).SetTrainerEnabledAsync(
                scenario == "gym-zero" ? 0 : scenario == "gym-negative" ? -1 : 7, AccountRole.GymAdmin,
                scenario == "null" ? null! : scenario == "blank" ? " " : scenario == "long" ? new string('a', 451) : "opaque", enabled));
        Assert.Equal(0, repository.Calls);
    }

    [Theory]
    [InlineData(1, 20, 0)]
    [InlineData(2, 100, 100)]
    [InlineData(int.MaxValue, 1, int.MaxValue - 1)]
    [InlineData(1073741824, 2, int.MaxValue - 1)]
    public async Task TrainerService_ValidBounds_MapPersistedSafeProjectionAndForwardTrustedScope(int page, int size, int offset)
    {
        var repository = new TrainerRepositorySpy();
        using var cancellation = new CancellationTokenSource();
        var result = await Create(repository).ListTrainersAsync(7, AccountRole.GymAdmin, new TrainerListRequest { Page = page, PageSize = size }, cancellation.Token);
        Assert.Equal((7, offset, size, cancellation.Token), repository.ListInput);
        Assert.Equal((page, size, 31), (result.Page, result.PageSize, result.TotalCount));
        var item = Assert.Single(result.Items);
        Assert.Equal(("persisted-id", " Mixed@Example.invalid ", false, true), (item.AccountId, item.Email, item.IsEnabled, item.EmailConfirmed));
        Assert.All(typeof(TrainerResponse).GetProperties(), p => Assert.Null(p.SetMethod));
        Assert.All(typeof(TrainerListResponse).GetProperties(), p => Assert.Null(p.SetMethod));
        Assert.Equal(1, repository.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TrainerService_StateOutcomeAndOpaqueId_AreForwardedUnchanged(bool enabled, bool exists)
    {
        var repository = new TrainerRepositorySpy { Exists = exists };
        var id = new string('a', 450);
        using var cancellation = new CancellationTokenSource();
        Assert.Equal(exists, await Create(repository).SetTrainerEnabledAsync(7, AccountRole.GymAdmin, id, enabled, cancellation.Token));
        Assert.Equal((7, id, enabled, cancellation.Token), repository.StateInput);
        Assert.Equal(1, repository.Calls);
    }

    [Fact]
    public async Task TrainerService_CancelledRequests_DoNotInvokeRepository()
    {
        var repository = new TrainerRepositorySpy();
        var service = Create(repository);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListTrainersAsync(7, AccountRole.GymAdmin, new TrainerListRequest(), cancellation.Token));
        foreach (var enabled in new[] { false, true })
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SetTrainerEnabledAsync(7, AccountRole.GymAdmin, "opaque", enabled, cancellation.Token));
        Assert.Equal(0, repository.Calls);
    }

    private static AccountService Create(TrainerRepositorySpy repository) => new AccountService(repository, TestMapper.Instance,
        new EphemeralDataProtectionProvider(), new ConfigurationBuilder().Build(), TimeProvider.System);

    private sealed class TrainerRepositorySpy : IAccountRepository
    {
        public int Calls { get; private set; }
        public bool Exists { get; init; }
        public (int Gym, int Offset, int Size, CancellationToken Token) ListInput { get; private set; }
        public (int Gym, string Id, bool Enabled, CancellationToken Token) StateInput { get; private set; }

        public Task<TrainerAccountPage> ListTrainersAsync(int gymId, int offset, int pageSize, CancellationToken cancellationToken)
        {
            Calls++;
            ListInput = (gymId, offset, pageSize, cancellationToken);
            return Task.FromResult(new TrainerAccountPage(new[] { new TrainerAccountProjection("persisted-id", " Mixed@Example.invalid ", false, true) }, 31));
        }

        public Task<bool> SetTrainerEnabledAsync(int gymId, string accountId, bool enabled, CancellationToken cancellationToken)
        {
            Calls++;
            StateInput = (gymId, accountId, enabled, cancellationToken);
            return Task.FromResult(Exists);
        }

        public Task<Account> CreateAsync(int gymId, string email, string password, AccountRole role, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Account?> FindByEmailAsync(int gymId, string email, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Account?> FindByIdAsync(int? gymId, string accountId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Account?> SignInAsync(int? gymId, string email, string password, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ChangePasswordAsync(int? gymId, string accountId, string currentPassword, string newPassword, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task EnqueueAsync(int? gymId, string email, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> ResetAsync(string token, string password, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CleanupAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PasswordRecoveryDelivery?> LeaseDueAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> FreezeDeliveryAsync(PasswordRecoveryDelivery delivery, string protectedSnapshot, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsCurrentAsync(PasswordRecoveryDelivery delivery, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteAsync(PasswordRecoveryDelivery delivery, DateTimeOffset now, bool accepted, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
