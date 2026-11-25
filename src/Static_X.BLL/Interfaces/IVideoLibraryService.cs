using BLL.DTOs.Students;
using Entity;

namespace BLL.Services.Interfaces;

public interface IVideoLibraryService
{
    Task<VideoListResponse> ListAsync(int gymId, AccountRole role, VideoListRequest request, CancellationToken cancellationToken);
    Task<VideoResponse> UploadAsync(int gymId, AccountRole role, string actorId, CreateVideoRequest request, Stream content, long length, CancellationToken cancellationToken);
    Task<VideoResponse> ReplaceAsync(int gymId, AccountRole role, string actorId, Guid videoId, Guid expectedRevision, Stream content, long length, CancellationToken cancellationToken);
    Task DeleteAsync(int gymId, AccountRole role, string actorId, Guid videoId, Guid expectedRevision, CancellationToken cancellationToken);
    Task<VideoContentResponse> ReadAsync(int gymId, AccountRole role, Guid videoId, VideoRangeRequest request, CancellationToken cancellationToken);
    Task<bool> CleanupOneAsync(CancellationToken cancellationToken);
}
