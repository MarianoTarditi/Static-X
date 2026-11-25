using DAL;
using DAL.Repositories.Implementation;
using Entity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using BLL;
using BLL.DTOs.Activations;
using DAL.Repositories.Interfaces;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using System.Data.Common;

namespace Static_X.Tests;

public sealed class TenantInvitationSliceTests
{
    [Fact]
    public async Task Deactivate_ExplicitLink_DisablesAccountAndRotatesStampAtomically()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var gym = new Gimnasio { Nombre = "Isolated" };
        db.Gimnasios.Add(gym);
        await db.SaveChangesAsync();
        var account = new GymAccount { Id = "linked", UserName = "linked", Email = "login@example.invalid", NormalizedEmail = "LOGIN@EXAMPLE.INVALID", GymId = gym.Id, Role = AccountRole.Student, SecurityStamp = "original" };
        db.Users.Add(account);
        var student = new Alumno { GimnasioId = gym.Id, Nombre = "Selected", Apellido = "Student", Email = "contact@example.invalid" };
        var link = typeof(Alumno).GetProperty("AccountId");
        Assert.NotNull(link);
        link.SetValue(student, account.Id);
        db.Alumnos.Add(student);
        await db.SaveChangesAsync();

        Assert.False((await new StudentRepository(db).SetActiveAsync(gym.Id, student.Id, false, default))!.Activo);
        db.ChangeTracker.Clear();
        var persisted = await db.Users.SingleAsync();
        var enabled = typeof(GymAccount).GetProperty("IsEnabled");
        Assert.NotNull(enabled);
        Assert.Equal(false, enabled.GetValue(persisted));
        Assert.NotEqual("original", persisted.SecurityStamp);
        Assert.Equal("contact@example.invalid", (await db.Alumnos.SingleAsync()).Email);
    }

    [Theory]
    [MemberData(nameof(AccountHttpTests.InvitationMatrix), MemberType = typeof(AccountHttpTests))]
    public async Task InvitationService_IndependentlyEnforcesActorGrantMatrixBeforeRepositoryAccess(AccountRole actor, AccountRole grant)
    {
        var repository = new InvitationRepositorySpy();
        var service = new ActivationService(repository, new EphemeralDataProtectionProvider(), new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Activation:KeyRingPath"] = Path.GetTempPath() }).Build(), TimeProvider.System);
        var request = new InviteAccountRequest("matrix", " recipient@example.invalid ", grant, grant == AccountRole.Student ? 23 : null);
        var allowed = actor == AccountRole.GymAdmin && grant is AccountRole.Trainer or AccountRole.Student || actor == AccountRole.Trainer && grant == AccountRole.Student;
        if (!allowed)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.InviteAsync(7, actor, request));
            Assert.Equal(0, repository.Calls);
        }
        else
        {
            Assert.Equal("persisted", (await service.InviteAsync(7, actor, request))!.AccountId);
            Assert.Equal(1, repository.Calls);
            Assert.Equal((7, "recipient@example.invalid", grant, request.StudentId), repository.Target);
        }
    }

    [Theory]
    [InlineData("key")]
    [InlineData("email")]
    [InlineData("student")]
    [InlineData("trainer")]
    [InlineData("gym")]
    [InlineData("enum")]
    [InlineData("null")]
    [InlineData("email-long")]
    [InlineData("email-blank")]
    [InlineData("key-blank")]
    [InlineData("negative-student")]
    public async Task InvitationService_InvalidInput_IsRejectedWithoutPersistenceOrMailConfiguration(string scenario)
    {
        var repository = new InvitationRepositorySpy();
        var service = new ActivationService(repository, new EphemeralDataProtectionProvider(), new ConfigurationBuilder().Build(), TimeProvider.System);
        var request = new InviteAccountRequest(scenario == "key" ? new string('k', 129) : scenario == "key-blank" ? " " : "valid",
            scenario == "email" ? "Name <recipient@example.invalid>" : scenario == "email-long" ? new string('a', 257) + "@example.invalid" : scenario == "email-blank" ? " " : "recipient@example.invalid",
            scenario == "trainer" ? AccountRole.Trainer : scenario == "enum" ? (AccountRole)99 : AccountRole.Student,
            scenario == "student" ? null : scenario == "negative-student" ? -1 : 23);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.InviteAsync(scenario == "gym" ? 0 : 7, AccountRole.GymAdmin, scenario == "null" ? null! : request));
        Assert.Equal(0, repository.Calls);
    }

    [Theory]
    [InlineData("foreign-gym")]
    [InlineData("wrong-role")]
    [InlineData("commit-failure")]
    public async Task LinkedState_InvalidBindingOrCommitFailure_RollsBackFichaAccountAndStamps(string scenario)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var interceptor = new SliceCommitFailure();
        var options = new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).AddInterceptors(interceptor).Options;
        await using var db = new StaticXDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var first = new Gimnasio { Nombre = "First" };
        var other = new Gimnasio { Nombre = "Other" };
        db.Gimnasios.AddRange(first, other);
        await db.SaveChangesAsync();
        var account = new GymAccount { Id = "target", UserName = "target", GymId = scenario == "foreign-gym" ? other.Id : first.Id,
            Role = scenario == "wrong-role" ? AccountRole.Trainer : AccountRole.Student, Email = "target@example.invalid", NormalizedEmail = "TARGET@EXAMPLE.INVALID",
            SecurityStamp = "original-stamp", ConcurrencyStamp = "original-concurrency" };
        db.Users.Add(account);
        var student = new Alumno { GimnasioId = first.Id, AccountId = account.Id, Nombre = "Selected", Apellido = "Student", Email = "contact@example.invalid" };
        db.Alumnos.Add(student);
        await db.SaveChangesAsync();
        interceptor.Fail = scenario == "commit-failure";
        await Assert.ThrowsAsync<InvalidOperationException>(() => new StudentRepository(db).SetActiveAsync(first.Id, student.Id, false, default));
        db.ChangeTracker.Clear();
        var persisted = await db.Users.SingleAsync();
        Assert.True(persisted.IsEnabled);
        Assert.False(persisted.StudentAccessSuspended);
        Assert.Equal(("original-stamp", "original-concurrency"), (persisted.SecurityStamp, persisted.ConcurrencyStamp));
        Assert.True((await db.Alumnos.SingleAsync()).Activo);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Reactivation_UnlinkedOrAdministrativelyDisabled_PreservesIndependentAccessAndLockout(bool linked, bool enabled)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var gym = new Gimnasio { Nombre = "Isolated" };
        db.Gimnasios.Add(gym);
        await db.SaveChangesAsync();
        var until = DateTimeOffset.UtcNow.AddDays(1);
        var account = new GymAccount { Id = "disabled", UserName = "disabled", GymId = gym.Id, Role = AccountRole.Student,
            Email = "same@example.invalid", NormalizedEmail = "SAME@EXAMPLE.INVALID", IsEnabled = enabled, SecurityStamp = "do-not-restore",
            LockoutEnd = until, AccessFailedCount = 4 };
        db.Users.Add(account);
        var student = new Alumno { GimnasioId = gym.Id, AccountId = linked ? account.Id : null, Nombre = "Independent", Apellido = "Ficha", Email = "same@example.invalid" };
        db.Alumnos.Add(student);
        await db.SaveChangesAsync();
        var repository = new StudentRepository(db);
        Assert.False((await repository.SetActiveAsync(gym.Id, student.Id, false, default))!.Activo);
        Assert.True((await repository.SetActiveAsync(gym.Id, student.Id, true, default))!.Activo);
        db.ChangeTracker.Clear();
        var persisted = Assert.Single(await db.Users.ToListAsync());
        Assert.Equal(enabled, persisted.IsEnabled);
        Assert.False(persisted.StudentAccessSuspended);
        Assert.False(persisted.EmailConfirmed);
        Assert.Null(persisted.PasswordHash);
        Assert.Equal((until, 4, "do-not-restore"), (persisted.LockoutEnd, persisted.AccessFailedCount, persisted.SecurityStamp));
        Assert.Equal(linked ? account.Id : null, (await db.Alumnos.SingleAsync()).AccountId);
        Assert.Empty(await db.ActivationInvitations.ToListAsync());
    }

    [Fact]
    public async Task StudentLink_UniqueForeignKey_PreventsSharingAndDanglingIdentityReferences()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var gym = new Gimnasio { Nombre = "Isolated" };
        db.Gimnasios.Add(gym);
        await db.SaveChangesAsync();
        var user = new GymAccount { Id = "one", UserName = "one", GymId = gym.Id, Role = AccountRole.Student, Email = "one@example.invalid", NormalizedEmail = "ONE@EXAMPLE.INVALID" };
        db.Users.Add(user);
        db.Alumnos.Add(new Alumno { GimnasioId = gym.Id, AccountId = user.Id, Nombre = "First", Apellido = "Student", Email = "first@example.invalid" });
        await db.SaveChangesAsync();
        db.Alumnos.Add(new Alumno { GimnasioId = gym.Id, AccountId = user.Id, Nombre = "Second", Apellido = "Student", Email = "second@example.invalid" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.Alumnos.Add(new Alumno { GimnasioId = gym.Id, AccountId = "missing", Nombre = "Dangling", Apellido = "Student", Email = "dangling@example.invalid" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.Users.Remove(await db.Users.SingleAsync());
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Single(await db.Alumnos.AsNoTracking().ToListAsync());
    }

    [Fact]
    public void TenantMigration_OfflineSqlPreservesGlobalScopeAndGuardsRollbackWithoutSeeds()
    {
        using var db = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>().UseSqlServer("Server=offline.invalid;Database=unused;Integrated Security=true").Options);
        var assembly = db.GetService<IMigrationsAssembly>();
        const string id = "20261111012132_AddTenantAccountInvitations";
        var migration = assembly.CreateMigration(assembly.Migrations[id], db.Database.ProviderName!);
        Assert.DoesNotContain(migration.UpOperations, o => o is InsertDataOperation or UpdateDataOperation or DeleteDataOperation or DropTableOperation or DropColumnOperation);
        var enabled = Assert.Single(migration.UpOperations.OfType<AddColumnOperation>(), o => o.Name == "IsEnabled");
        Assert.Equal((false, true), (enabled.IsNullable, enabled.DefaultValue));
        var link = Assert.Single(migration.UpOperations.OfType<CreateIndexOperation>(), o => o.Name == "IX_Alumnos_AccountId");
        Assert.True(link.IsUnique);
        Assert.Equal("[AccountId] IS NOT NULL", link.Filter);
        var sql = db.GetService<IMigrator>().GenerateScript("20261109221729_AddPasswordRecoveryOutbox", id);
        Assert.Contains("DEFAULT N'GymAdmin'", sql);
        Assert.Contains("CREATE TABLE [TenantInvitationRequests]", sql);
        Assert.DoesNotContain("UPDATE [AspNetUsers]", sql);
        var down = db.GetService<IMigrator>().GenerateScript(id, "20261109221729_AddPasswordRecoveryOutbox");
        Assert.Contains("THROW 51001", down);
        Assert.Contains("DROP TABLE", down);
        Assert.True(down.IndexOf("THROW 51001", StringComparison.Ordinal) < down.IndexOf("DROP TABLE", StringComparison.Ordinal));
        Assert.Contains("[IsEnabled] = 0", down);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.True(db.Model.FindEntityType(typeof(GymAccount))!.FindProperty(nameof(GymAccount.GymId))!.IsNullable);
        Assert.True(db.Model.FindEntityType(typeof(Alumno))!.FindProperty(nameof(Alumno.AccountId))!.IsConcurrencyToken);
    }

    [Fact]
    public async Task UnlinkedState_LinkAttachedAfterRead_RejectsStaleWriteAndPreservesEnabledAccess()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).Options;
        await using (var seed = new StaticXDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Gimnasios.Add(new Gimnasio { Id = 1, Nombre = "Isolated" });
            seed.Users.Add(new GymAccount { Id = "new-link", UserName = "new-link", GymId = 1, Role = AccountRole.Student,
                Email = "login@example.invalid", NormalizedEmail = "LOGIN@EXAMPLE.INVALID", SecurityStamp = "original" });
            seed.Alumnos.Add(new Alumno { Id = 1, GimnasioId = 1, Nombre = "Selected", Apellido = "Student", Email = "contact@example.invalid" });
            await seed.SaveChangesAsync();
        }
        await using var db = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection)
            .AddInterceptors(new AttachLinkBeforeStateSave()).Options);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => new StudentRepository(db).SetActiveAsync(1, 1, false, default));
        db.ChangeTracker.Clear();
        var student = await db.Alumnos.SingleAsync();
        Assert.Equal("new-link", student.AccountId);
        Assert.True(student.Activo);
        var account = await db.Users.SingleAsync();
        Assert.True(account.IsEnabled);
        Assert.False(account.StudentAccessSuspended);
        Assert.Equal("original", account.SecurityStamp);
    }

    private sealed class AttachLinkBeforeStateSave : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            // This commits independently before the legacy unlinked SaveChanges starts its own write.
            Assert.Equal(1, await eventData.Context!.Set<Alumno>().Where(s => s.Id == 1 && s.AccountId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(s => s.AccountId, "new-link"), cancellationToken));
            return result;
        }
    }

    private sealed class SliceCommitFailure : DbTransactionInterceptor
    {
        public bool Fail { get; set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new InvalidOperationException("Isolated commit failure.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class InvitationRepositorySpy : IActivationRepository
    {
        public int Calls { get; private set; }
        public (int Gym, string Email, AccountRole Role, int? Student) Target { get; private set; }
        public Task<Account?> InviteAsync(int gymId, string key, string email, AccountRole role, int? studentId, string tokenHash, Guid invitationId,
            string protectedToken, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken)
        {
            Calls++;
            Target = (gymId, email, role, studentId);
            Assert.Equal(64, tokenHash.Length);
            Assert.Equal(TimeSpan.FromHours(24), expiresAt - now);
            Assert.NotEmpty(protectedToken);
            return Task.FromResult<Account?>(new Account("persisted", gymId, email, role));
        }
        public Task<int> ProvisionAsync(string key, string gymName, string email, string tokenHash, Guid invitationId, string protectedToken,
            DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Account?> ActivateAsync(string tokenHash, string password, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ReissueAsync(int gymId, string accountId, string tokenHash, Guid invitationId, string protectedToken,
            DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
