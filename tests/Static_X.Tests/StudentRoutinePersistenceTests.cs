using System.Data.Common;
using DAL;
using DAL.Repositories.Implementation;
using DAL.Repositories.Models;
using Entity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Static_X.Tests;

public sealed class StudentRoutinePersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetingPublications_OnlyOneSucceedsForSameExpectedRevision(bool replace)
    {
        var path = Path.Combine(Path.GetTempPath(), $"static-x-routine-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, ForeignKeys = true, DefaultTimeout = 10, Pooling = false
        }.ToString();
        var options = new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connectionString).Options;
        try
        {
            Guid? revision = null;
            await using (var setup = new StaticXDbContext(options))
            {
                await SeedAsync(setup);
                if (replace)
                    revision = (await PublishAsync(setup, null, "Original", "Old exercise", "Third old exercise")).Routine!.Revision;
            }

            using var start = new Barrier(2);
            var attempts = new[] { "Press", "Row" }.Select(name => Task.Run(async () =>
            {
                await using var db = new StaticXDbContext(options);
                Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
                return await PublishAsync(db, revision, name, name + " variation");
            }));
            var results = await Task.WhenAll(attempts);
            var winner = Assert.Single(results, r => r.Outcome == RoutineWriteOutcome.Published).Routine!;
            Assert.Single(results, r => r.Outcome == RoutineWriteOutcome.RevisionConflict);

            await using var verification = new StaticXDbContext(options);
            var current = (await new RoutineRepository(verification).FindForTrainerAsync(1, 1, default))!.Routine!;
            Assert.Equal(winner.Revision, current.Revision);
            Assert.Equal(Names(winner), Names(current));
            Assert.Equal(2, await verification.Set<StudentRoutineExercise>().CountAsync());
            Assert.Single(await verification.Set<StudentRoutineDay>().ToArrayAsync());

            verification.StudentRoutines.Add(new StudentRoutine { StudentId = 1, Revision = Guid.NewGuid() });
            var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => verification.SaveChangesAsync());
            Assert.Equal(1555, Assert.IsType<SqliteException>(duplicate.InnerException).SqliteExtendedErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FailingSecondExerciseInsert_RollsBackRevisionDeletionAndPartialInsert()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var fault = new RoutineInsertFault();
        var options = new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).AddInterceptors(fault).Options;
        Guid revision;
        await using (var setup = new StaticXDbContext(options))
        {
            await SeedAsync(setup);
            revision = (await PublishAsync(setup, null, "Original first", "Original second")).Routine!.Revision;
        }

        fault.Enabled = true;
        await using (var attempt = new StaticXDbContext(options))
        {
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => PublishAsync(attempt, revision, "New first", "New second"));
            Assert.IsType<IOException>(error.InnerException);
        }
        Assert.Equal(2, fault.Attempts);
        fault.Enabled = false;

        await using (var verification = new StaticXDbContext(options))
        {
            var current = (await new RoutineRepository(verification).FindForTrainerAsync(1, 1, default))!.Routine!;
            Assert.Equal(revision, current.Revision);
            Assert.Equal(new[] { "Original first", "Original second" }, Names(current));
            Assert.Equal(RoutineWriteOutcome.Published, (await PublishAsync(verification, revision, "Retry")).Outcome);
        }
    }

    [Fact]
    public void SqlServerMigration_AddsOnlyRoutineTablesAndMatchesModelOffline()
    {
        using var context = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var migrations = context.GetService<IMigrationsAssembly>();
        const string previous = "20261116000000_AddPayments";
        const string current = "20261119013122_AddStudentRoutines";
        Assert.Equal(previous, migrations.Migrations.Keys.TakeWhile(id => id != current).Last());
        Assert.False(context.Database.HasPendingModelChanges());
        var migration = migrations.CreateMigration(migrations.Migrations[current], context.Database.ProviderName!);
        var tables = new[] { "StudentRoutines", "StudentRoutineDays", "StudentRoutineExercises" };
        Assert.Equal(tables, migration.UpOperations.Select(op => Assert.IsType<CreateTableOperation>(op).Name));
        Assert.Equal(tables.Reverse(), migration.DownOperations.Select(op => Assert.IsType<DropTableOperation>(op).Name));
        var script = context.GetService<IMigrator>().GenerateScript(previous, current);
        foreach (var table in tables) Assert.Contains($"CREATE TABLE [{table}]", script);
        Assert.DoesNotContain("ALTER TABLE", script);
        Assert.DoesNotContain("DROP TABLE", script);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void SqlServerMigration_AddsOnlyNullableRoutineMetadataAndMatchesModelOffline()
    {
        using var context = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var migrations = context.GetService<IMigrationsAssembly>();
        const string previous = "20261119013122_AddStudentRoutines";
        const string current = "20261123000000_AddRoutineMetadata";
        Assert.Equal(previous, migrations.Migrations.Keys.TakeWhile(id => id != current).Last());
        Assert.False(context.Database.HasPendingModelChanges());
        var migration = migrations.CreateMigration(migrations.Migrations[current], context.Database.ProviderName!);
        var expected = new[]
        {
            (Table: "StudentRoutineDays", Column: "Instructions", Limit: 2000),
            (Table: "StudentRoutineDays", Column: "Name", Limit: 200),
            (Table: "StudentRoutines", Column: "Instructions", Limit: 2000),
            (Table: "StudentRoutines", Column: "Title", Limit: 200)
        };
        var added = migration.UpOperations.Select(Assert.IsType<AddColumnOperation>).ToArray();
        Assert.Equal(expected, added.Select(column => (column.Table, column.Name, column.MaxLength!.Value)));
        Assert.All(added, column =>
        {
            Assert.True(column.IsNullable);
            Assert.Null(column.DefaultValue);
            Assert.Null(column.DefaultValueSql);
        });
        Assert.Equal(expected.Select(column => (column.Table, column.Column)),
            migration.DownOperations.Select(Assert.IsType<DropColumnOperation>).Select(column => (column.Table, column.Name)));

        var model = context.GetService<IModelRuntimeInitializer>().Initialize(migration.TargetModel);
        var previousMigration = migrations.CreateMigration(migrations.Migrations[previous], context.Database.ProviderName!);
        var previousModel = context.GetService<IModelRuntimeInitializer>().Initialize(previousMigration.TargetModel);
        Assert.Equal(migration.UpOperations.Select(OperationShape).Order(),
            context.GetService<IMigrationsModelDiffer>().GetDifferences(
                previousModel.GetRelationalModel(), model.GetRelationalModel()).Select(OperationShape).Order());
        var successor = migrations.CreateMigration(migrations.Migrations["20261123010000_AddExerciseDuration"], context.Database.ProviderName!);
        var successorModel = context.GetService<IModelRuntimeInitializer>().Initialize(successor.TargetModel);
        var changes = context.GetService<IMigrationsModelDiffer>().GetDifferences(model.GetRelationalModel(), successorModel.GetRelationalModel());
        Assert.Equal(4, changes.Count);
        Assert.Single(changes.OfType<AddColumnOperation>(), column => column.Name == "DurationSeconds");
        Assert.Single(changes.OfType<AlterColumnOperation>(), column => column.Name == "Repetitions" && column.IsNullable);
        Assert.Single(changes.OfType<DropCheckConstraintOperation>());
        Assert.Single(changes.OfType<AddCheckConstraintOperation>());
        var script = context.GetService<IMigrator>().GenerateScript(previous, current);
        foreach (var (table, column, limit) in expected)
            Assert.Contains($"ALTER TABLE [{table}] ADD [{column}] nvarchar({limit}) NULL;", script);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void SqlServerMigration_AddsExerciseDurationAndGuardsRollbackOffline()
    {
        using var context = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>()
            .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
        var migrations = context.GetService<IMigrationsAssembly>();
        const string previous = "20261123000000_AddRoutineMetadata";
        const string current = "20261123010000_AddExerciseDuration";
        Assert.Equal(new[] { previous, current }, migrations.Migrations.Keys.TakeLast(2));
        Assert.False(context.Database.HasPendingModelChanges());
        var migration = migrations.CreateMigration(migrations.Migrations[current], context.Database.ProviderName!);
        Assert.Collection(migration.UpOperations,
            operation => Assert.IsType<DropCheckConstraintOperation>(operation),
            operation =>
            {
                var column = Assert.IsType<AlterColumnOperation>(operation);
                Assert.Equal(("StudentRoutineExercises", "Repetitions", true, false),
                    (column.Table, column.Name, column.IsNullable, column.OldColumn.IsNullable));
                Assert.Null(column.DefaultValue);
            },
            operation =>
            {
                var column = Assert.IsType<AddColumnOperation>(operation);
                Assert.Equal(("StudentRoutineExercises", "DurationSeconds", "int", true),
                    (column.Table, column.Name, column.ColumnType, column.IsNullable));
                Assert.Null(column.DefaultValue);
                Assert.Null(column.DefaultValueSql);
            },
            operation =>
            {
                var check = Assert.IsType<AddCheckConstraintOperation>(operation);
                Assert.Contains("[Repetitions] IS NOT NULL AND [Repetitions] > 0 AND [DurationSeconds] IS NULL", check.Sql);
                Assert.Contains("[DurationSeconds] IS NOT NULL AND [DurationSeconds] > 0 AND [Repetitions] IS NULL", check.Sql);
            });
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var model = initializer.Initialize(migration.TargetModel);
        var previousMigration = migrations.CreateMigration(migrations.Migrations[previous], context.Database.ProviderName!);
        var previousModel = initializer.Initialize(previousMigration.TargetModel);
        var differ = context.GetService<IMigrationsModelDiffer>();
        Assert.False(differ.HasDifferences(model.GetRelationalModel(), context.GetService<IDesignTimeModel>().Model.GetRelationalModel()));
        Assert.Equal(differ.GetDifferences(previousModel.GetRelationalModel(), model.GetRelationalModel()).Select(OperationShape).Order(),
            migration.UpOperations.Select(OperationShape).Order());
        var guard = Assert.IsType<SqlOperation>(migration.DownOperations[0]);
        Assert.Contains("THROW 51000", guard.Sql);
        Assert.Contains("TABLOCKX, HOLDLOCK", guard.Sql);
        Assert.Contains("[DurationSeconds] IS NOT NULL OR [Repetitions] IS NULL", guard.Sql);
        var narrowed = Assert.Single(migration.DownOperations.OfType<AlterColumnOperation>());
        // Model differ proposes a CLR default when narrowing. The guarded rollback must not backfill it.
        Assert.Null(narrowed.DefaultValue);
        Assert.Null(narrowed.DefaultValueSql);
        Assert.Equal(differ.GetDifferences(model.GetRelationalModel(), previousModel.GetRelationalModel()).Select(OperationShape).Order(),
            migration.DownOperations.Skip(1).Select(OperationShape).Order());
        var rollback = context.GetService<IMigrator>().GenerateScript(current, previous);
        Assert.True(rollback.IndexOf("THROW 51000", StringComparison.Ordinal) < rollback.IndexOf("ALTER TABLE", StringComparison.Ordinal));
        Assert.Contains("ALTER COLUMN [Repetitions] int NOT NULL", rollback);
        Assert.Contains("DROP COLUMN [DurationSeconds]", rollback);
        Assert.DoesNotContain("UPDATE [StudentRoutineExercises]", rollback);
        Assert.DoesNotContain("DEFAULT 0", rollback);
        var upgrade = context.GetService<IMigrator>().GenerateScript(previous, current);
        Assert.Contains("ADD [DurationSeconds] int NULL", upgrade);
        Assert.Contains("ALTER COLUMN [Repetitions] int NULL", upgrade);
        Assert.DoesNotContain("UPDATE [StudentRoutineExercises]", upgrade);
        Assert.DoesNotContain("DROP TABLE", upgrade);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Theory]
    [InlineData(1, null, true)]
    [InlineData(int.MaxValue, null, true)]
    [InlineData(null, 1, true)]
    [InlineData(null, int.MaxValue, true)]
    [InlineData(null, null, false)]
    [InlineData(8, 45, false)]
    [InlineData(0, null, false)]
    [InlineData(-1, null, false)]
    [InlineData(null, 0, false)]
    [InlineData(null, -1, false)]
    [InlineData(0, 45, false)]
    [InlineData(8, 0, false)]
    public async Task Storage_EnforcesExactlyOnePositiveTarget(int? repetitions, int? durationSeconds, bool accepted)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var db = new StaticXDbContext(new DbContextOptionsBuilder<StaticXDbContext>().UseSqlite(connection).Options);
        await SeedAsync(db);
        Assert.Equal(RoutineWriteOutcome.Published, (await PublishAsync(db, null, "Original")).Outcome);
        var write = () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE StudentRoutineExercises SET Repetitions = {repetitions}, DurationSeconds = {durationSeconds}");
        if (accepted)
        {
            Assert.Equal(1, await write());
            var saved = await db.Set<StudentRoutineExercise>().AsNoTracking().SingleAsync();
            Assert.Equal(repetitions, saved.Repetitions);
            Assert.Equal(durationSeconds, saved.DurationSeconds);
        }
        else
        {
            var error = await Assert.ThrowsAsync<SqliteException>(write);
            Assert.Equal(275, error.SqliteExtendedErrorCode);
            var saved = await db.Set<StudentRoutineExercise>().AsNoTracking().SingleAsync();
            Assert.Equal(2, saved.Repetitions);
            Assert.Null(saved.DurationSeconds);
        }
    }

    private static string OperationShape(MigrationOperation operation) => operation switch
    {
        AlterColumnOperation column => $"Alter:{column.Table}:{column.Name}:{column.ColumnType}:{column.IsNullable}:{column.OldColumn.IsNullable}",
        AddColumnOperation column => $"Add:{column.Table}:{column.Name}:{column.ColumnType}:{column.IsNullable}:{column.MaxLength}:{column.DefaultValue}:{column.DefaultValueSql}",
        DropColumnOperation column => $"Drop:{column.Table}:{column.Name}",
        AddCheckConstraintOperation check => $"AddCheck:{check.Table}:{check.Name}:{check.Sql}",
        DropCheckConstraintOperation check => $"DropCheck:{check.Table}:{check.Name}",
        _ => throw new InvalidOperationException($"Unexpected migration operation: {operation.GetType().Name}")
    };

    private static async Task SeedAsync(StaticXDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        db.Gimnasios.Add(new Gimnasio { Id = 1, Nombre = "First" });
        db.Alumnos.Add(new Alumno { Id = 1, GimnasioId = 1, Nombre = "Ana", Apellido = "Student", Email = "student@example.invalid" });
        db.Users.Add(new GymAccount
        {
            Id = "trainer", GymId = 1, Role = AccountRole.Trainer, IsEnabled = true,
            Email = "trainer@example.invalid", NormalizedEmail = "TRAINER@EXAMPLE.INVALID"
        });
        await db.SaveChangesAsync();
    }

    private static Task<RoutineWriteResult> PublishAsync(StaticXDbContext db, Guid? revision, params string[] names) =>
        new RoutineRepository(db).PublishAsync(1, "trainer", new StudentRoutine
        {
            StudentId = 1,
            Days = [new StudentRoutineDay
            {
                StudentId = 1,
                Exercises = names.Select((name, index) => new StudentRoutineExercise
                {
                    StudentId = 1, Position = index, Name = name, Sets = 1, Repetitions = 2
                }).ToList()
            }]
        }, revision, default);

    private static string[] Names(StudentRoutine routine) => Assert.Single(routine.Days).Exercises.Select(x => x.Name).ToArray();

    private sealed class RoutineInsertFault : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public int Attempts { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.StartsWith("INSERT INTO \"StudentRoutineExercises\"", StringComparison.Ordinal) && ++Attempts == 2)
                throw new IOException("Injected routine insert failure.");
            return ValueTask.FromResult(result);
        }
    }
}
