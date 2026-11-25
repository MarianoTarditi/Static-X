namespace Entity;

public sealed class LibraryVideo
{
    public Guid Id { get; set; }
    public int GymId { get; set; }
    public Guid Revision { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ObjectKey { get; set; } = string.Empty;
    public long Length { get; set; }
    public DateTime CreatedAt { get; set; }
}
