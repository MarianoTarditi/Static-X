namespace BLL.DTOs.Students;

/// <summary>A gym video without private storage coordinates.</summary>
public sealed class VideoResponse
{
    public Guid Id { get; }
    public Guid Revision { get; }
    public string Title { get; }
    public string Category { get; }
    public string? Description { get; }
    public long Length { get; }
    public VideoResponse(Guid id, Guid revision, string title, string category, string? description, long length)
    { Id = id; Revision = revision; Title = title; Category = category; Description = description; Length = length; }
}
