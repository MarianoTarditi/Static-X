namespace BLL.Interfaces;

/// <summary>Private immutable objects; reads return exactly the requested interval, deletions are idempotent.</summary>
public interface IVideoObjectStore
{
    Task PutAsync(string key, Stream content, long length, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string key, long offset, long length, CancellationToken cancellationToken);
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}
