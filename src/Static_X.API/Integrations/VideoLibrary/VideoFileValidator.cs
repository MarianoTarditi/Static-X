using System.Buffers.Binary;
using System.Text;

namespace Static_X.API.Integrations.VideoLibrary;

/// <summary>Checks bounded ISO BMFF box structure and an MP4 video handler, not client MIME or filenames.</summary>
internal static class VideoFileValidator
{
    public static async Task ValidateAsync(Stream stream, long length, CancellationToken cancellationToken)
    {
        if (length is <= 0 or > 100000000 || !stream.CanSeek || stream.Length != length) throw new ArgumentException("Invalid MP4 length.");
        var boxes = 0;
        var ftyp = false;
        var moov = false;
        var media = false;
        var video = false;
        async Task ScanAsync(long end, int depth, string parent)
        {
            while (stream.Position < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++boxes > 100000 || end - stream.Position < 8) throw new ArgumentException("Invalid MP4 boxes.");
                var start = stream.Position;
                var header = new byte[8];
                await stream.ReadExactlyAsync(header, cancellationToken);
                long size = BinaryPrimitives.ReadUInt32BigEndian(header);
                var type = Encoding.ASCII.GetString(header, 4, 4);
                var headerLength = 8;
                if (size == 1)
                {
                    if (end - stream.Position < 8) throw new ArgumentException("Invalid MP4 box.");
                    await stream.ReadExactlyAsync(header, cancellationToken);
                    var extended = BinaryPrimitives.ReadUInt64BigEndian(header);
                    if (extended > long.MaxValue) throw new ArgumentException("Invalid MP4 box.");
                    size = (long)extended;
                    headerLength = 16;
                }
                if (size == 0) size = end - start;
                if (size < headerLength || size > end - start) throw new ArgumentException("Invalid MP4 box.");
                var next = start + size;
                if (parent == "root" && type == "ftyp")
                {
                    if (ftyp || start != 0 || size - headerLength < 8 || (size - headerLength) % 4 != 0 || size > 4096)
                        throw new ArgumentException("Invalid MP4 brands.");
                    var brands = new byte[(int)(size - headerLength)];
                    await stream.ReadExactlyAsync(brands, cancellationToken);
                    for (var i = 0; i < brands.Length; i += 4)
                        if (i != 4 && Encoding.ASCII.GetString(brands, i, 4) is "isom" or "iso2" or "mp41" or "mp42" or "avc1") ftyp = true;
                }
                if (parent == "root" && type == "mdat" && size > headerLength) media = true;
                if (parent == "mdia" && type == "hdlr" && size - headerLength >= 12)
                {
                    var handler = new byte[12];
                    await stream.ReadExactlyAsync(handler, cancellationToken);
                    video |= Encoding.ASCII.GetString(handler, 8, 4) == "vide";
                }
                if ((parent == "root" && type == "moov") || (parent == "moov" && type == "trak") || (parent == "trak" && type == "mdia"))
                {
                    if (depth >= 3) throw new ArgumentException("Invalid MP4 nesting.");
                    if (type == "moov") moov = true;
                    await ScanAsync(next, depth + 1, type);
                }
                stream.Position = next;
            }
        }
        try
        {
            await ScanAsync(length, 0, "root");
            if (!ftyp || !moov || !media || !video) throw new ArgumentException("An MP4 video track is required.");
        }
        catch (EndOfStreamException) { throw new ArgumentException("Truncated MP4."); }
        finally { stream.Position = 0; }
    }
}
