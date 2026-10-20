using BLL;
using DAL;
using DAL.Repositories.Implementation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Static_X.Tests;

public sealed class TenantStudentTests
{
    [Fact]
    public async Task AddAndList_OnlyReturnStudentsFromRequestedGym()
    {
        await using var db = await TestDatabase.CreateAsync();
        var gyms = new GymService(new GymRepository(db.Context));
        var first = await gyms.AddAsync(" First gym ");
        var second = await gyms.AddAsync("Second gym");
        var students = new StudentService(new StudentRepository(db.Context));

        var created = await students.AddAsync(first.Id, " Ana ", " Smith ", " Ana@Example.com ");
        var secondStudent = await students.AddAsync(second.Id, "Ben", "Jones", "ana@example.com");

        Assert.Equal("First gym", first.Nombre);
        Assert.Equal(first.Id, created.GimnasioId);
        Assert.Equal("Ana", created.Nombre);
        Assert.Equal("Smith", created.Apellido);
        Assert.Equal("ANA@EXAMPLE.COM", created.Email);
        Assert.NotEqual(created.Id, secondStudent.Id);
        Assert.Equal(new[] { created.Id }, (await students.ListAsync(first.Id)).Select(s => s.Id));
        Assert.Equal(new[] { secondStudent.Id }, (await students.ListAsync(second.Id)).Select(s => s.Id));
    }

    [Fact]
    public async Task Find_CannotResolveStudentByIdAcrossGyms()
    {
        await using var db = await TestDatabase.CreateAsync();
        var gyms = new GymService(new GymRepository(db.Context));
        var first = await gyms.AddAsync("First");
        var second = await gyms.AddAsync("Second");
        var students = new StudentService(new StudentRepository(db.Context));
        var student = await students.AddAsync(first.Id, "Ana", "Smith", "ana@example.com");

        Assert.Null(await students.FindAsync(second.Id, student.Id));
        Assert.Equal(student.Id, (await students.FindAsync(first.Id, student.Id))?.Id);
        Assert.Empty(await students.ListAsync(second.Id));
    }

    [Fact]
    public async Task Add_RejectsDuplicateEmailWithinSameGym()
    {
        await using var db = await TestDatabase.CreateAsync();
        var gym = await new GymService(new GymRepository(db.Context)).AddAsync("First");
        var students = new StudentService(new StudentRepository(db.Context));
        await students.AddAsync(gym.Id, "Ana", "Smith", "ana@example.com");

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            students.AddAsync(gym.Id, "Ben", "Jones", " ANA@EXAMPLE.COM "));
    }

    [Fact]
    public async Task Add_RejectsMissingGymAndBlankStudentFields()
    {
        await using var db = await TestDatabase.CreateAsync();
        var students = new StudentService(new StudentRepository(db.Context));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => students.AddAsync(0, "Ana", "Smith", "a@b.com"));
        await Assert.ThrowsAsync<ArgumentException>(() => students.AddAsync(1, "  ", "Smith", "a@b.com"));
        await Assert.ThrowsAsync<DbUpdateException>(() => students.AddAsync(999, "Ana", "Smith", "a@b.com"));
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public dbcontext Context { get; }

        private TestDatabase(SqliteConnection connection, dbcontext context)
        {
            this.connection = connection;
            Context = context;
        }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA foreign_keys=ON;";
                await command.ExecuteNonQueryAsync();
            }
            var context = new dbcontext(new DbContextOptionsBuilder<dbcontext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
