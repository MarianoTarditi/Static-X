using BLL;
using BLL.DTOs.Gyms;
using DAL;
using DAL.Repositories.Implementation;
using DAL.Repositories.Interfaces;
using DAL.Repositories.Models;
using Entity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Xunit;

namespace Static_X.Tests;

public sealed class GymAdministrativeQueryTests
{
    [Fact]
    public async Task Repository_GymPages_OrderAllGymsAndRetainTotalPastEndWithoutWrites()
    {
        await using var db = await QueryDatabase.CreateAsync();
        db.Context.Gimnasios.AddRange(new Gimnasio { Id = 30, Nombre = "Third" },
            new Gimnasio { Id = 10, Nombre = "First" }, new Gimnasio { Id = 20, Nombre = "Second" });
        await db.Context.SaveChangesAsync();
        db.BeginReadOnly();
        var repository = new GymRepository(db.Context);
        var first = await repository.ListAsync(0, 2, default);
        Assert.Equal(new[] { 10, 20 }, first.Items.Select(gym => gym.Id));
        Assert.Equal(3, first.TotalCount);
        var page = await repository.ListAsync(1, 1, default);
        Assert.Equal(20, Assert.Single(page.Items).Id);
        Assert.Equal("Second", page.Items[0].Nombre);
        Assert.Equal(3, page.TotalCount);
        var end = await repository.ListAsync(3, 1, default);
        Assert.Empty(end.Items);
        Assert.Equal(3, end.TotalCount);
        Assert.Empty(db.Context.ChangeTracker.Entries());
        Assert.All(db.Guard.Commands, sql => Assert.StartsWith("SELECT", sql.TrimStart(), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(db.Guard.Commands, sql => sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, db.Guard.SaveAttempts);
        Assert.Equal(3, await db.Context.Gimnasios.CountAsync());
    }

    [Theory]
    [InlineData("pending", true)]
    [InlineData("missing-invitation", true)]
    [InlineData("expired", true)]
    [InlineData("revoked", true)]
    [InlineData("consumed", true)]
    [InlineData("sent", true)]
    [InlineData("confirmed", false)]
    [InlineData("password", false)]
    [InlineData("empty-password", false)]
    [InlineData("trainer", false)]
    [InlineData("student", false)]
    [InlineData("global", false)]
    [InlineData("other-gym", false)]
    [InlineData("no-provision", false)]
    [InlineData("wrong-provision-gym", false)]
    [InlineData("wrong-provision-account", false)]
    public async Task Repository_PendingPredicate_ExactlyMatchesReissueEligibility(string scenario, bool eligible)
    {
        await using var db = await QueryDatabase.CreateAsync();
        db.Context.Gimnasios.AddRange(new Gimnasio { Id = 1, Nombre = "Selected" }, new Gimnasio { Id = 2, Nombre = "Other" });
        var account = new GymAccount
        {
            Id = "opaque-account", UserName = "opaque-account", Email = "admin@example.invalid",
            NormalizedEmail = "ADMIN@EXAMPLE.INVALID", GymId = 1, Role = AccountRole.GymAdmin,
            EmailConfirmed = scenario == "confirmed",
            PasswordHash = scenario == "password" ? "private-password-hash" : scenario == "empty-password" ? "" : null
        };
        if (scenario == "trainer") account.Role = AccountRole.Trainer;
        if (scenario == "student") account.Role = AccountRole.Student;
        if (scenario == "global") { account.Role = AccountRole.SuperAdmin; account.GymId = null; }
        if (scenario == "other-gym") account.GymId = 2;
        db.Context.Users.Add(account);
        if (scenario == "wrong-provision-account")
            db.Context.Users.Add(new GymAccount { Id = "other-account", UserName = "other-account", GymId = 1,
                Role = AccountRole.Trainer, Email = "other@example.invalid", NormalizedEmail = "OTHER@EXAMPLE.INVALID" });
        if (scenario != "no-provision")
            db.Context.ProvisioningRequests.Add(new ProvisioningRequest
            { Key = "request", GymId = scenario == "wrong-provision-gym" ? 2 : 1,
                AccountId = scenario == "wrong-provision-account" ? "other-account" : account.Id });
        if (scenario != "missing-invitation")
        {
            var invitation = new ActivationInvitation
            {
                Id = Guid.NewGuid(), GymId = 1, AccountId = account.Id, TokenHash = "private-token-hash",
                ExpiresAt = scenario == "expired" ? new DateTime(2020, 1, 1) : new DateTime(2099, 1, 1),
                RevokedAt = scenario == "revoked" ? new DateTime(2020, 1, 1) : null,
                ConsumedAt = scenario == "consumed" ? new DateTime(2020, 1, 1) : null
            };
            db.Context.ActivationInvitations.Add(invitation);
            db.Context.ActivationOutbox.Add(new ActivationOutbox
            {
                Id = Guid.NewGuid(), InvitationId = invitation.Id, ProtectedToken = "private-protected-token",
                CreatedAt = new DateTime(2020, 1, 1), SentAt = scenario == "sent" ? new DateTime(2020, 1, 1) : null
            });
        }
        await db.Context.SaveChangesAsync();
        db.BeginReadOnly();
        var repository = new GymRepository(db.Context);
        var page = await repository.ListPendingAdministratorsAsync(1, 0, 20, default);
        Assert.NotNull(page);
        Assert.Equal(eligible ? 1 : 0, page.TotalCount);
        if (eligible)
        {
            var item = Assert.Single(page.Items);
            Assert.Equal("opaque-account", item.Id);
            Assert.Equal("admin@example.invalid", item.Email);
        }
        else Assert.Empty(page.Items);
        var pastEnd = await repository.ListPendingAdministratorsAsync(1, 20, 20, default);
        Assert.NotNull(pastEnd);
        Assert.Empty(pastEnd.Items);
        Assert.Equal(page.TotalCount, pastEnd.TotalCount);
        Assert.Null(await repository.ListPendingAdministratorsAsync(999, 0, 20, default));
        Assert.Empty(db.Context.ChangeTracker.Entries());
        Assert.Equal(0, db.Guard.SaveAttempts);
        Assert.All(db.Guard.Commands, sql => Assert.StartsWith("SELECT", sql.TrimStart(), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(db.Guard.Commands, sql => sql.Contains("EXISTS", StringComparison.OrdinalIgnoreCase) && sql.Contains("ProvisioningRequests"));
        Assert.Contains(db.Guard.Commands, sql => sql.Contains("ORDER BY \"a\".\"Id\"") && sql.Contains("LIMIT"));
        foreach (var sql in db.Guard.Commands)
        {
            var projection = sql.Split("FROM", 2, StringSplitOptions.None)[0];
            foreach (var secret in new[] { "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "TokenHash", "ProtectedToken" })
                Assert.DoesNotContain(secret, projection);
        }
        Assert.Equal(scenario == "wrong-provision-account" ? 2 : 1, await db.Context.Users.CountAsync());
        Assert.Equal(scenario == "missing-invitation" ? 0 : 1, await db.Context.ActivationInvitations.CountAsync());
        Assert.Equal(scenario == "missing-invitation" ? 0 : 1, await db.Context.ActivationOutbox.CountAsync());
    }

    [Fact]
    public async Task Repository_EmptyDatabaseAndCanceledQueries_RemainDistinct()
    {
        await using var db = await QueryDatabase.CreateAsync();
        var repository = new GymRepository(db.Context);
        var page = await repository.ListAsync(0, 100, default);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ListAsync(0, 1, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ListPendingAdministratorsAsync(1, 0, 1, cancellation.Token));
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 101)]
    [InlineData(int.MaxValue, 100)]
    [InlineData(21474838, 100)]
    public async Task Service_InvalidPage_IsRejectedIndependentlyForBothQueries(int page, int pageSize)
    {
        var repository = new QueryRepository();
        var service = new GymService(repository, TestMapper.Instance);
        var request = new GymListRequest { Page = page, PageSize = pageSize };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.ListAsync(request));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.ListPendingAdministratorsAsync(7, request));
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task Service_NullRequestsAndInvalidGym_AreRejectedBeforeStorage()
    {
        var repository = new QueryRepository();
        var service = new GymService(repository, TestMapper.Instance);
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.ListAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.ListPendingAdministratorsAsync(7, null!));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListPendingAdministratorsAsync(0, new GymListRequest()));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListPendingAdministratorsAsync(-1, new GymListRequest()));
        Assert.Equal(0, repository.Calls);
    }

    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(1, 100, 0)]
    [InlineData(3, 20, 40)]
    [InlineData(int.MaxValue, 1, 2147483646)]
    [InlineData(21474837, 100, 2147483600)]
    [InlineData(2, 20, 20)]
    public async Task Service_ValidBounds_MapsPersistedResultsAndForwardsCancellation(int page, int pageSize, int offset)
    {
        TestMapper.Configuration.AssertConfigurationIsValid();
        var repository = new QueryRepository();
        var service = new GymService(repository, TestMapper.Instance);
        using var cancellation = new CancellationTokenSource();
        var request = new GymListRequest { Page = page, PageSize = pageSize };
        var gyms = await service.ListAsync(request, cancellation.Token);
        Assert.Equal(29, Assert.Single(gyms.Items).Id);
        Assert.Equal("Persisted gym", gyms.Items[0].Nombre);
        Assert.Equal((page, pageSize, 42), (gyms.Page, gyms.PageSize, gyms.TotalCount));
        Assert.Equal((offset, pageSize, cancellation.Token), (repository.Offset, repository.PageSize, repository.Token));
        var admins = await service.ListPendingAdministratorsAsync(7, request, cancellation.Token);
        Assert.NotNull(admins);
        Assert.Equal("persisted-account", Assert.Single(admins.Items).AccountId);
        Assert.Equal("persisted@example.invalid", admins.Items[0].Email);
        Assert.Equal((page, pageSize, 43), (admins.Page, admins.PageSize, admins.TotalCount));
        Assert.Equal((7, offset, pageSize, cancellation.Token), (repository.GymId, repository.Offset, repository.PageSize, repository.Token));
    }

    [Fact]
    public async Task Service_UnknownGymEmptyPagesAndCancellation_ArePreserved()
    {
        var repository = new QueryRepository { Missing = true };
        var service = new GymService(repository, TestMapper.Instance);
        Assert.Null(await service.ListPendingAdministratorsAsync(7, new GymListRequest()));
        repository.Missing = false;
        repository.Empty = true;
        var page = await service.ListPendingAdministratorsAsync(7, new GymListRequest { Page = 4 });
        Assert.NotNull(page);
        Assert.Empty(page.Items);
        Assert.Equal((4, 20, 43), (page.Page, page.PageSize, page.TotalCount));
        var gyms = await service.ListAsync(new GymListRequest { Page = 4 });
        Assert.Empty(gyms.Items);
        Assert.Equal((4, 20, 42), (gyms.Page, gyms.PageSize, gyms.TotalCount));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListAsync(new GymListRequest(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListPendingAdministratorsAsync(7, new GymListRequest(), cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Controller_Cancellation_IsPropagatedEvenBeforeTokenIsSignaled(bool canceled)
    {
        var repository = new QueryRepository { CancelAlways = true };
        var service = new GymService(repository, TestMapper.Instance);
        var controller = new Static_X.API.Controllers.GymRegistrationController(null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Static_X.API.Controllers.GymRegistrationController>.Instance, service);
        using var cancellation = new CancellationTokenSource();
        if (canceled) cancellation.Cancel();
        var first = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.ListGyms(new GymListRequest(), cancellation.Token));
        Assert.Equal(cancellation.Token, first.CancellationToken);
        var second = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.ListPendingAdministrators(7, new GymListRequest(), cancellation.Token));
        Assert.Equal(cancellation.Token, second.CancellationToken);
        Assert.Equal(cancellation.Token, repository.Token);
    }

    private sealed class QueryRepository : IGymRepository
    {
        public int Calls { get; private set; }
        public int GymId { get; private set; }
        public int Offset { get; private set; }
        public int PageSize { get; private set; }
        public CancellationToken Token { get; private set; }
        public bool Missing { get; set; }
        public bool Empty { get; set; }
        public bool CancelAlways { get; set; }

        public Task<Gimnasio> AddAsync(Gimnasio gym, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<GymPage> ListAsync(int offset, int pageSize, CancellationToken cancellationToken)
        {
            Record(0, offset, pageSize, cancellationToken);
            return Task.FromResult(new GymPage(Empty ? [] : [new Gimnasio { Id = 29, Nombre = "Persisted gym" }], 42));
        }

        public Task<PendingAdministratorPage?> ListPendingAdministratorsAsync(int gymId, int offset, int pageSize, CancellationToken cancellationToken)
        {
            Record(gymId, offset, pageSize, cancellationToken);
            return Task.FromResult(Missing ? null : new PendingAdministratorPage(Empty ? [] :
                [new Account("persisted-account", gymId, "persisted@example.invalid", AccountRole.GymAdmin)], 43));
        }

        private void Record(int gymId, int offset, int pageSize, CancellationToken token)
        {
            Calls++;
            GymId = gymId;
            Offset = offset;
            PageSize = pageSize;
            Token = token;
            if (CancelAlways) throw new OperationCanceledException(token);
            token.ThrowIfCancellationRequested();
        }
    }

    private sealed class QueryDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public StaticXDbContext Context { get; }
        public ReadGuard Guard { get; }

        private QueryDatabase(SqliteConnection connection, StaticXDbContext context, ReadGuard guard)
        {
            this.connection = connection;
            Context = context;
            Guard = guard;
        }

        public static async Task<QueryDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var guard = new ReadGuard();
            var context = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>()
                .UseSqlite(connection).AddInterceptors(guard).Options);
            await context.Database.EnsureCreatedAsync();
            return new QueryDatabase(connection, context, guard);
        }

        public void BeginReadOnly()
        {
            Context.ChangeTracker.Clear();
            Guard.Commands.Clear();
            Guard.ReadOnly = true;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class ReadGuard : SaveChangesInterceptor, IDbCommandInterceptor
    {
        public bool ReadOnly { get; set; }
        public int SaveAttempts { get; private set; }
        public List<string> Commands { get; } = [];

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            if (ReadOnly) { SaveAttempts++; throw new InvalidOperationException("Queries must not save."); }
            return base.SavingChanges(eventData, result);
        }

        public InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            if (ReadOnly) throw new InvalidOperationException("Queries must not execute writes.");
            return result;
        }

        public ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (ReadOnly) throw new InvalidOperationException("Queries must not execute writes.");
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (ReadOnly) { SaveAttempts++; throw new InvalidOperationException("Queries must not save."); }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (ReadOnly) Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
