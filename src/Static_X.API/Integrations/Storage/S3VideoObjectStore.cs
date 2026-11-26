using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;
using BLL.Interfaces;
using Microsoft.Extensions.Options;

namespace Static_X.API.Integrations.Storage;

/// <summary>Stores immutable private objects without exposing storage URLs to callers.</summary>
public sealed class S3VideoObjectStore : IVideoObjectStore, IDisposable
{
    private readonly VideoStorageOptions options;
    private readonly Lazy<IAmazonS3> client;
    private readonly bool ownsClient;

    public S3VideoObjectStore(IOptions<VideoStorageOptions> options, IAmazonS3? client = null)
    {
        this.options = options.Value;
        this.client = new Lazy<IAmazonS3>(() => client ?? this.options.CreateClient());
        ownsClient = client is null;
    }

    public async Task PutAsync(string key, Stream content, long length, CancellationToken cancellationToken)
    {
        Validate(key, cancellationToken);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead || length is <= 0 or > 100000000 ||
            content.CanSeek && content.Length - content.Position != length)
            throw new ArgumentException("Invalid video stream length.");
        var request = new PutObjectRequest
        {
            BucketName = options.BucketName,
            Key = key,
            InputStream = content,
            ContentType = "video/mp4",
            IfNoneMatch = "*",
            AutoCloseStream = false,
            AutoResetStreamPosition = false
        };
        request.Headers.ContentLength = length;
        await client.Value.PutObjectAsync(request, cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(string key, long offset, long length, CancellationToken cancellationToken)
    {
        Validate(key, cancellationToken);
        if (offset < 0 || length <= 0 || offset > long.MaxValue - length)
            throw new ArgumentOutOfRangeException(nameof(length));
        var end = offset + length - 1;
        var response = await client.Value.GetObjectAsync(new GetObjectRequest
        {
            BucketName = options.BucketName,
            Key = key,
            ByteRange = new ByteRange(offset, end)
        }, cancellationToken);
        var prefix = FormattableString.Invariant($"bytes {offset}-{end}/");
        if (response.HttpStatusCode != HttpStatusCode.PartialContent || response.ContentLength != length ||
            response.ContentRange is null || !response.ContentRange.StartsWith(prefix, StringComparison.Ordinal) ||
            !long.TryParse(response.ContentRange[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var total) || total <= end)
        {
            response.Dispose();
            throw new IOException("Storage did not return the requested video interval.");
        }
        return new ResponseStream(response);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        Validate(key, cancellationToken);
        await client.Value.DeleteObjectAsync(new DeleteObjectRequest { BucketName = options.BucketName, Key = key }, cancellationToken);
    }

    private void Validate(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options.Validate();
        if (key is null || !Regex.IsMatch(key, "\\Avideo/[1-9][0-9]*/[a-f0-9]{32}\\z"))
            throw new ArgumentException("Invalid private video object key.", nameof(key));
    }

    public void Dispose()
    {
        if (ownsClient && client.IsValueCreated) client.Value.Dispose();
    }

    // The caller owns the returned stream, which also owns the SDK response/connection.
    private sealed class ResponseStream : Stream
    {
        private readonly GetObjectResponse response;
        public ResponseStream(GetObjectResponse response) { this.response = response; }
        public override bool CanRead => response.ResponseStream.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => response.ContentLength;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => response.ResponseStream.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            response.ResponseStream.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            response.ResponseStream.ReadAsync(buffer, offset, count, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing) response.Dispose();
            base.Dispose(disposing);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
