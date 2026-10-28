using BLL;
using BLL.DTOs.Gyms;
using BLL.DTOs.Students;
using BLL.Exceptions;
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
        var gyms = new GymService(new GymRepository(db.Context), TestMapper.Instance);
        var first = await gyms.AddAsync(new CreateGymRequest(" First gym "));
        var second = await gyms.AddAsync(new CreateGymRequest("Second gym"));
        var students = new StudentService(new StudentRepository(db.Context), TestMapper.Instance);

        var created = await students.AddAsync(first.Id, new CreateStudentRequest { Nombre = " Ana ", Apellido = " Smith ", Email = " Ana@Example.com " });
        var secondStudent = await students.AddAsync(second.Id, new CreateStudentRequest { Nombre = "Ben", Apellido = "Jones", Email = "ana@example.com" });

        Assert.Equal("First gym", first.Nombre);
        Assert.Equal(first.Id, (await db.Context.Alumnos.SingleAsync(s => s.Id == created.Id)).GimnasioId);
        Assert.Equal("Ana", created.Nombre);
        Assert.Equal("Smith", created.Apellido);
        Assert.Equal("ANA@EXAMPLE.COM", created.Email);
        Assert.NotEqual(created.Id, secondStudent.Id);
        Assert.Equal(new[] { created.Id }, (await students.ListAsync(first.Id, new StudentListRequest())).Items.Select(s => s.Id));
        Assert.Equal(new[] { secondStudent.Id }, (await students.ListAsync(second.Id, new StudentListRequest())).Items.Select(s => s.Id));
    }

    [Fact]
    public async Task Find_CannotResolveStudentByIdAcrossGyms()
    {
        await using var db = await TestDatabase.CreateAsync();
        var gyms = new GymService(new GymRepository(db.Context), TestMapper.Instance);
        var first = await gyms.AddAsync(new CreateGymRequest("First"));
        var second = await gyms.AddAsync(new CreateGymRequest("Second"));
        var students = new StudentService(new StudentRepository(db.Context), TestMapper.Instance);
        var student = await students.AddAsync(first.Id, new CreateStudentRequest { Nombre = "Ana", Apellido = "Smith", Email = "ana@example.com" });

        Assert.Null(await students.FindAsync(second.Id, student.Id));
        Assert.Equal(student.Id, (await students.FindAsync(first.Id, student.Id))?.Id);
        Assert.Empty((await students.ListAsync(second.Id, new StudentListRequest())).Items);
    }

    [Fact]
    public async Task Add_RejectsDuplicateEmailWithinSameGym()
    {
        await using var db = await TestDatabase.CreateAsync();
        var gym = await new GymService(new GymRepository(db.Context), TestMapper.Instance).AddAsync(new CreateGymRequest("First"));
        var students = new StudentService(new StudentRepository(db.Context), TestMapper.Instance);
        await students.AddAsync(gym.Id, new CreateStudentRequest { Nombre = "Ana", Apellido = "Smith", Email = "ana@example.com" });

        await Assert.ThrowsAsync<StudentEmailConflictException>(() =>
            students.AddAsync(gym.Id, new CreateStudentRequest { Nombre = "Ben", Apellido = "Jones", Email = " ANA@EXAMPLE.COM " }));
    }

    [Fact]
    public async Task Add_RejectsMissingGymAndBlankStudentFields()
    {
        await using var db = await TestDatabase.CreateAsync();
        var students = new StudentService(new StudentRepository(db.Context), TestMapper.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => students.AddAsync(0, new CreateStudentRequest { Nombre = "Ana", Apellido = "Smith", Email = "a@b.com" }));
        await Assert.ThrowsAsync<ArgumentException>(() => students.AddAsync(1, new CreateStudentRequest { Nombre = "  ", Apellido = "Smith", Email = "a@b.com" }));
        await Assert.ThrowsAsync<DbUpdateException>(() => students.AddAsync(999, new CreateStudentRequest { Nombre = "Ana", Apellido = "Smith", Email = "a@b.com" }));
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public StaticXDbContext Context { get; }

        private TestDatabase(SqliteConnection connection, StaticXDbContext context)
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
            var context = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).Options);
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
