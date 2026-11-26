using DAL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Static_X.Tests;

public sealed class VideoLibraryPersistenceTests
{
    [Fact]
    public void SqlServerMigration_AddsOnlyVideoTablesWithRestrictiveKeysConstraintsAndIndexesOffline()
    {
        using var context = CreateContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var current = Assert.Single(migrations.Migrations.Keys, id => id.EndsWith("_AddVideoLibrary", StringComparison.Ordinal));
        const string previous = "20261123010000_AddExerciseDuration";
        Assert.Equal(previous, migrations.Migrations.Keys.TakeWhile(id => id != current).Last());
        Assert.False(context.Database.HasPendingModelChanges());
        var migration = migrations.CreateMigration(migrations.Migrations[current], context.Database.ProviderName!);
        Assert.Equal(6, migration.UpOperations.Count);
        var tables = migration.UpOperations.OfType<CreateTableOperation>().ToArray();
        Assert.Equal(new[] { "VideoObjects", "LibraryVideos" }, tables.Select(table => table.Name));
        var objects = tables[0];
        var videos = tables[1];
        Assert.Equal(new[] { "Key" }, objects.PrimaryKey!.Columns);
        Assert.Equal(new[] { "Id" }, videos.PrimaryKey!.Columns);
        Assert.Equal(new[] { "GymId", "Key", "NextAttemptAt", "PendingUntil", "State" }, objects.Columns.Select(column => column.Name).Order());
        Assert.Equal(new[] { "Category", "CreatedAt", "Description", "GymId", "Id", "Length", "ObjectKey", "Revision", "Title" },
            videos.Columns.Select(column => column.Name).Order());
        Assert.Equal(("CK_VideoObjects_State", "[State] IN ('Pending', 'Active', 'Cleanup')"),
            (Assert.Single(objects.CheckConstraints).Name, objects.CheckConstraints[0].Sql));
        Assert.Equal(new[] { ("CK_LibraryVideos_Category", "[Category] IN ('informational', 'exercise')"),
                ("CK_LibraryVideos_Length", "[Length] > 0 AND [Length] <= 100000000") },
            videos.CheckConstraints.Select(check => (check.Name, check.Sql)).OrderBy(check => check.Name));
        Assert.Equal(new[] { ("VideoObjects", "GymId", "Gimnasios", "Id"),
                ("LibraryVideos", "GymId", "Gimnasios", "Id"), ("LibraryVideos", "ObjectKey", "VideoObjects", "Key") }.Order(),
            tables.SelectMany(table => table.ForeignKeys).Select(key =>
                (key.Table, Assert.Single(key.Columns), key.PrincipalTable, Assert.Single(key.PrincipalColumns!))).Order());
        Assert.All(tables.SelectMany(table => table.ForeignKeys), key => Assert.Equal(ReferentialAction.Restrict, key.OnDelete));
        var indexes = migration.UpOperations.OfType<CreateIndexOperation>().ToArray();
        Assert.Equal(new[] { ("LibraryVideos", "GymId,CreatedAt,Id", false), ("LibraryVideos", "ObjectKey", true),
                ("VideoObjects", "GymId", false), ("VideoObjects", "State,NextAttemptAt", false) },
            indexes.Select(index => (index.Table, string.Join(",", index.Columns), index.IsUnique)).Order());

        var initializer = context.GetService<IModelRuntimeInitializer>();
        var predecessor = migrations.CreateMigration(migrations.Migrations[previous], context.Database.ProviderName!);
        var before = initializer.Initialize(predecessor.TargetModel).GetRelationalModel();
        var after = initializer.Initialize(migration.TargetModel).GetRelationalModel();
        var differ = context.GetService<IMigrationsModelDiffer>();
        Assert.Equal(migration.UpOperations.Select(OperationShape).Order(), differ.GetDifferences(before, after).Select(OperationShape).Order());
        Assert.False(differ.HasDifferences(after, context.GetService<IDesignTimeModel>().Model.GetRelationalModel()));
        var script = context.GetService<IMigrator>().GenerateScript(previous, current);
        Assert.Equal(2, script.Split("CREATE TABLE [", StringSplitOptions.None).Length - 1);
        Assert.Contains("CREATE TABLE [VideoObjects]", script);
        Assert.Contains("CREATE TABLE [LibraryVideos]", script);
        Assert.DoesNotContain("ALTER TABLE", script);
        Assert.DoesNotContain("DROP TABLE", script);
        Assert.DoesNotContain("UPDATE [", script);
        Assert.DoesNotContain("INSERT INTO [AspNetUsers]", script);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    [Fact]
    public void SqlServerMigration_RollbackGuardsAllVideoDataIncludingCleanupTombstonesBeforeDropsOffline()
    {
        using var context = CreateContext();
        var migrations = context.GetService<IMigrationsAssembly>();
        var current = Assert.Single(migrations.Migrations.Keys, id => id.EndsWith("_AddVideoLibrary", StringComparison.Ordinal));
        const string previous = "20261123010000_AddExerciseDuration";
        var migration = migrations.CreateMigration(migrations.Migrations[current], context.Database.ProviderName!);
        Assert.Equal(3, migration.DownOperations.Count);
        var guard = Assert.IsType<SqlOperation>(migration.DownOperations[0]);
        Assert.False(guard.SuppressTransaction);
        Assert.Contains("FROM [LibraryVideos] WITH (TABLOCKX, HOLDLOCK)", guard.Sql);
        Assert.Contains("FROM [VideoObjects] WITH (TABLOCKX, HOLDLOCK)", guard.Sql);
        Assert.Contains("THROW 51000", guard.Sql);
        Assert.DoesNotContain("WHERE", guard.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "LibraryVideos", "VideoObjects" }, migration.DownOperations.Skip(1).Select(operation => Assert.IsType<DropTableOperation>(operation).Name));
        var initializer = context.GetService<IModelRuntimeInitializer>();
        var predecessor = migrations.CreateMigration(migrations.Migrations[previous], context.Database.ProviderName!);
        Assert.Equal(context.GetService<IMigrationsModelDiffer>().GetDifferences(
                initializer.Initialize(migration.TargetModel).GetRelationalModel(), initializer.Initialize(predecessor.TargetModel).GetRelationalModel())
                .Select(OperationShape).Order(), migration.DownOperations.Skip(1).Select(OperationShape).Order());
        var script = context.GetService<IMigrator>().GenerateScript(current, previous);
        Assert.Contains("BEGIN TRANSACTION", script);
        var firstDrop = script.IndexOf("DROP TABLE", StringComparison.Ordinal);
        Assert.True(firstDrop > script.IndexOf("FROM [LibraryVideos] WITH (TABLOCKX, HOLDLOCK)", StringComparison.Ordinal));
        Assert.True(firstDrop > script.IndexOf("FROM [VideoObjects] WITH (TABLOCKX, HOLDLOCK)", StringComparison.Ordinal));
        Assert.True(firstDrop > script.IndexOf("THROW 51000", StringComparison.Ordinal));
        Assert.Equal(2, script.Split("DROP TABLE [", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("UPDATE [", script);
        Assert.DoesNotContain("DELETE FROM [VideoObjects]", script);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
    }

    private static string OperationShape(MigrationOperation operation) => operation switch
    {
        CreateTableOperation table => $"Create:{table.Name}:{string.Join(",", table.Columns.Select(column => $"{column.Name}:{column.ColumnType}:{column.IsNullable}:{column.MaxLength}").Order())}",
        CreateIndexOperation index => $"Index:{index.Table}:{index.Name}:{string.Join(",", index.Columns)}:{index.IsUnique}:{index.Filter}",
        DropTableOperation table => $"Drop:{table.Name}",
        _ => throw new InvalidOperationException($"Unexpected video migration operation: {operation.GetType().Name}")
    };

    private static StaticXDbContext CreateContext() => new(new DbContextOptionsBuilder<StaticXDbContext>()
        .UseSqlServer("Server=unused;Database=unused;Integrated Security=true").Options);
}
