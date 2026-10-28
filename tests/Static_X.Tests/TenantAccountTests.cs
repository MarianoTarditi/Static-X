using BLL;
using BLL.DTOs.Accounts;
using BLL.DTOs.Gyms;
using BLL.DTOs.Students;
using DAL;
using Entity;
using DAL.Repositories.Implementation;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Static_X.Tests;

public sealed class TenantAccountTests
{
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

    private sealed class AccountDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private readonly ServiceProvider provider;
        private readonly IServiceScope scope;
        public StaticXDbContext Context { get; }
        public UserManager<GymAccount> Users { get; }
        public AccountService Accounts { get; }
        public GymService Gyms { get; }

        private AccountDatabase(SqliteConnection connection, ServiceProvider provider, IServiceScope scope)
        {
            this.connection = connection;
            this.provider = provider;
            this.scope = scope;
            Context = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
            Users = scope.ServiceProvider.GetRequiredService<UserManager<GymAccount>>();
            Accounts = new AccountService(new AccountRepository(Context, Users,
                scope.ServiceProvider.GetRequiredService<SignInManager<GymAccount>>()), TestMapper.Instance);
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
            services.AddIdentityCore<GymAccount>(options => options.User.RequireUniqueEmail = false)
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
