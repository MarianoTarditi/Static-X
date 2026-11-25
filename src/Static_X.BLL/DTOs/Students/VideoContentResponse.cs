namespace BLL.DTOs.Students;

/// <summary>An owned playback stream and its resolved byte interval.</summary>
public sealed class VideoContentResponse : IAsyncDisposable
{
    public Stream? Content { get; }
    public long TotalLength { get; }
    public long Start { get; }
    public long Length { get; }
    public bool Partial { get; }
    public bool Satisfiable { get; }
    public Guid Revision { get; }
    public VideoContentResponse(Stream? content, long totalLength, long start, long length, bool partial, bool satisfiable, Guid revision)
    { Content = content; TotalLength = totalLength; Start = start; Length = length; Partial = partial; Satisfiable = satisfiable; Revision = revision; }
    public ValueTask DisposeAsync() => Content?.DisposeAsync() ?? ValueTask.CompletedTask;
}
