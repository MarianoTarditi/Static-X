using DAL.Repositories.Interfaces;
using Entity;
using Microsoft.EntityFrameworkCore;

namespace DAL.Repositories.Implementation;

/// <summary>Persiste el catálogo y serializa publicación y limpieza por gimnasio.</summary>
public sealed class VideoLibraryRepository : IVideoLibraryRepository
{
    private readonly StaticXDbContext context;

    /// <summary>Recibe el contexto de persistencia de la videoteca.</summary>
    public VideoLibraryRepository(StaticXDbContext context) { this.context = context; }

    /// <summary>Consulta una página ordenada dentro del gimnasio indicado.</summary>
    public async Task<IReadOnlyList<LibraryVideo>> ListAsync(int gymId, int skip, int take, string? category, CancellationToken cancellationToken) =>
        await context.Set<LibraryVideo>().AsNoTracking().Where(v => v.GymId == gymId && (category == null || v.Category == category))
            .OrderByDescending(v => v.CreatedAt).ThenBy(v => v.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);

    /// <summary>Busca una entrada sin distinguir identificadores inexistentes de otros gimnasios.</summary>
    public Task<LibraryVideo?> FindAsync(int gymId, Guid videoId, CancellationToken cancellationToken) =>
        context.Set<LibraryVideo>().AsNoTracking().SingleOrDefaultAsync(v => v.GymId == gymId && v.Id == videoId, cancellationToken);

    /// <summary>Registra la clave pendiente antes de permitir cualquier escritura externa.</summary>
    public async Task BeginUploadAsync(int gymId, string actorId, string key, DateTime expiresAt, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await TenantAccountLock.AcquireAsync(context, gymId, cancellationToken);
        await RequireTrainerAsync(gymId, actorId, cancellationToken);
        context.Set<VideoObject>().Add(new VideoObject { Key = key, GymId = gymId, PendingUntil = expiresAt, NextAttemptAt = expiresAt });
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Publica una clave preparada y registra la limpieza anterior en la misma transacción.</summary>
    public async Task<LibraryVideo> PublishAsync(int gymId, string actorId, LibraryVideo video, Guid? expectedRevision, DateTime now, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await TenantAccountLock.AcquireAsync(context, gymId, cancellationToken);
        await RequireTrainerAsync(gymId, actorId, cancellationToken);
        var pending = await context.Set<VideoObject>().SingleAsync(o => o.Key == video.ObjectKey && o.GymId == gymId, cancellationToken);
        if (pending.State != "Pending" || pending.PendingUntil <= now) throw new InvalidOperationException("Upload expired.");
        LibraryVideo result;
        if (expectedRevision is null)
        {
            video.GymId = gymId;
            video.Revision = Guid.NewGuid();
            context.Set<LibraryVideo>().Add(video);
            result = video;
        }
        else
        {
            var current = await context.Set<LibraryVideo>().SingleOrDefaultAsync(v => v.GymId == gymId && v.Id == video.Id, cancellationToken)
                ?? throw new KeyNotFoundException();
            if (current.Revision != expectedRevision.Value) throw new InvalidOperationException("Revision conflict.");
            var previous = await context.Set<VideoObject>().SingleAsync(o => o.Key == current.ObjectKey, cancellationToken);
            previous.State = "Cleanup";
            previous.NextAttemptAt = now;
            current.ObjectKey = video.ObjectKey;
            current.Length = video.Length;
            current.Revision = Guid.NewGuid();
            result = current;
        }
        pending.State = "Active";
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        context.ChangeTracker.Clear();
        return result;
    }

    /// <summary>Elimina la entrada y confirma su limpieza duradera con la revisión esperada.</summary>
    public async Task DeleteAsync(int gymId, string actorId, Guid videoId, Guid expectedRevision, DateTime now, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await TenantAccountLock.AcquireAsync(context, gymId, cancellationToken);
        await RequireTrainerAsync(gymId, actorId, cancellationToken);
        var current = await context.Set<LibraryVideo>().SingleOrDefaultAsync(v => v.GymId == gymId && v.Id == videoId, cancellationToken)
            ?? throw new KeyNotFoundException();
        if (current.Revision != expectedRevision) throw new InvalidOperationException("Revision conflict.");
        var storage = await context.Set<VideoObject>().SingleAsync(o => o.Key == current.ObjectKey, cancellationToken);
        storage.State = "Cleanup";
        storage.NextAttemptAt = now;
        context.Remove(current);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        context.ChangeTracker.Clear();
    }

    /// <summary>Inhabilita una clave abandonada antes de borrar y conserva su reintento incluso ante fallos.</summary>
    public async Task<bool> CleanupOneAsync(DateTime now, Func<string, CancellationToken, Task> delete, CancellationToken cancellationToken)
    {
        var candidate = await context.Set<VideoObject>().AsNoTracking()
            .Where(o => o.State != "Active" && o.NextAttemptAt <= now)
            .OrderBy(o => o.NextAttemptAt).ThenBy(o => o.Key).FirstOrDefaultAsync(cancellationToken);
        if (candidate is null) return false;
        // Commit the tombstone before external I/O: an expired uploader can never publish again.
        await using (var transaction = await context.Database.BeginTransactionAsync(cancellationToken))
        {
            await TenantAccountLock.AcquireAsync(context, candidate.GymId, cancellationToken);
            var claimed = await context.Set<VideoObject>().Where(o => o.Key == candidate.Key && o.State != "Active" && o.NextAttemptAt <= now &&
                    !context.Set<LibraryVideo>().Any(v => v.ObjectKey == o.Key))
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.State, "Cleanup").SetProperty(o => o.NextAttemptAt, now.AddMinutes(5)), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (claimed == 0) return true;
        }
        await delete(candidate.Key, cancellationToken);
        // Retain and periodically retry tombstones, including after a delayed PUT completes after a crash.
        await context.Set<VideoObject>().Where(o => o.Key == candidate.Key && o.State == "Cleanup")
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, now.AddDays(1)), cancellationToken);
        return true;
    }

    /// <summary>Comprueba el estado y rol persistidos del entrenador dentro de la transacción.</summary>
    private async Task RequireTrainerAsync(int gymId, string actorId, CancellationToken cancellationToken)
    {
        if (!await context.Users.AnyAsync(u => u.Id == actorId && u.GymId == gymId && u.Role == AccountRole.Trainer && u.IsEnabled, cancellationToken))
            throw new UnauthorizedAccessException();
    }
}
