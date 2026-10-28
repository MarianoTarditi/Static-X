using System.Reflection;
using System.Data.Common;
using System.Security.Claims;
using BLL;
using BLL.DTOs.Students;
using BLL.Exceptions;
using DAL;
using DAL.Repositories.Exceptions;
using DAL.Repositories.Implementation;
using DAL.Repositories.Interfaces;
using DAL.Repositories.Models;
using Entity;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Static_X.API.Controllers;
using Xunit;

namespace Static_X.Tests;

public sealed class StudentLayerTests
{
    [Theory]
    [InlineData(0, 20, false)]
    [InlineData(-1, 20, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, -1, false)]
    [InlineData(1, 101, false)]
    [InlineData(int.MaxValue, 100, false)]
    [InlineData(1, 20, true)]
    public async Task List_ServiceInvalidBoundsAndSearch_RejectBeforeRepository(int page, int size, bool longSearch)
    {
        var repository = new StubRepository();
        var service = new StudentService(repository, TestMapper.Instance);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.ListAsync(7, new StudentListRequest
        { Page = page, PageSize = size, Search = longSearch ? new string('a', 321) : null }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.ListAsync(7, null!));
        Assert.Equal(0, repository.Calls);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData(" aNa ", "ANA")]
    public async Task List_ServiceNormalizesAndForwardsBoundsAndCancellation_MapsPersistedPage(string? search, string? expected)
    {
        var repository = new StubRepository { TotalCount = 42, Rows = new[]
        { new Alumno { Id = 23, GimnasioId = 7, Nombre = "Persisted", Apellido = "Smith", Email = "A@B.COM", Activo = false } } };
        using var cancellation = new CancellationTokenSource();
        var result = await new StudentService(repository, TestMapper.Instance).ListAsync(7,
            new StudentListRequest { Page = 3, PageSize = 5, Search = search, Activo = false }, cancellation.Token);
        Assert.Equal((7, (bool?)false, expected, 10, 5, cancellation.Token),
            (repository.Gym, repository.Active, repository.Search, repository.Offset, repository.PageSize, repository.Token));
        Assert.Equal((3, 5, 42), (result.Page, result.PageSize, result.TotalCount));
        var item = Assert.Single(result.Items);
        Assert.Equal((23, "Persisted", "Smith", "A@B.COM", false), (item.Id, item.Nombre, item.Apellido, item.Email, item.Activo));
    }

    [Fact]
    public async Task List_ServiceMaximumSearchAndOffset_AreAcceptedWithoutOverflow()
    {
        var repository = new StubRepository();
        var result = await new StudentService(repository, TestMapper.Instance).ListAsync(7,
            new StudentListRequest { Page = int.MaxValue, PageSize = 1, Search = new string('a', 320) });
        Assert.Equal(int.MaxValue - 1, repository.Offset);
        Assert.Equal(new string('A', 320), repository.Search);
        Assert.Equal((int.MaxValue, 1, 0), (result.Page, result.PageSize, result.TotalCount));
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("surname")]
    [InlineData("email-blank")]
    [InlineData("email-invalid")]
    [InlineData("email-long")]
    [InlineData("name-null")]
    [InlineData("surname-null")]
    [InlineData("email-null")]
    public async Task Service_InvalidFields_RejectsBeforeRepository(string scenario)
    {
        var repository = new StubRepository();
        var service = new StudentService(repository, TestMapper.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() => service.AddAsync(1, new CreateStudentRequest
        {
            Nombre = scenario == "name-null" ? null! : scenario == "name" ? " " : "Ana", Apellido = scenario == "surname-null" ? null! : scenario == "surname" ? " " : "Smith",
            Email = scenario switch { "email-null" => null!, "email-blank" => " ", "email-invalid" => "invalid", "email-long" => new string('a', 309) + "@example.com", _ => "ana@example.com" }
        }));
        Assert.Null(repository.Added);
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(1, 23, new UpdateStudentRequest
        {
            Nombre = scenario == "name-null" ? null! : scenario == "name" ? " " : "Ana", Apellido = scenario == "surname-null" ? null! : scenario == "surname" ? " " : "Smith",
            Email = scenario switch { "email-null" => null!, "email-blank" => " ", "email-invalid" => "invalid", "email-long" => new string('a', 309) + "@example.com", _ => "ana@example.com" }
        }));
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task Service_MaximumEmail_NormalizesFieldsAndPropagatesCancellation()
    {
        var repository = new StubRepository();
        var service = new StudentService(repository, TestMapper.Instance);
        using var cancellation = new CancellationTokenSource();
        var email = new string('a', 308) + "@example.com";
        Assert.Equal(320, email.Length);
        var result = await service.AddAsync(7, new CreateStudentRequest { Nombre = " Ana ", Apellido = " Smith ", Email = $" {email} " }, cancellation.Token);
        Assert.Equal("Ana", result.Nombre);
        Assert.Equal("Smith", result.Apellido);
        Assert.Equal(email.ToUpperInvariant(), result.Email);
        Assert.Equal(7, repository.Added?.GimnasioId);
        Assert.Equal(cancellation.Token, repository.Token);
        Assert.Equal(23, result.Id);
        Assert.NotNull(repository.Added);
        Assert.Equal((23, 7, "Ana", "Smith", email.ToUpperInvariant()),
            (repository.Added.Id, repository.Added.GimnasioId, repository.Added.Nombre, repository.Added.Apellido, repository.Added.Email));
        await service.ListAsync(7, new StudentListRequest(), cancellation.Token);
        Assert.Equal(cancellation.Token, repository.Token);
        Assert.Equal(7, repository.Gym);
        await service.FindAsync(7, 23, cancellation.Token);
        Assert.Equal(cancellation.Token, repository.Token);
        Assert.Equal(7, repository.Gym);
        Assert.Equal(23, repository.Id);
        var updated = await service.UpdateAsync(7, 23, new UpdateStudentRequest { Nombre = " Bea ", Apellido = " Jones ", Email = $" {email} " }, cancellation.Token);
        Assert.NotNull(updated);
        Assert.Equal((23, "Bea", "Jones", email.ToUpperInvariant()),
            (updated.Id, updated.Nombre, updated.Apellido, updated.Email));
        Assert.Equal(7, repository.Gym);
        Assert.Equal(cancellation.Token, repository.Token);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("list")]
    [InlineData("find")]
    [InlineData("update")]
    public async Task Service_NonpositiveGym_RejectsBeforeRepository(string operation)
    {
        var repository = new StubRepository();
        var service = new StudentService(repository, TestMapper.Instance);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            if (operation == "add") await service.AddAsync(0, new CreateStudentRequest { Nombre = "Ana", Apellido = "Smith", Email = "ana@example.com" });
            else if (operation == "list") await service.ListAsync(0, new StudentListRequest());
            else if (operation == "update") await service.UpdateAsync(0, 1, new UpdateStudentRequest { Nombre = "Ana", Apellido = "Smith", Email = "ana@example.com" });
            else await service.FindAsync(0, 1);
        });
        Assert.Null(repository.Added);
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task Service_Reads_MapAllFichaFieldsAndPreserveEmptyAndMissingResults()
    {
        var repository = new StubRepository
        {
            Rows = new[]
            {
                new Alumno { Id = 23, GimnasioId = 7, Nombre = "Ana", Apellido = "Smith", Email = "ANA@EXAMPLE.COM" },
                new Alumno { Id = 24, GimnasioId = 7, Nombre = "Bea", Apellido = "Jones", Email = "BEA@EXAMPLE.COM" }
            }
        };
        var service = new StudentService(repository, TestMapper.Instance);
        using var cancellation = new CancellationTokenSource();
        var list = await service.ListAsync(7, new StudentListRequest(), cancellation.Token);
        Assert.Equal(new[] { (23, "Ana", "Smith", "ANA@EXAMPLE.COM"), (24, "Bea", "Jones", "BEA@EXAMPLE.COM") },
            list.Items.Select(student => (student.Id, student.Nombre, student.Apellido, student.Email)));
        Assert.Equal((7, cancellation.Token), (repository.Gym, repository.Token));
        var found = await service.FindAsync(7, 24, cancellation.Token);
        Assert.NotNull(found);
        Assert.Equal((24, "Bea", "Jones", "BEA@EXAMPLE.COM"), (found.Id, found.Nombre, found.Apellido, found.Email));
        Assert.Equal((7, 24, cancellation.Token), (repository.Gym, repository.Id, repository.Token));
        Assert.Null(await service.FindAsync(7, 99));
        repository.Rows = Array.Empty<Alumno>();
        Assert.Empty((await service.ListAsync(7, new StudentListRequest())).Items);
    }

    [Fact]
    public async Task Service_NullRequests_RejectBeforeRepository()
    {
        var repository = new StubRepository();
        var service = new StudentService(repository, TestMapper.Instance);
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.AddAsync(7, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.UpdateAsync(7, 23, null!));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AddAsync(7, new CreateStudentRequest()));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(7, 23, new UpdateStudentRequest()));
        Assert.Equal(0, repository.Calls);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("update")]
    public async Task Service_Writes_MapPersistedResultRatherThanSubmittedFields(string operation)
    {
        var repository = new StubRepository
        {
            WriteResult = new Alumno { Id = 71, GimnasioId = 7, Nombre = "Stored name", Apellido = "Stored surname", Email = "STORED@EXAMPLE.COM", Activo = false }
        };
        var service = new StudentService(repository, TestMapper.Instance);
        using var cancellation = new CancellationTokenSource();
        var result = operation == "add"
            ? await service.AddAsync(7, new CreateStudentRequest { Nombre = " Input name ", Apellido = " Input surname ", Email = " input@example.com " }, cancellation.Token)
            : await service.UpdateAsync(7, 23, new UpdateStudentRequest { Nombre = " Input name ", Apellido = " Input surname ", Email = " input@example.com " }, cancellation.Token);

        Assert.NotNull(result);
        Assert.Equal((71, "Stored name", "Stored surname", "STORED@EXAMPLE.COM"),
            (result.Id, result.Nombre, result.Apellido, result.Email));
        Assert.False(result.Activo);
        Assert.NotNull(repository.Added);
        Assert.Equal((7, "Input name", "Input surname", "INPUT@EXAMPLE.COM"),
            (repository.Added.GimnasioId, repository.Added.Nombre, repository.Added.Apellido, repository.Added.Email));
        Assert.Equal((1, cancellation.Token), (repository.Calls, repository.Token));
    }

    [Fact]
    public async Task Service_DuplicateOnly_TranslatesRepositoryConflict()
    {
        var repository = new StubRepository { Failure = new DuplicateStudentEmailException() };
        var service = new StudentService(repository, TestMapper.Instance);
        var create = new CreateStudentRequest { Nombre = "Ana", Apellido = "Smith", Email = "ana@example.com" };
        var update = new UpdateStudentRequest { Nombre = "Ana", Apellido = "Smith", Email = "ana@example.com" };
        await Assert.ThrowsAsync<StudentEmailConflictException>(() => service.AddAsync(1, create));
        await Assert.ThrowsAsync<StudentEmailConflictException>(() => service.UpdateAsync(1, 23, update));
        var unexpected = new InvalidOperationException("unexpected");
        repository.Failure = unexpected;
        Assert.Same(unexpected, await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddAsync(1, create)));
        Assert.Same(unexpected, await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(1, 23, update)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = new OperationCanceledException(cancellation.Token);
        repository.Failure = canceled;
        Assert.Same(canceled, await Assert.ThrowsAsync<OperationCanceledException>(() => service.UpdateAsync(1, 23, update, cancellation.Token)));
    }

    [Fact]
    public async Task Controller_AllOperations_ForwardSessionGymAndCancellation()
    {
        var repository = new StubRepository();
        var controller = new StudentsController(new StudentService(repository, TestMapper.Instance))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("gym_id", "7") }, "test"))
                }
            }
        };
        using var cancellation = new CancellationTokenSource();
        await controller.Create(new CreateStudentRequest { Nombre = "Ana", Apellido = "Smith", Email = "ana@example.com" }, cancellation.Token);
        Assert.Equal(cancellation.Token, repository.Token);
        Assert.Equal(7, repository.Added?.GimnasioId);
        await controller.List(new StudentListRequest(), cancellation.Token);
        Assert.Equal(cancellation.Token, repository.Token);
        Assert.Equal(7, repository.Gym);
        await controller.Find(23, cancellation.Token);
        Assert.Equal(cancellation.Token, repository.Token);
        Assert.Equal(7, repository.Gym);
        Assert.Equal(23, repository.Id);
        var update = await controller.Update(23, new UpdateStudentRequest { Nombre = " Bea ", Apellido = " Jones ", Email = " bea@example.com " }, cancellation.Token);
        var response = Assert.IsType<StudentResponse>(Assert.IsType<OkObjectResult>(update.Result).Value);
        Assert.Equal((23, "Bea", "Jones", "BEA@EXAMPLE.COM"), (response.Id, response.Nombre, response.Apellido, response.Email));
        Assert.Equal(cancellation.Token, repository.Token);
        Assert.Equal(7, repository.Gym);
        Assert.Equal(23, repository.Id);
        Assert.Equal(4, repository.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("zero")]
    [InlineData("negative")]
    public async Task Controller_InvalidGymClaim_AllOperationsFailClosedWithoutServiceAccess(string scenario)
    {
        var repository = new StubRepository();
        var identity = new ClaimsIdentity("test");
        if (scenario != "missing") identity.AddClaim(new Claim("gym_id", scenario switch { "malformed" => "invalid", "zero" => "0", _ => "-1" }));
        var controller = new StudentsController(new StudentService(repository, TestMapper.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } }
        };
        Assert.IsType<ForbidResult>((await controller.Create(new CreateStudentRequest(), default)).Result);
        Assert.IsType<ForbidResult>((await controller.List(new StudentListRequest(), default)).Result);
        Assert.IsType<ForbidResult>((await controller.Find(1, default)).Result);
        Assert.IsType<ForbidResult>((await controller.Update(1, new UpdateStudentRequest(), default)).Result);
        Assert.IsType<ForbidResult>((await controller.Deactivate(1, default)).Result);
        Assert.IsType<ForbidResult>((await controller.Reactivate(1, default)).Result);
        Assert.Equal(0, repository.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Service_NonpositiveStudentId_RejectsBeforeRepository(int id)
    {
        var repository = new StubRepository();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new StudentService(repository, TestMapper.Instance).FindAsync(1, id));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new StudentService(repository, TestMapper.Instance).UpdateAsync(1, id, new UpdateStudentRequest { Nombre = "Ana", Apellido = "Smith", Email = "ana@example.com" }));
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task Service_UpdateMissing_ReturnsNullWithoutInventingStudent()
    {
        var repository = new StubRepository { Missing = true };
        Assert.Null(await new StudentService(repository, TestMapper.Instance).UpdateAsync(7, 23, new UpdateStudentRequest { Nombre = "Ana", Apellido = "Smith", Email = "ana@example.com" }));
        Assert.Equal((7, 23, 1), (repository.Gym, repository.Id, repository.Calls));
    }

    [Theory]
    [InlineData("sql-student-index", true)]
    [InlineData("sql-localized-student-index", true)]
    [InlineData("sql-other-index", false)]
    [InlineData("sql-other-table", false)]
    [InlineData("sql-primary-key", false)]
    [InlineData("sql-foreign-key", false)]
    [InlineData("sql-value-spoof", false)]
    [InlineData("sqlite-student-index", true)]
    [InlineData("sqlite-other-index", false)]
    [InlineData("sqlite-primary-key", false)]
    [InlineData("sqlite-foreign-key", false)]
    public async Task Repository_OnlyExactStudentEmailConstraint_IsTranslated(string scenario, bool duplicate)
    {
        Exception provider;
        if (scenario.StartsWith("sql-", StringComparison.Ordinal))
        {
            var number = scenario switch { "sql-primary-key" => 2627, "sql-foreign-key" => 547, _ => 2601 };
            var table = scenario == "sql-other-table" ? "dbo.Other" : "dbo.Alumnos";
            var index = scenario == "sql-other-index" ? "IX_Other" : "IX_Alumnos_GimnasioId_Email";
            var message = $"Cannot insert duplicate key row in object '{table}' with unique index '{index}'. The duplicate key value is (1, ANA@EXAMPLE.COM).";
            if (scenario == "sql-localized-student-index") message = "No se puede insertar una fila de clave duplicada en el objeto 'dbo.Alumnos' con índice único 'IX_Alumnos_GimnasioId_Email'.";
            if (scenario == "sql-value-spoof") message = "Cannot insert duplicate key row in object 'dbo.Other' with unique index 'IX_Other'. The duplicate key value is ('dbo.Alumnos', 'IX_Alumnos_GimnasioId_Email').";
            provider = SqlFailure(number, message);
        }
        else
        {
            var code = scenario switch { "sqlite-primary-key" => 1555, "sqlite-foreign-key" => 787, _ => 2067 };
            provider = new SqliteException(scenario == "sqlite-other-index"
                ? "SQLite Error 19: 'UNIQUE constraint failed: Other.Email'."
                : "SQLite Error 19: 'UNIQUE constraint failed: Alumnos.GimnasioId, Alumnos.Email'.", 19, code);
        }
        var failure = new DbUpdateException("provider details", provider);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection)
            .AddInterceptors(new SaveFailure(failure)).Options);
        var repository = new StudentRepository(db);
        var student = new Alumno { GimnasioId = 1, Nombre = "Ana", Apellido = "Smith", Email = "ANA@EXAMPLE.COM" };
        if (duplicate)
        {
            await Assert.ThrowsAsync<DuplicateStudentEmailException>(() => repository.AddAsync(student, default));
            Assert.Equal(EntityState.Detached, db.Entry(student).State);
        }
        else
        {
            Assert.Same(failure, await Assert.ThrowsAsync<DbUpdateException>(() => repository.AddAsync(student, default)));
        }
        db.ChangeTracker.Clear();
        await using (var seed = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).Options))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Gimnasios.Add(new Gimnasio { Id = 1, Nombre = "Gym" });
            seed.Alumnos.Add(new Alumno { Id = 23, GimnasioId = 1, Nombre = "Original", Apellido = "Name", Email = "ORIGINAL@EXAMPLE.COM" });
            await seed.SaveChangesAsync();
        }
        if (duplicate)
        {
            await Assert.ThrowsAsync<DuplicateStudentEmailException>(() => repository.UpdateAsync(1, 23, "New", "Name", "ANA@EXAMPLE.COM", default));
            Assert.Empty(db.ChangeTracker.Entries<Alumno>());
        }
        else
        {
            Assert.Same(failure, await Assert.ThrowsAsync<DbUpdateException>(() => repository.UpdateAsync(1, 23, "New", "Name", "ANA@EXAMPLE.COM", default)));
        }
    }

    [Fact]
    public async Task Repository_ReadsAreScopedOrderedAndUntracked_AndCancellationIsObserved()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Gimnasios.AddRange(new Gimnasio { Id = 1, Nombre = "First" }, new Gimnasio { Id = 2, Nombre = "Second" });
        await db.SaveChangesAsync();
        var repository = new StudentRepository(db);
        await repository.AddAsync(new Alumno { GimnasioId = 1, Nombre = "Second", Apellido = "Smith", Email = "B@EXAMPLE.COM" }, default);
        await repository.AddAsync(new Alumno { GimnasioId = 2, Nombre = "Other", Apellido = "Smith", Email = "B@EXAMPLE.COM" }, default);
        await repository.AddAsync(new Alumno { GimnasioId = 1, Nombre = "Third", Apellido = "Smith", Email = "C@EXAMPLE.COM" }, default);
        db.ChangeTracker.Clear();
        Assert.Equal(new[] { 1, 3 }, (await repository.ListAsync(1, null, null, 0, 20, default)).Items.Select(s => s.Id));
        Assert.Null(await repository.FindAsync(1, 2, default));
        Assert.Equal("Second", (await repository.FindAsync(1, 1, default))?.Nombre);
        Assert.Empty(db.ChangeTracker.Entries<Alumno>());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ListAsync(1, null, null, 0, 20, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.FindAsync(1, 1, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.AddAsync(new Alumno(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.UpdateAsync(1, 1, "New", "Name", "NEW@EXAMPLE.COM", cancellation.Token));
    }

    [Fact]
    public async Task Repository_UpdateConflict_DetachesFailedChangeAndPreservesStoredFicha()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var db = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Gimnasios.AddRange(new Gimnasio { Id = 1, Nombre = "First" }, new Gimnasio { Id = 2, Nombre = "Second" });
        db.Alumnos.AddRange(
            new Alumno { Id = 1, GimnasioId = 1, Nombre = "Ana", Apellido = "Smith", Email = "ANA@EXAMPLE.COM" },
            new Alumno { Id = 2, GimnasioId = 1, Nombre = "Ben", Apellido = "Jones", Email = "BEN@EXAMPLE.COM" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var repository = new StudentRepository(db);
        Assert.Null(await repository.UpdateAsync(2, 1, "Changed", "Changed", "BEN@EXAMPLE.COM", default));
        Assert.Null(await repository.UpdateAsync(1, int.MaxValue, "Changed", "Changed", "BEN@EXAMPLE.COM", default));
        await Assert.ThrowsAsync<DuplicateStudentEmailException>(() => repository.UpdateAsync(1, 1, "Changed", "Changed", "BEN@EXAMPLE.COM", default));
        Assert.Empty(db.ChangeTracker.Entries<Alumno>());
        await db.SaveChangesAsync();
        var actual = await repository.FindAsync(1, 1, default);
        Assert.NotNull(actual);
        Assert.Equal((1, 1, "Ana", "Smith", "ANA@EXAMPLE.COM"), (actual.Id, actual.GimnasioId, actual.Nombre, actual.Apellido, actual.Email));
        Assert.Equal(2, await db.Alumnos.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repository_UpdateDeletedAfterRetrieval_PropagatesConcurrencyFailureEvenForUnchangedFields(bool unchanged)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).Options;
        await using (var seed = new StaticXDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Gimnasios.AddRange(new Gimnasio { Id = 1, Nombre = "First" }, new Gimnasio { Id = 2, Nombre = "Other" });
            seed.Alumnos.AddRange(
                new Alumno { Id = 1, GimnasioId = 1, Nombre = "Ana", Apellido = "Smith", Email = "ANA@EXAMPLE.COM" },
                new Alumno { Id = 2, GimnasioId = 2, Nombre = "Other", Apellido = "Gym", Email = "OTHER@EXAMPLE.COM" });
            await seed.SaveChangesAsync();
        }
        var interceptor = new DeleteBeforeSave();
        await using var db = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).AddInterceptors(interceptor).Options);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => new StudentRepository(db).UpdateAsync(1, 1,
            unchanged ? "Ana" : "Changed", "Smith", "ANA@EXAMPLE.COM", cancellation.Token));
        Assert.Equal(cancellation.Token, interceptor.Token);
        var remaining = await db.Alumnos.AsNoTracking().SingleAsync();
        Assert.Equal((2, 2, "Other", "Gym", "OTHER@EXAMPLE.COM"), (remaining.Id, remaining.GimnasioId, remaining.Nombre, remaining.Apellido, remaining.Email));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task State_ControllerAndService_ForwardTrustedContextAndMapPersistedResult(bool active)
    {
        var repository = new StubRepository
        {
            WriteResult = new Alumno { Id = 23, GimnasioId = 7, Nombre = "Stored", Apellido = "Name", Email = "STORED@EXAMPLE.COM", Activo = !active }
        };
        var service = new StudentService(repository, TestMapper.Instance);
        var controller = new StudentsController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("gym_id", "7") }, "test")) } }
        };
        using var cancellation = new CancellationTokenSource();
        var result = active ? await controller.Reactivate(23, cancellation.Token) : await controller.Deactivate(23, cancellation.Token);
        var response = Assert.IsType<StudentResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal((23, "Stored", "Name", "STORED@EXAMPLE.COM", !active),
            (response.Id, response.Nombre, response.Apellido, response.Email, response.Activo));
        Assert.Equal((7, 23, (bool?)active, cancellation.Token), (repository.Gym, repository.Id, repository.Active, repository.Token));
        await controller.List(new StudentListRequest { Activo = active }, cancellation.Token);
        Assert.Equal((7, (bool?)active, cancellation.Token), (repository.Gym, repository.Active, repository.Token));
        repository.Missing = true;
        Assert.Null(active ? await service.ReactivateAsync(7, 23) : await service.DeactivateAsync(7, 23));
        var missing = active ? await controller.Reactivate(23, default) : await controller.Deactivate(23, default);
        Assert.IsType<NotFoundObjectResult>(missing.Result);
        repository.Failure = new OperationCanceledException(cancellation.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(() => active ? service.ReactivateAsync(7, 23, cancellation.Token) : service.DeactivateAsync(7, 23, cancellation.Token));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public async Task State_ServiceInvalidIdentifiers_RejectBeforeRepository(int gym, int id)
    {
        var repository = new StubRepository();
        var service = new StudentService(repository, TestMapper.Instance);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.DeactivateAsync(gym, id));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ReactivateAsync(gym, id));
        Assert.Equal(0, repository.Calls);
    }

    [Fact]
    public async Task State_RepositoryCreationDefaultsAndFiltering_ArePersistedScopedOrderedAndUntracked()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var commands = new QueryCapture();
        await using var db = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).AddInterceptors(commands).Options);
        await db.Database.EnsureCreatedAsync();
        db.Gimnasios.AddRange(new Gimnasio { Id = 1, Nombre = "First" }, new Gimnasio { Id = 2, Nombre = "Other" });
        await db.SaveChangesAsync();
        Assert.True(new Alumno().Activo);
        var repository = new StudentRepository(db);
        var first = await repository.AddAsync(new Alumno { GimnasioId = 1, Nombre = "Ana", Apellido = "Smith", Email = "A@EXAMPLE.COM", Activo = false }, default);
        Assert.True(first.Activo);
        var second = await repository.AddAsync(new Alumno { GimnasioId = 1, Nombre = "Ben", Apellido = "Jones", Email = "B@EXAMPLE.COM" }, default);
        await repository.AddAsync(new Alumno { GimnasioId = 2, Nombre = "Other", Apellido = "Gym", Email = "A@EXAMPLE.COM" }, default);
        Assert.False((await repository.SetActiveAsync(1, first.Id, false, default))!.Activo);
        Assert.Null(await repository.SetActiveAsync(2, first.Id, false, default));
        Assert.Null(await repository.SetActiveAsync(1, int.MaxValue, false, default));
        db.ChangeTracker.Clear();
        Assert.Equal(new[] { first.Id, second.Id }, (await repository.ListAsync(1, null, null, 0, 20, default)).Items.Select(s => s.Id));
        Assert.Equal(second.Id, Assert.Single((await repository.ListAsync(1, true, null, 0, 20, default)).Items).Id);
        Assert.Equal(first.Id, Assert.Single((await repository.ListAsync(1, false, null, 0, 20, default)).Items).Id);
        var sql = commands.LastReaderSql;
        var predicate = sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];
        Assert.Contains("\"GimnasioId\"", predicate);
        Assert.Contains("\"Activo\"", predicate);
        Assert.Contains("ORDER BY", predicate);
        Assert.Contains("\"Id\"", predicate);
        Assert.Empty((await repository.ListAsync(2, false, null, 0, 20, default)).Items);
        Assert.False((await repository.FindAsync(1, first.Id, default))!.Activo);
        Assert.Empty(db.ChangeTracker.Entries<Alumno>());
        var updated = await repository.UpdateAsync(1, first.Id, "Updated", "Name", "NEW@EXAMPLE.COM", default);
        Assert.False(updated!.Activo);
        db.ChangeTracker.Clear();
        // An omitted column exercises the relational default, independently of the CLR initializer.
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Alumnos (GimnasioId, Nombre, Apellido, Email) VALUES (1, 'Default', 'Name', 'DEFAULT@EXAMPLE.COM')");
        Assert.True((await db.Alumnos.SingleAsync(s => s.Email == "DEFAULT@EXAMPLE.COM")).Activo);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.SetActiveAsync(1, first.Id, true, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.ListAsync(1, false, null, 0, 20, cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task State_RepositoryDeleteRace_NeverReturnsFalseSuccessEvenWhenIdempotent(bool initialActive)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).Options;
        await using (var seed = new StaticXDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Gimnasios.Add(new Gimnasio { Id = 1, Nombre = "Gym" });
            seed.Alumnos.Add(new Alumno { Id = 1, GimnasioId = 1, Nombre = "Ana", Apellido = "Smith", Email = "ANA@EXAMPLE.COM", Activo = initialActive });
            await seed.SaveChangesAsync();
        }
        var interceptor = new DeleteBeforeSave();
        await using var db = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).AddInterceptors(interceptor).Options);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => new StudentRepository(db).SetActiveAsync(1, 1, false, cancellation.Token));
        Assert.Equal(cancellation.Token, interceptor.Token);
        Assert.Empty(await db.Alumnos.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task State_RepositoryConcurrentFichaEdit_OnlyStateIsWritten()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).Options;
        await using (var seed = new StaticXDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Gimnasios.Add(new Gimnasio { Id = 1, Nombre = "Gym" });
            seed.Alumnos.Add(new Alumno { Id = 1, GimnasioId = 1, Nombre = "Ana", Apellido = "Smith", Email = "ANA@EXAMPLE.COM" });
            await seed.SaveChangesAsync();
        }
        var interceptor = new EditFichaBeforeStateSave();
        await using var db = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).AddInterceptors(interceptor).Options);
        var returned = await new StudentRepository(db).SetActiveAsync(1, 1, false, default);
        Assert.NotNull(returned);
        var actual = await db.Alumnos.AsNoTracking().SingleAsync();
        Assert.Equal(("Concurrent", "Edit", "CONCURRENT@EXAMPLE.COM", false), (actual.Nombre, actual.Apellido, actual.Email, actual.Activo));
        Assert.Equal((actual.Id, actual.GimnasioId, actual.Nombre, actual.Apellido, actual.Email, actual.Activo),
            (returned.Id, returned.GimnasioId, returned.Nombre, returned.Apellido, returned.Email, returned.Activo));
        Assert.Equal(new[] { "Activo" }, interceptor.ModifiedProperties);
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("other-gym")]
    [InlineData("cancelled")]
    public async Task State_RepositoryRefreshAfterSave_DisappearanceFailsAndCancellationIsObserved(string scenario)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).Options;
        await using (var seed = new StaticXDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Gimnasios.AddRange(new Gimnasio { Id = 1, Nombre = "Gym" }, new Gimnasio { Id = 2, Nombre = "Other" });
            seed.Alumnos.Add(new Alumno { Id = 1, GimnasioId = 1, Nombre = "Ana", Apellido = "Smith", Email = "ANA@EXAMPLE.COM" });
            await seed.SaveChangesAsync();
        }
        using var cancellation = new CancellationTokenSource();
        var interceptor = new ChangeAfterStateSave(scenario, cancellation);
        await using var db = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).AddInterceptors(interceptor).Options);
        var repository = new StudentRepository(db);
        if (scenario == "cancelled")
        {
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.SetActiveAsync(1, 1, false, cancellation.Token));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
        }
        else
        {
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => repository.SetActiveAsync(1, 1, false, cancellation.Token));
        }
        Assert.Equal(cancellation.Token, interceptor.Token);
        var persisted = await db.Alumnos.AsNoTracking().SingleOrDefaultAsync();
        if (scenario == "deleted") Assert.Null(persisted);
        else
        {
            Assert.NotNull(persisted);
            Assert.Equal((1, scenario == "other-gym" ? 2 : 1, "Ana", "Smith", "ANA@EXAMPLE.COM", false),
                (persisted.Id, persisted.GimnasioId, persisted.Nombre, persisted.Apellido, persisted.Email, persisted.Activo));
        }
    }

    private sealed class ChangeAfterStateSave : SaveChangesInterceptor
    {
        private readonly string scenario;
        private readonly CancellationTokenSource cancellation;
        public CancellationToken Token { get; private set; }

        public ChangeAfterStateSave(string scenario, CancellationTokenSource cancellation)
        {
            this.scenario = scenario;
            this.cancellation = cancellation;
        }

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            var query = eventData.Context!.Set<Alumno>().Where(s => s.Id == 1 && s.GimnasioId == 1);
            if (scenario == "deleted") await query.ExecuteDeleteAsync(cancellationToken);
            else if (scenario == "other-gym") await query.ExecuteUpdateAsync(setters => setters.SetProperty(s => s.GimnasioId, 2), cancellationToken);
            else cancellation.Cancel();
            return result;
        }
    }

    private sealed class EditFichaBeforeStateSave : SaveChangesInterceptor
    {
        public string[] ModifiedProperties { get; private set; } = [];
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = eventData.Context!;
            ModifiedProperties = db.ChangeTracker.Entries<Alumno>().Single().Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name).ToArray();
            await db.Set<Alumno>().Where(s => s.Id == 1 && s.GimnasioId == 1).ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.Nombre, "Concurrent").SetProperty(s => s.Apellido, "Edit").SetProperty(s => s.Email, "CONCURRENT@EXAMPLE.COM"), cancellationToken);
            return result;
        }
    }

    private sealed class QueryCapture : DbCommandInterceptor
    {
        public string LastReaderSql { get; private set; } = string.Empty;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            LastReaderSql = command.CommandText;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class DeleteBeforeSave : SaveChangesInterceptor
    {
        public CancellationToken Token { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            await eventData.Context!.Set<Alumno>().Where(s => s.Id == 1 && s.GimnasioId == 1).ExecuteDeleteAsync(cancellationToken);
            return result;
        }
    }

    // SqlClient exposes no public constructors for server errors. Build genuine provider
    // exceptions in tests only; no SQL Server connection or production reflection is involved.
    private static SqlException SqlFailure(int number, string message)
    {
        var constructor = typeof(SqlError).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .First(c => c.GetParameters().Any(p => p.Name == "errorMessage"));
        var arguments = constructor.GetParameters().Select(p => p.Name switch
        {
            "infoNumber" => (object)number,
            "errorMessage" => message,
            _ => p.ParameterType == typeof(string) ? "" : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null
        }).ToArray();
        var error = (SqlError)constructor.Invoke(arguments);
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(errors, new object[] { error });
        var factory = typeof(SqlException).GetMethod("CreateException", BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(SqlErrorCollection), typeof(string) })!;
        var result = (SqlException)factory.Invoke(null, new object[] { errors, "test-server" })!;
        Assert.Equal(number, result.Number);
        return result;
    }

    private sealed class SaveFailure : SaveChangesInterceptor
    {
        private readonly Exception failure;
        public SaveFailure(Exception failure) { this.failure = failure; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) => throw failure;
    }

    private sealed class StubRepository : IStudentRepository
    {
        public int? TotalCount { get; init; }
        public string? Search { get; private set; }
        public int Offset { get; private set; }
        public int PageSize { get; private set; }
        public bool? Active { get; private set; }
        public Task<Alumno?> SetActiveAsync(int gimnasioId, int studentId, bool active, CancellationToken cancellationToken)
        {
            Calls++;
            Gym = gimnasioId;
            Id = studentId;
            Active = active;
            Token = cancellationToken;
            return Failure is null ? Task.FromResult<Alumno?>(Missing ? null : WriteResult ?? new Alumno { Id = studentId, GimnasioId = gimnasioId, Activo = active }) : Task.FromException<Alumno?>(Failure);
        }
        public Task<Alumno?> UpdateAsync(int gimnasioId, int studentId, string nombre, string apellido, string email, CancellationToken cancellationToken)
        {
            Calls++;
            Gym = gimnasioId;
            Id = studentId;
            Token = cancellationToken;
            Added = new Alumno { Id = studentId, GimnasioId = gimnasioId, Nombre = nombre, Apellido = apellido, Email = email };
            return Failure is null ? Task.FromResult<Alumno?>(Missing ? null : WriteResult ?? Added) : Task.FromException<Alumno?>(Failure);
        }
        public Alumno? Added { get; private set; }
        public CancellationToken Token { get; private set; }
        public int Gym { get; private set; }
        public int Id { get; private set; }
        public int Calls { get; private set; }
        public Exception? Failure { get; set; }
        public bool Missing { get; set; }
        public Alumno? WriteResult { get; init; }
        public IReadOnlyList<Alumno> Rows { get; set; } = Array.Empty<Alumno>();
        public Task<Alumno> AddAsync(Alumno student, CancellationToken cancellationToken)
        {
            Calls++;
            Added = student;
            student.Id = 23;
            Token = cancellationToken;
            return Failure is null ? Task.FromResult(WriteResult ?? student) : Task.FromException<Alumno>(Failure);
        }
        public Task<Alumno?> FindAsync(int gimnasioId, int studentId, CancellationToken cancellationToken)
        {
            Calls++;
            Gym = gimnasioId;
            Id = studentId;
            Token = cancellationToken;
            return Task.FromResult(Rows.SingleOrDefault(student => student.Id == studentId && student.GimnasioId == gimnasioId));
        }
        public Task<StudentPage> ListAsync(int gimnasioId, bool? activo, string? search, int offset, int pageSize, CancellationToken cancellationToken)
        {
            Calls++;
            Gym = gimnasioId;
            Token = cancellationToken;
            Active = activo;
            Search = search;
            Offset = offset;
            PageSize = pageSize;
            return Task.FromResult(new StudentPage(Rows, TotalCount ?? Rows.Count));
        }
    }
}
