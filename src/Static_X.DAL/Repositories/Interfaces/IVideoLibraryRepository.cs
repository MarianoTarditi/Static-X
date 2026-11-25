using Entity;

namespace DAL.Repositories.Interfaces;

public interface IVideoLibraryRepository
{
    Task<IReadOnlyList<LibraryVideo>> ListAsync(int gymId, int skip, int take, string? category, CancellationToken cancellationToken);
    Task<LibraryVideo?> FindAsync(int gymId, Guid videoId, CancellationToken cancellationToken);
    Task BeginUploadAsync(int gymId, string actorId, string key, DateTime expiresAt, CancellationToken cancellationToken);
    Task<LibraryVideo> PublishAsync(int gymId, string actorId, LibraryVideo video, Guid? expectedRevision, DateTime now, CancellationToken cancellationToken);
    Task DeleteAsync(int gymId, string actorId, Guid videoId, Guid expectedRevision, DateTime now, CancellationToken cancellationToken);
    Task<bool> CleanupOneAsync(DateTime now, Func<string, CancellationToken, Task> delete, CancellationToken cancellationToken);
}
