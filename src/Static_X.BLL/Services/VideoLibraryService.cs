using AutoMapper;
using BLL.DTOs.Students;
using BLL.Interfaces;
using BLL.Services.Interfaces;
using DAL.Repositories.Interfaces;
using Entity;

namespace BLL;

/// <summary>Autoriza y coordina la publicación y reproducción privada de videos.</summary>
public sealed class VideoLibraryService : IVideoLibraryService
{
    private readonly IVideoLibraryRepository repository;
    private readonly IVideoObjectStore store;
    private readonly IMapper mapper;
    private readonly TimeProvider clock;

    /// <summary>Recibe persistencia, almacenamiento privado, mapeo y reloj.</summary>
    public VideoLibraryService(IVideoLibraryRepository repository, IVideoObjectStore store, IMapper mapper, TimeProvider clock)
    { this.repository = repository; this.store = store; this.mapper = mapper; this.clock = clock; }

    /// <summary>Valida el ámbito y devuelve únicamente una página del catálogo.</summary>
    public async Task<VideoListResponse> ListAsync(int gymId, AccountRole role, VideoListRequest request, CancellationToken cancellationToken)
    {
        RequireAccess(gymId, role, false);
        if (request.Page is < 1 or > 100000 || request.PageSize is < 1 or > 100 ||
            request.Category is not (null or "informational" or "exercise")) throw new ArgumentException("Invalid catalog query.");
        var items = await repository.ListAsync(gymId, (request.Page - 1) * request.PageSize, request.PageSize, request.Category, cancellationToken);
        return new VideoListResponse(mapper.Map<List<VideoResponse>>(items));
    }

    /// <summary>Valida los metadatos y publica una nueva entrada tras completar el almacenamiento.</summary>
    public async Task<VideoResponse> UploadAsync(int gymId, AccountRole role, string actorId, CreateVideoRequest request, Stream content, long length, CancellationToken cancellationToken)
    {
        RequireAccess(gymId, role, true);
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 200 || request.Description?.Length > 2000 ||
            request.Category is not ("informational" or "exercise")) throw new ArgumentException("Invalid video metadata.");
        var video = new LibraryVideo { Id = Guid.NewGuid(), GymId = gymId, Title = request.Title.Trim(), Category = request.Category,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(), CreatedAt = clock.GetUtcNow().UtcDateTime };
        return await StoreAndPublishAsync(gymId, actorId, video, null, content, length, cancellationToken);
    }

    /// <summary>Conserva los metadatos y sustituye únicamente el contenido con revisión vigente.</summary>
    public async Task<VideoResponse> ReplaceAsync(int gymId, AccountRole role, string actorId, Guid videoId, Guid expectedRevision, Stream content, long length, CancellationToken cancellationToken)
    {
        RequireAccess(gymId, role, true);
        var current = await repository.FindAsync(gymId, videoId, cancellationToken) ?? throw new KeyNotFoundException();
        if (expectedRevision == Guid.Empty || current.Revision != expectedRevision) throw new InvalidOperationException("Revision conflict.");
        return await StoreAndPublishAsync(gymId, actorId, new LibraryVideo { Id = videoId }, expectedRevision, content, length, cancellationToken);
    }

    /// <summary>Autoriza la baja y delega su confirmación atómica junto con la limpieza.</summary>
    public Task DeleteAsync(int gymId, AccountRole role, string actorId, Guid videoId, Guid expectedRevision, CancellationToken cancellationToken)
    {
        RequireAccess(gymId, role, true);
        return repository.DeleteAsync(gymId, actorId, videoId, expectedRevision, clock.GetUtcNow().UtcDateTime, cancellationToken);
    }

    /// <summary>Resuelve un rango contra el objeto actual sin divulgar su clave privada.</summary>
    public async Task<VideoContentResponse> ReadAsync(int gymId, AccountRole role, Guid videoId, VideoRangeRequest request, CancellationToken cancellationToken)
    {
        RequireAccess(gymId, role, false);
        var video = await repository.FindAsync(gymId, videoId, cancellationToken) ?? throw new KeyNotFoundException();
        var partial = request.Start.HasValue || request.End.HasValue || request.Suffix.HasValue;
        var start = request.Suffix.HasValue ? Math.Max(0, video.Length - request.Suffix.Value) : request.Start ?? 0;
        var end = request.Suffix.HasValue ? video.Length - 1 : Math.Min(request.End ?? video.Length - 1, video.Length - 1);
        if (start < 0 || start >= video.Length || end < start || request.Suffix is <= 0)
            return new VideoContentResponse(null, video.Length, 0, 0, partial, false, video.Revision);
        var length = end - start + 1;
        var stream = await store.OpenReadAsync(video.ObjectKey, start, length, cancellationToken);
        return new VideoContentResponse(stream, video.Length, start, length, partial, true, video.Revision);
    }

    /// <summary>Procesa una limpieza persistida mediante una eliminación externa idempotente.</summary>
    public Task<bool> CleanupOneAsync(CancellationToken cancellationToken) =>
        repository.CleanupOneAsync(clock.GetUtcNow().UtcDateTime, store.DeleteAsync, cancellationToken);

    /// <summary>Registra primero la clave pendiente y permite publicar solo después de una escritura completa.</summary>
    private async Task<VideoResponse> StoreAndPublishAsync(int gymId, string actorId, LibraryVideo video, Guid? revision, Stream content, long length, CancellationToken cancellationToken)
    {
        if (length is <= 0 or > 100000000) throw new ArgumentException("Invalid video size.");
        video.ObjectKey = $"video/{gymId}/{Guid.NewGuid():N}";
        video.Length = length;
        await repository.BeginUploadAsync(gymId, actorId, video.ObjectKey, clock.GetUtcNow().UtcDateTime.AddHours(1), cancellationToken);
        await store.PutAsync(video.ObjectKey, content, length, cancellationToken);
        var persisted = await repository.PublishAsync(gymId, actorId, video, revision, clock.GetUtcNow().UtcDateTime, cancellationToken);
        return mapper.Map<VideoResponse>(persisted);
    }

    /// <summary>Restringe las operaciones a los roles de gimnasio expresamente autorizados.</summary>
    private static void RequireAccess(int gymId, AccountRole role, bool write)
    {
        if (gymId <= 0 || (write ? role != AccountRole.Trainer : role is not (AccountRole.Trainer or AccountRole.Student)))
            throw new UnauthorizedAccessException();
    }
}
