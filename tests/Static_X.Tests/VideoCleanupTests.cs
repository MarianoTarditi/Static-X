using DAL;
using DAL.Repositories.Interfaces;
using Entity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Static_X.Tests;

public sealed class VideoCleanupTests
{
    [Fact]
    public async Task ExpiredUpload_TombstoneCommitsBeforeDeleteAndRejectsLatePublicationThenReapsLatePut()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var key = Key();
        await WithRepository(app, repository => repository.BeginUploadAsync(1, "Trainer", key, now, default));
        var objects = new HashSet<string>();
        await WithRepository(app, async repository =>
        {
            Assert.True(await repository.CleanupOneAsync(now, async (deleted, _) =>
            {
                Assert.Equal(key, deleted);
                using var scope = app.Factory.Services.CreateScope();
                var persisted = await scope.ServiceProvider.GetRequiredService<StaticXDbContext>().Set<VideoObject>().AsNoTracking().SingleAsync();
                Assert.Equal("Cleanup", persisted.State);
                Assert.Equal(now.AddMinutes(5), persisted.NextAttemptAt);
                await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IVideoLibraryRepository>()
                    .PublishAsync(1, "Trainer", Entry(key), null, now, default));
                objects.Remove(deleted);
            }, default));
        });
        objects.Add(key); // A PUT already in flight finishes after the first deletion.
        await WithRepository(app, async repository =>
        {
            Assert.False(await repository.CleanupOneAsync(now.AddHours(23), (_, _) => throw new Exception("Not due."), default));
            Assert.True(await repository.CleanupOneAsync(now.AddDays(1), (deleted, _) => { objects.Remove(deleted); return Task.CompletedTask; }, default));
        });
        Assert.Empty(objects);
        using var check = app.Factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<StaticXDbContext>();
        Assert.Equal("Cleanup", (await db.Set<VideoObject>().SingleAsync()).State);
        Assert.Empty(await db.Set<LibraryVideo>().ToListAsync());
    }

    [Fact]
    public async Task CompetingReplacement_LoserCannotChangeWinnerAndCleanupRetriesNeverSelectActiveKey()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var oldKey = Key(); var winningKey = Key(); var losingKey = Key();
        var original = Entry(oldKey);
        await WithRepository(app, async repository =>
        {
            foreach (var key in new[] { oldKey, winningKey, losingKey })
                await repository.BeginUploadAsync(1, "Trainer", key, now.AddHours(1), default);
            original = await repository.PublishAsync(1, "Trainer", original, null, now, default);
        });
        var winner = Entry(winningKey); winner.Id = original.Id;
        await WithRepository(app, async repository => { winner = await repository.PublishAsync(1, "Trainer", winner, original.Revision, now, default); });
        var loser = Entry(losingKey); loser.Id = original.Id;
        await WithRepository(app, repository => Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.PublishAsync(1, "Trainer", loser, original.Revision, now, default)));
        await WithRepository(app, repository => Assert.ThrowsAsync<IOException>(() =>
            repository.CleanupOneAsync(now, (_, _) => throw new IOException("Injected delete failure."), default)));
        var deleted = new List<string>();
        await WithRepository(app, async repository =>
        {
            Assert.False(await repository.CleanupOneAsync(now.AddMinutes(4), (_, _) => throw new Exception("Lease not due."), default));
            Assert.True(await repository.CleanupOneAsync(now.AddMinutes(5), (key, _) => { deleted.Add(key); return Task.CompletedTask; }, default));
            Assert.True(await repository.CleanupOneAsync(now.AddHours(1), (key, _) => { deleted.Add(key); return Task.CompletedTask; }, default));
            var current = await repository.FindAsync(1, original.Id, default);
            Assert.Equal(winner.Revision, current!.Revision);
            Assert.Equal(winningKey, current.ObjectKey);
            Assert.Equal("Original metadata", current.Title);
        });
        Assert.Equal(new[] { oldKey, losingKey }, deleted);
        Assert.DoesNotContain(winningKey, deleted);
    }

    private static async Task WithRepository(PaymentHttpApp app, Func<IVideoLibraryRepository, Task> operation)
    {
        using var scope = app.Factory.Services.CreateScope();
        await operation(scope.ServiceProvider.GetRequiredService<IVideoLibraryRepository>());
    }

    private static string Key() => $"video/1/{Guid.NewGuid():N}";
    private static LibraryVideo Entry(string key) => new LibraryVideo
    {
        Id = Guid.NewGuid(), GymId = 1, Title = "Original metadata", Category = "exercise", Length = 12,
        ObjectKey = key, CreatedAt = DateTime.UtcNow
    };
}
