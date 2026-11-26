using BLL;
using BLL.DTOs.Accounts;
using BLL.DTOs.Gyms;
using BLL.DTOs.Students;
using DAL;
using Entity;
using DAL.Repositories.Implementation;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Static_X.Tests;

public sealed class TenantAccountTests
{
    [Theory]
    [InlineData("wrong-gym")]
    [InlineData("global-selector")]
    [InlineData("unconfirmed")]
    [InlineData("missing")]
    [InlineData("zero-gym")]
    [InlineData("negative-gym")]
    public async Task ChangePassword_Repository_RequiresMatchingConfirmedPersistedScope(string scenario)
    {
        await using var db = await AccountDatabase.CreateAsync();
        var gym = await db.Gyms.AddAsync(new CreateGymRequest("First"));
        var account = await db.Accounts.CreateAsync(gym.Id, new CreateAccountRequest("scope@example.invalid", "CorrectPassword1!"), AccountRole.Student);
        var user = (await db.Users.FindByIdAsync(account.Id))!;
        user.EmailConfirmed = scenario != "unconfirmed";
        Assert.True((await db.Users.UpdateAsync(user)).Succeeded);
        var before = System.Text.Json.JsonSerializer.Serialize(user);
        var repository = new AccountRepository(db.Context, db.Users, db.SignIn, db.Protection);
        int? scope = scenario == "wrong-gym" ? gym.Id + 1 : scenario == "global-selector" ? null : scenario == "zero-gym" ? 0 : scenario == "negative-gym" ? -1 : gym.Id;

        Assert.False(await repository.ChangePasswordAsync(scope, scenario == "missing" ? "missing-id" : account.Id,
            "CorrectPassword1!", "NewPassword2!", default));

        db.Context.ChangeTracker.Clear();
        Assert.True(before == System.Text.Json.JsonSerializer.Serialize(await db.Context.Users.SingleAsync()), "Rejected scope must not mutate persisted account state.");
    }

    [Fact]
    public async Task ChangePassword_Repository_ConcurrencyConflictDoesNotPersistHashOrStamp()
    {
        await using var db = await AccountDatabase.CreateAsync();
        var gym = await db.Gyms.AddAsync(new CreateGymRequest("First"));
        var account = await db.Accounts.CreateAsync(gym.Id, new CreateAccountRequest("concurrent@example.invalid", "CorrectPassword1!"), AccountRole.Student);
        var stale = (await db.Users.FindByIdAsync(account.Id))!;
        stale.EmailConfirmed = true;
        Assert.True((await db.Users.UpdateAsync(stale)).Succeeded);
        string persistedBefore;
        using (var concurrent = db.CreateScope())
        {
            var users = concurrent.ServiceProvider.GetRequiredService<UserManager<IdentityAccount>>();
            var current = (await users.FindByIdAsync(account.Id))!;
            current.PhoneNumber = "concurrent-change";
            Assert.True((await users.UpdateAsync(current)).Succeeded);
            persistedBefore = System.Text.Json.JsonSerializer.Serialize(current);
        }

        Assert.False(await new AccountRepository(db.Context, db.Users, db.SignIn, db.Protection).ChangePasswordAsync(gym.Id, account.Id,
            "CorrectPassword1!", "NewPassword2!", default));

        using var verification = db.CreateScope();
        var persisted = await verification.ServiceProvider.GetRequiredService<StaticXDbContext>().Users.SingleAsync();
        Assert.True(persistedBefore == System.Text.Json.JsonSerializer.Serialize(persisted), "Concurrency failure must preserve the winning update, password hash and security stamp.");
        var verifier = verification.ServiceProvider.GetRequiredService<UserManager<IdentityAccount>>();
        Assert.True(await verifier.CheckPasswordAsync(persisted, "CorrectPassword1!"));
        Assert.False(await verifier.CheckPasswordAsync(persisted, "NewPassword2!"));
    }

    [Fact]
    public async Task ChangePassword_Repository_CancellationPropagatesWithoutMutation()
    {
        await using var db = await AccountDatabase.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => new AccountRepository(db.Context, db.Users, db.SignIn, db.Protection)
            .ChangePasswordAsync(null, "id", "current", "replacement", cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, await db.Context.Users.CountAsync());
    }

    [Fact]
    public async Task ChangePassword_Repository_SamePasswordFollowsIdentityPolicyAndRotatesStamp()
    {
        await using var db = await AccountDatabase.CreateAsync();
        var gym = await db.Gyms.AddAsync(new CreateGymRequest("First"));
        var account = await db.Accounts.CreateAsync(gym.Id, new CreateAccountRequest("same-password@example.invalid", "CorrectPassword1!"), AccountRole.Student);
        var user = (await db.Users.FindByIdAsync(account.Id))!;
        user.EmailConfirmed = true;
        Assert.True((await db.Users.UpdateAsync(user)).Succeeded);
        var stamp = user.SecurityStamp;
        Assert.True(await new AccountRepository(db.Context, db.Users, db.SignIn, db.Protection).ChangePasswordAsync(gym.Id, account.Id,
            "CorrectPassword1!", "CorrectPassword1!", default));
        db.Context.ChangeTracker.Clear();
        var persisted = await db.Context.Users.SingleAsync();
        Assert.True(stamp != persisted.SecurityStamp, "Even an Identity-approved same-password change revokes old cookies.");
        Assert.True(await db.Users.CheckPasswordAsync(persisted, "CorrectPassword1!"));
    }

    [Fact]
    public async Task Create_SameEmailAcrossGyms_PersistsSeparateIdentityPasswordsAndUsernames()
    {
        await using var db = await AccountDatabase.CreateAsync();
        var first = await db.Gyms.AddAsync(new CreateGymRequest("First"));
        var second = await db.Gyms.AddAsync(new CreateGymRequest("Second"));

        var firstAccount = await db.Accounts.CreateAsync(first.Id, new CreateAccountRequest(" Ana@Example.com ", "FirstPassword1!"), AccountRole.GymAdmin);
        var secondAccount = await db.Accounts.CreateAsync(second.Id, new CreateAccountRequest("ana@example.com", "SecondPassword1!"), AccountRole.Trainer);

        Assert.NotEqual(firstAccount.Id, secondAccount.Id);
        Assert.Equal(first.Id, firstAccount.GymId);
        Assert.Equal(second.Id, secondAccount.GymId);
        Assert.Equal("Ana@Example.com", firstAccount.Email);
        Assert.Equal(AccountRole.GymAdmin, firstAccount.Role);
        Assert.Equal(AccountRole.Trainer, secondAccount.Role);
        var firstUser = await db.Users.FindByIdAsync(firstAccount.Id);
        var secondUser = await db.Users.FindByIdAsync(secondAccount.Id);
        Assert.NotNull(firstUser);
        Assert.NotNull(secondUser);
        Assert.NotEqual(firstUser.UserName, secondUser.UserName);
        Assert.Equal("ANA@EXAMPLE.COM", firstUser.NormalizedEmail);
        Assert.Equal(firstUser.NormalizedEmail, secondUser.NormalizedEmail);
        Assert.NotEqual("FirstPassword1!", firstUser.PasswordHash);
        Assert.True(await db.Users.CheckPasswordAsync(firstUser, "FirstPassword1!"));
        Assert.False(await db.Users.CheckPasswordAsync(secondUser, "FirstPassword1!"));
    }

    [Fact]
    public async Task Create_DuplicateNormalizedEmailInSameGym_RejectsAtDatabaseIndex()
    {
        await using var db = await AccountDatabase.CreateAsync();
        var gym = await db.Gyms.AddAsync(new CreateGymRequest("First"));
        await db.Accounts.CreateAsync(gym.Id, new CreateAccountRequest("ana@example.com", "FirstPassword1!"), AccountRole.Trainer);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            db.Accounts.CreateAsync(gym.Id, new CreateAccountRequest(" ANA@EXAMPLE.COM ", "SecondPassword1!"), AccountRole.Student));
        Assert.Equal(1, await db.Context.Users.CountAsync(u => u.GymId == gym.Id));
    }

    [Theory]
    [InlineData(AccountRole.GymAdmin)]
    [InlineData(AccountRole.Trainer)]
    [InlineData(AccountRole.Student)]
    public async Task FindByEmail_RoleIsScopedToOwningGym(AccountRole role)
    {
        await using var db = await AccountDatabase.CreateAsync();
        var first = await db.Gyms.AddAsync(new CreateGymRequest("First"));
        var second = await db.Gyms.AddAsync(new CreateGymRequest("Second"));
        var created = await db.Accounts.CreateAsync(first.Id, new CreateAccountRequest("ana@example.com", "FirstPassword1!"), role);

        var found = await db.Accounts.FindByEmailAsync(first.Id, " ANA@Example.Com ");
        Assert.Equal(created.Id, found?.Id);
        Assert.Equal(first.Id, found?.GymId);
        Assert.Equal(role, found?.Role);
        Assert.Null(await db.Accounts.FindByEmailAsync(second.Id, "ana@example.com"));
    }

    [Fact]
    public async Task FindByEmail_DoesNotReturnOtherGymAccountWithMatchingEmail()
    {
        await using var db = await AccountDatabase.CreateAsync();
        var first = await db.Gyms.AddAsync(new CreateGymRequest("First"));
        var second = await db.Gyms.AddAsync(new CreateGymRequest("Second"));
        var firstAccount = await db.Accounts.CreateAsync(first.Id, new CreateAccountRequest("same@example.com", "FirstPassword1!"), AccountRole.Student);
        var secondAccount = await db.Accounts.CreateAsync(second.Id, new CreateAccountRequest("same@example.com", "SecondPassword1!"), AccountRole.GymAdmin);

        Assert.Equal(firstAccount.Id, (await db.Accounts.FindByEmailAsync(first.Id, "SAME@example.com"))?.Id);
        Assert.Equal(secondAccount.Id, (await db.Accounts.FindByEmailAsync(second.Id, "same@example.com"))?.Id);
        Assert.Null(await db.Accounts.FindByEmailAsync(second.Id, "missing@example.com"));
    }

    [Fact]
    public async Task StudentProfile_CanExistWithoutAccount()
    {
        await using var db = await AccountDatabase.CreateAsync();
        var gym = await db.Gyms.AddAsync(new CreateGymRequest("First"));
        var student = await new StudentService(new StudentRepository(db.Context), TestMapper.Instance)
            .AddAsync(gym.Id, new CreateStudentRequest { Nombre = "Ana", Apellido = "Smith", Email = "ana@example.com" });

        Assert.True(student.Id > 0);
        Assert.Null(await db.Accounts.FindByEmailAsync(gym.Id, "ana@example.com"));
        Assert.Equal(0, await db.Context.Users.CountAsync());
    }

    [Fact]
    public async Task Create_InvalidRole_RejectsBeforePersistence()
    {
        await using var db = await AccountDatabase.CreateAsync();
        var gym = await db.Gyms.AddAsync(new CreateGymRequest("First"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            db.Accounts.CreateAsync(gym.Id, new CreateAccountRequest("ana@example.com", "FirstPassword1!"), (AccountRole)99));
        Assert.Equal(0, await db.Context.Users.CountAsync());
    }

    [Theory]
    [InlineData("admin-without-gym")]
    [InlineData("trainer-without-gym")]
    [InlineData("student-without-gym")]
    [InlineData("global-with-gym")]
    [InlineData("unknown-role")]
    [InlineData("missing-gym")]
    public async Task AccountSchema_InvalidRoleScopeOrForeignGym_IsRejected(string scenario)
    {
        await using var db = await AccountDatabase.CreateAsync();
        var gym = await db.Gyms.AddAsync(new CreateGymRequest("Existing"));
        var account = new IdentityAccount { Id = "invalid", UserName = "invalid", Email = "invalid@example.com", NormalizedEmail = "INVALID@EXAMPLE.COM",
            GymId = scenario is "global-with-gym" or "unknown-role" ? gym.Id : scenario == "missing-gym" ? gym.Id + 1 : null,
            Role = scenario switch { "admin-without-gym" => AccountRole.GymAdmin, "trainer-without-gym" => AccountRole.Trainer,
                "student-without-gym" or "missing-gym" => AccountRole.Student, "unknown-role" => (AccountRole)99, _ => AccountRole.SuperAdmin } };
        db.Context.Users.Add(account);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.Context.SaveChangesAsync());
        Assert.Equal(0, await db.Context.Users.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task AccountSchema_GlobalEmailUnique_AllowsSameEmailInSeparateTenantsWithoutFakeGym()
    {
        await using var db = await AccountDatabase.CreateAsync();
        var global = new IdentityAccount { Id = "global", UserName = "global", Email = "same@example.com", Role = AccountRole.SuperAdmin };
        Assert.True((await db.Users.CreateAsync(global, "GlobalPassword1!")).Succeeded);
        Assert.Equal(0, await db.Context.Gimnasios.CountAsync());
        var first = await db.Gyms.AddAsync(new CreateGymRequest("First"));
        var second = await db.Gyms.AddAsync(new CreateGymRequest("Second"));
        await db.Accounts.CreateAsync(first.Id, new CreateAccountRequest("same@example.com", "TenantPassword1!"), AccountRole.GymAdmin);
        await db.Accounts.CreateAsync(second.Id, new CreateAccountRequest("same@example.com", "TenantPassword1!"), AccountRole.Student);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.Users.CreateAsync(new IdentityAccount
        { Id = "duplicate", UserName = "duplicate", Email = "SAME@example.com", Role = AccountRole.SuperAdmin }, "OtherPassword1!"));
        Assert.Equal(3, await db.Context.Users.CountAsync());
        Assert.Equal(1, await db.Context.Users.CountAsync(u => u.GymId == null));
    }

    [Fact]
    public async Task Create_SuperAdminThroughTenantService_IsRejectedWithoutPersistence()
    {
        await using var db = await AccountDatabase.CreateAsync();
        var gym = await db.Gyms.AddAsync(new CreateGymRequest("First"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.Accounts.CreateAsync(gym.Id,
            new CreateAccountRequest("global@example.com", "GlobalPassword1!"), AccountRole.SuperAdmin));
        Assert.Equal(0, await db.Context.Users.CountAsync());
        Assert.Equal((1, 2, 3, 4), ((int)AccountRole.GymAdmin, (int)AccountRole.Trainer, (int)AccountRole.Student, (int)AccountRole.SuperAdmin));
    }

    private sealed class AccountDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ServiceProvider provider;
        private readonly IServiceScope scope;
        public StaticXDbContext Context { get; }
        public UserManager<IdentityAccount> Users { get; }
        public AccountService Accounts { get; }
        public GymService Gyms { get; }
        public IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();
        public SignInManager<IdentityAccount> SignIn => scope.ServiceProvider.GetRequiredService<SignInManager<IdentityAccount>>();
        public IServiceScope CreateScope() => provider.CreateScope();

        private AccountDatabase(SqliteConnection connection, ServiceProvider provider, IServiceScope scope)
        {
            this.connection = connection;
            this.provider = provider;
            this.scope = scope;
            Context = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            Users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityAccount>>();
            Accounts = new AccountService(new AccountRepository(Context, Users,
                scope.ServiceProvider.GetRequiredService<SignInManager<IdentityAccount>>(), Protection), TestMapper.Instance,
                Protection, new ConfigurationBuilder().Build(), TimeProvider.System);
            Gyms = new GymService(new GymRepository(Context), TestMapper.Instance);
        }

        public static async Task<AccountDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
            services.AddDbContext<StaticXDbContext>(options => options.UseSqlite(connection));
            services.AddIdentityCore<IdentityAccount>(options => options.User.RequireUniqueEmail = false)
                .AddEntityFrameworkStores<StaticXDbContext>().AddSignInManager();
            var provider = services.BuildServiceProvider();
            var db = new AccountDatabase(connection, provider, provider.CreateScope());
            await db.Context.Database.EnsureCreatedAsync();
            return db;
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
