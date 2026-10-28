using System.Data;
using System.Data.Common;
using BLL;
using BLL.DTOs.Students;
using DAL;
using DAL.Repositories.Implementation;
using Entity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Static_X.Tests;

public sealed class StudentPaginationTests
{
    [Fact]
    public async Task Repository_FilteredCountAndBoundedItems_ExecuteTwoSqlQueriesAndRemainUntracked()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var capture = new CaptureCommands();
        await using var db = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>()
            .UseSqlite(connection).AddInterceptors(capture).Options);
        await SeedAsync(db);
        db.ChangeTracker.Clear();
        capture.Commands.Clear();
        capture.Tokens.Clear();
        using var cancellation = new CancellationTokenSource();
        var repository = new StudentRepository(db);
        var page = await repository.ListAsync(1, false, "ANA", 1, 1, cancellation.Token);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(3, Assert.Single(page.Items).Id);
        Assert.Empty(db.ChangeTracker.Entries<Alumno>());
        Assert.Equal(2, capture.Commands.Count);
        Assert.All(capture.Tokens, token => Assert.Equal(cancellation.Token, token));
        var count = capture.Commands[0];
        var items = capture.Commands[1];
        Assert.Contains("COUNT(*)", count);
        Assert.DoesNotContain("LIMIT", count);
        Assert.Contains("LIMIT", items);
        Assert.Contains("OFFSET", items);
        Assert.Contains("ORDER BY", items);
        foreach (var sql in capture.Commands)
        {
            Assert.Contains("WHERE", sql);
            Assert.Contains("\"GimnasioId\"", sql);
            Assert.Contains("\"Activo\"", sql);
            Assert.Contains("upper(", sql);
            Assert.Contains("instr(", sql);
            Assert.Contains(" OR ", sql);
            Assert.DoesNotContain("'ANA'", sql);
        }
        var beyond = await repository.ListAsync(1, false, "ANA", 100, 1, default);
        Assert.Equal(2, beyond.TotalCount);
        Assert.Empty(beyond.Items);
        var empty = await repository.ListAsync(2, false, "MISSING", 0, 20, default);
        Assert.Equal(0, empty.TotalCount);
        Assert.Empty(empty.Items);
    }

    [Theory]
    [InlineData(" aNa ", new[] { 1, 2, 3 })]
    [InlineData("SURNAME", new[] { 2 })]
    [InlineData("mail-token", new[] { 3 })]
    [InlineData("%", new[] { 4 })]
    [InlineData("_", new[] { 5 })]
    [InlineData("'", new[] { 6 })]
    [InlineData("%' OR 1=1 --", new int[0])]
    [InlineData(" ", new[] { 1, 2, 3, 4, 5, 6 })]
    [InlineData(null, new[] { 1, 2, 3, 4, 5, 6 })]
    public async Task Search_EachFieldCaseWhitespaceAndLiteralPunctuation_RemainsGymScoped(string? search, int[] expected)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).Options);
        await SeedAsync(db);
        var service = new StudentService(new StudentRepository(db), TestMapper.Instance);
        var result = await service.ListAsync(1, new StudentListRequest { Search = search });
        Assert.Equal(expected.Length, result.TotalCount);
        Assert.Equal(expected, result.Items.Select(s => s.Id));
    }

    [Fact]
    public async Task Repository_CancellationBetweenCountAndItems_ReachesSecondQuery()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var capture = new CaptureCommands();
        await using var db = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>()
            .UseSqlite(connection).AddInterceptors(capture).Options);
        await SeedAsync(db);
        capture.CancelAfterCount = cancellation;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new StudentRepository(db).ListAsync(1, null, null, 0, 20, cancellation.Token));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(cancellation.Token, capture.Tokens.Last());
    }

    [Fact]
    public async Task SqlServer_ActualRepositoryQueries_TranslateCountFiltersAndOffsetOffline()
    {
        var capture = new OfflineSqlServer();
        await using var db = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true;Connect Timeout=1")
            .AddInterceptors(new SuppressConnection(), capture).Options);
        using var cancellation = new CancellationTokenSource();
        var result = await new StudentRepository(db).ListAsync(7, true, "A%_'", 20, 10, cancellation.Token);
        Assert.Equal(42, result.TotalCount);
        Assert.Empty(result.Items);
        Assert.Equal(2, capture.Commands.Count);
        Assert.Contains("COUNT(*)", capture.Commands[0]);
        Assert.DoesNotContain("OFFSET", capture.Commands[0]);
        Assert.Contains("ORDER BY [a].[Id]", capture.Commands[1]);
        Assert.Contains("OFFSET", capture.Commands[1]);
        Assert.Contains("FETCH NEXT", capture.Commands[1]);
        Assert.All(capture.Commands, sql =>
        {
            Assert.Contains("WHERE", sql);
            Assert.Contains("[GimnasioId]", sql);
            Assert.Contains("[Activo]", sql);
            Assert.Contains("UPPER([a].[Nombre])", sql);
            Assert.Contains("UPPER([a].[Apellido])", sql);
            Assert.Contains("UPPER([a].[Email])", sql);
            Assert.Contains(" OR ", sql);
            Assert.DoesNotContain("A%_'", sql);
        });
        Assert.All(capture.Tokens, token => Assert.Equal(cancellation.Token, token));
        Assert.Contains(20, capture.Parameters[1]);
        Assert.Contains(10, capture.Parameters[1]);
        // Contains escapes LIKE metacharacters when translated to a LIKE parameter.
        Assert.Contains(capture.Parameters.SelectMany(p => p).OfType<string>(), value =>
            value.Contains("\\%", StringComparison.Ordinal) && value.Contains("\\_", StringComparison.Ordinal));
    }

    private static async Task SeedAsync(StaticXDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        db.Gimnasios.AddRange(new Gimnasio { Id = 1, Nombre = "Own" }, new Gimnasio { Id = 2, Nombre = "Other" });
        db.Alumnos.AddRange(
            new Alumno { Id = 1, GimnasioId = 1, Nombre = "aNa", Apellido = "First", Email = "FIRST@EXAMPLE.COM" },
            new Alumno { Id = 2, GimnasioId = 1, Nombre = "Other", Apellido = "Ana Surname", Email = "SECOND@EXAMPLE.COM", Activo = false },
            new Alumno { Id = 3, GimnasioId = 1, Nombre = "Other", Apellido = "Third", Email = "ANA-MAIL-TOKEN@EXAMPLE.COM", Activo = false },
            new Alumno { Id = 4, GimnasioId = 1, Nombre = "100%", Apellido = "Fourth", Email = "FOURTH@EXAMPLE.COM" },
            new Alumno { Id = 5, GimnasioId = 1, Nombre = "Under_score", Apellido = "Fifth", Email = "FIFTH@EXAMPLE.COM" },
            new Alumno { Id = 6, GimnasioId = 1, Nombre = "O'Neil", Apellido = "Sixth", Email = "SIXTH@EXAMPLE.COM" },
            new Alumno { Id = 7, GimnasioId = 2, Nombre = "Ana 100% Under_score O'Neil", Apellido = "Ana Surname", Email = "ANA-MAIL-TOKEN@EXAMPLE.COM", Activo = false });
        await db.SaveChangesAsync();
    }

    private sealed class CaptureCommands : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public CancellationTokenSource? CancelAfterCount { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)) Tokens.Add(cancellationToken);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData,
            InterceptionResult result)
        {
            if (command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal)) CancelAfterCount?.Cancel();
            return result;
        }
    }

    private sealed class SuppressConnection : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default) => ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private sealed class OfflineSqlServer : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];
        public List<object?[]> Parameters { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            Parameters.Add(command.Parameters.Cast<DbParameter>().Select(p => p.Value).ToArray());
            Tokens.Add(cancellationToken);
            var table = new DataTable();
            if (command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal))
            {
                table.Columns.Add("Count", typeof(int));
                table.Rows.Add(42);
            }
            else
            {
                table.Columns.Add("Id", typeof(int));
                table.Columns.Add("Activo", typeof(bool));
                table.Columns.Add("Apellido", typeof(string));
                table.Columns.Add("Email", typeof(string));
                table.Columns.Add("GimnasioId", typeof(int));
                table.Columns.Add("Nombre", typeof(string));
            }
            return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(table.CreateDataReader()));
        }
    }
}
