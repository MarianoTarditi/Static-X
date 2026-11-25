using System.Data.Common;
using System.Text.Json;
using DAL;
using DAL.Repositories.Implementation;
using DAL.Repositories.Interfaces;
using Entity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Static_X.Tests;

public sealed partial class AccountHttpTests
{
    [Fact]
    public async Task TrainerRepository_ListingProjectsOnlySafeColumnsWithDatabaseCountOrderingAndBoundsWithoutTrackingOrWrites()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var commands = new TrainerCommands();
        await using var db = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).AddInterceptors(commands).Options);
        await db.Database.EnsureCreatedAsync();
        db.Gimnasios.AddRange(new Gimnasio { Id = 1, Nombre = "First" }, new Gimnasio { Id = 2, Nombre = "Second" });
        db.Users.AddRange(
            new GymAccount { Id = "z", UserName = "z", GymId = 1, Role = AccountRole.Trainer, Email = "z@example.invalid", IsEnabled = false },
            new GymAccount { Id = "a", UserName = "a", GymId = 1, Role = AccountRole.Trainer, Email = "a@example.invalid", EmailConfirmed = true },
            new GymAccount { Id = "foreign", UserName = "foreign", GymId = 2, Role = AccountRole.Trainer, Email = "foreign@example.invalid" },
            new GymAccount { Id = "student", UserName = "student", GymId = 1, Role = AccountRole.Student, Email = "student@example.invalid" });
        foreach (var entry in db.ChangeTracker.Entries<GymAccount>())
            entry.Entity.NormalizedEmail = entry.Entity.Email!.ToUpperInvariant();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        commands.Commands.Clear();
        var repository = new AccountRepository(db, null!, null!, new EphemeralDataProtectionProvider());
        using var cancellation = new CancellationTokenSource();
        var page = await repository.ListTrainersAsync(1, 1, 1, cancellation.Token);
        Assert.Equal(2, page.TotalCount);
        var item = Assert.Single(page.Items);
        Assert.Equal(("z", "z@example.invalid", false, false), (item.AccountId, item.Email, item.IsEnabled, item.EmailConfirmed));
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(2, commands.Commands.Count);
        Assert.Contains("COUNT(*)", commands.Commands[0]);
        var sql = commands.Commands[1];
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("LIMIT", sql);
        Assert.Contains("OFFSET", sql);
        Assert.All(commands.Commands, command =>
        {
            Assert.StartsWith("SELECT", command);
            Assert.Contains("\"GymId\"", command);
            Assert.Contains("\"Role\"", command);
            Assert.DoesNotContain("PasswordHash", command);
            Assert.DoesNotContain("SecurityStamp", command);
            Assert.DoesNotContain("ConcurrencyStamp", command);
        });
        Assert.Equal(cancellation.Token, commands.LastToken);
        Assert.Null(db.Model.FindEntityType(typeof(TrainerAccountProjection)));
    }

    [Fact]
    public async Task TrainerRepository_OnlySameGymTrainerPredicateCanChangeState()
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        var foreignGym = await app.CreateGymAsync();
        await SeedTrainerAsync(app, foreignGym, "foreign", true, false);
        var student = await app.CreateAccountAsync(gym, "student@example.invalid", "CorrectPassword1!", true);
        var admin = await app.CreateAccountAsync(gym, "admin@example.invalid", "CorrectPassword1!", true, AccountRole.GymAdmin);
        var global = await app.CreateGlobalAsync("global@example.invalid", "CorrectPassword1!");
        var before = await app.LogoutStateAsync();
        using var scope = app.Factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
        foreach (var enabled in new[] { false, true })
            foreach (var target in new[] { "foreign", "missing", student.Id, admin.Id, global })
                Assert.False(await repository.SetTrainerEnabledAsync(gym, target, enabled, default));
        Assert.False(await repository.SetTrainerEnabledAsync(int.MaxValue, "foreign", false, default));
        Assert.True(before == await app.LogoutStateAsync(), "DAL predicates must independently isolate role, gym and account.");
    }

    [Theory]
    [InlineData("gym-zero")]
    [InlineData("gym-negative")]
    [InlineData("offset-negative")]
    [InlineData("size-zero")]
    [InlineData("size-large")]
    public async Task TrainerRepository_InvalidListBounds_RejectBeforeDatabaseAccess(string scenario)
    {
        // Null context is intentional: all these guards must run before touching persistence.
        var repository = new AccountRepository(null!, null!, null!, new EphemeralDataProtectionProvider());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.ListTrainersAsync(
            scenario == "gym-zero" ? 0 : scenario == "gym-negative" ? -1 : 7,
            scenario == "offset-negative" ? -1 : 0, scenario == "size-zero" ? 0 : scenario == "size-large" ? 101 : 20, default));
    }

    [Theory]
    [InlineData("gym-zero")]
    [InlineData("gym-negative")]
    [InlineData("id-null")]
    [InlineData("id-blank")]
    [InlineData("id-long")]
    public async Task TrainerRepository_InvalidStateIdentifiers_RejectBeforeDatabaseAccess(string scenario)
    {
        var repository = new AccountRepository(null!, null!, null!, new EphemeralDataProtectionProvider());
        foreach (var enabled in new[] { false, true })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => repository.SetTrainerEnabledAsync(
                scenario == "gym-zero" ? 0 : scenario == "gym-negative" ? -1 : 7,
                scenario == "id-null" ? null! : scenario == "id-blank" ? " " : scenario == "id-long" ? new string('x', 451) : "opaque", enabled, default));
    }

    [Fact]
    public async Task TrainerRepository_CancellationPropagatesWithoutAnyAccountMutation()
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        await SeedTrainerAsync(app, gym, "cancel", true, true);
        var before = await app.LogoutStateAsync();
        using var scope = app.Factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ListTrainersAsync(gym, 0, 20, cancellation.Token));
        foreach (var enabled in new[] { false, true })
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.SetTrainerEnabledAsync(gym, "cancel", enabled, cancellation.Token));
        Assert.True(before == await app.LogoutStateAsync(), "Cancellation must not mutate accounts.");
    }

    [Fact]
    public async Task TrainerRepository_TransitionsPreserveLockoutAllIdentityFieldsOtherAccountsAndNoopStamps()
    {
        await using var app = await TestApp.CreateAsync();
        var gym = await app.CreateGymAsync();
        var id = new string('t', 450);
        await SeedTrainerAsync(app, gym, id, true, false);
        await SeedTrainerAsync(app, gym, "untouched", true, true);
        using var scope = app.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StaticXDbContext>();
        var original = await db.Users.SingleAsync(u => u.Id == id);
        original.LockoutEnd = DateTimeOffset.UtcNow.AddDays(1);
        original.AccessFailedCount = 4;
        original.TwoFactorEnabled = true;
        original.PhoneNumber = "isolated-phone";
        original.PhoneNumberConfirmed = true;
        await db.SaveChangesAsync();
        var originalJson = JsonSerializer.Serialize(original);
        var otherJson = JsonSerializer.Serialize(await db.Users.AsNoTracking().SingleAsync(u => u.Id == "untouched"));
        var originalStamp = original.SecurityStamp;
        db.ChangeTracker.Clear();
        var repository = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
        Assert.True(await repository.SetTrainerEnabledAsync(gym, id, false, default));
        var disabled = await db.Users.AsNoTracking().SingleAsync(u => u.Id == id);
        Assert.False(disabled.IsEnabled);
        Assert.NotEqual(originalStamp, disabled.SecurityStamp);
        Assert.NotEqual(original.ConcurrencyStamp, disabled.ConcurrencyStamp);
        Assert.True(await repository.SetTrainerEnabledAsync(gym, id, false, default));
        Assert.True(JsonSerializer.Serialize(disabled) == JsonSerializer.Serialize(await db.Users.AsNoTracking().SingleAsync(u => u.Id == id)), "Repeated disable must preserve both stamps and every field.");
        Assert.True(await repository.SetTrainerEnabledAsync(gym, id, true, default));
        var enabled = await db.Users.AsNoTracking().SingleAsync(u => u.Id == id);
        Assert.True(enabled.IsEnabled);
        Assert.Equal(disabled.SecurityStamp, enabled.SecurityStamp);
        Assert.NotEqual(disabled.ConcurrencyStamp, enabled.ConcurrencyStamp);
        Assert.True(await repository.SetTrainerEnabledAsync(gym, id, true, default));
        Assert.True(JsonSerializer.Serialize(enabled) == JsonSerializer.Serialize(await db.Users.AsNoTracking().SingleAsync(u => u.Id == id)), "Repeated enable must preserve both stamps and every field.");
        enabled.SecurityStamp = original.SecurityStamp;
        enabled.ConcurrencyStamp = original.ConcurrencyStamp;
        Assert.True(originalJson == JsonSerializer.Serialize(enabled), "Only enablement and stamps may change, including for locked pending accounts.");
        Assert.False(enabled.StudentAccessSuspended);
        Assert.Null(enabled.PasswordHash);
        Assert.False(enabled.EmailConfirmed);
        Assert.True(otherJson == JsonSerializer.Serialize(await db.Users.AsNoTracking().SingleAsync(u => u.Id == "untouched")), "Other Trainers must be untouched.");
        Assert.Empty(await db.Alumnos.ToListAsync());
        Assert.Empty(await db.ActivationInvitations.ToListAsync());
    }

    [Theory]
    [InlineData("change")]
    [InlineData("reset")]
    public async Task TrainerRepository_StaleIdentityPasswordWriter_CannotResurrectDisabledTrainer(string operation)
    {
        await using var app = await TestApp.CreateAsync(activation: true);
        var gym = await app.CreateGymAsync();
        await SeedTrainerAsync(app, gym, "stale", true, true);
        using var staleScope = app.Factory.Services.CreateScope();
        var users = staleScope.ServiceProvider.GetRequiredService<UserManager<GymAccount>>();
        var stale = await users.FindByIdAsync("stale");
        Assert.NotNull(stale);
        var token = operation == "reset" ? await users.GeneratePasswordResetTokenAsync(stale) : null;
        using (var writer = app.Factory.Services.CreateScope())
            Assert.True(await writer.ServiceProvider.GetRequiredService<IAccountRepository>().SetTrainerEnabledAsync(gym, stale.Id, false, default));
        // Deliberately stale Identity snapshot, not a claimed SQL Server interleaving. ConcurrencyStamp must reject its full-row update.
        var result = operation == "reset" ? await users.ResetPasswordAsync(stale, token!, "NewPassword2!") :
            await users.ChangePasswordAsync(stale, "CorrectPassword1!", "NewPassword2!");
        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "ConcurrencyFailure");
        using var verify = app.Factory.Services.CreateScope();
        var persisted = await verify.ServiceProvider.GetRequiredService<StaticXDbContext>().Users.AsNoTracking().SingleAsync();
        Assert.False(persisted.IsEnabled);
        Assert.NotEqual(stale.ConcurrencyStamp, persisted.ConcurrencyStamp);
        Assert.True(await verify.ServiceProvider.GetRequiredService<UserManager<GymAccount>>().CheckPasswordAsync(persisted, "CorrectPassword1!"));
    }

    private sealed class TrainerCommands : DbCommandInterceptor
    {
        public List<string> Commands { get; } = new List<string>();
        public CancellationToken LastToken { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            LastToken = cancellationToken;
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            LastToken = cancellationToken;
            return ValueTask.FromResult(result);
        }
    }
}
