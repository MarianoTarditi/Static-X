using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using BLL.Interfaces;
using Microsoft.Extensions.Options;
using Static_X.API.Integrations.Storage;
using Xunit;

namespace Static_X.Tests;

public sealed class VideoObjectStoreTests
{
    private const string Key = "video/7/0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Put_UsesExplicitPrivateBucketImmutableKeyLengthAndCallerOwnedStream()
    {
        using var sdk = new StorageSdk();
        using var adapter = new S3VideoObjectStore(Options.Create(Settings()), sdk);
        IVideoObjectStore store = adapter;
        using var cancellation = new CancellationTokenSource();
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        await store.PutAsync(Key, content, 3, cancellation.Token);
        var request = Assert.IsType<PutObjectRequest>(Assert.Single(sdk.Requests));
        Assert.Equal("test-private-videos", request.BucketName);
        Assert.Equal(Key, request.Key);
        Assert.Equal("*", request.IfNoneMatch);
        Assert.Equal(3, request.Headers.ContentLength);
        Assert.Equal("video/mp4", request.ContentType);
        Assert.Null(request.CannedACL);
        Assert.Same(content, request.InputStream);
        Assert.False(request.AutoCloseStream);
        Assert.False(request.AutoResetStreamPosition);
        Assert.True(content.CanRead);
        Assert.Equal(cancellation.Token, sdk.Token);
    }

    [Fact]
    public async Task Read_UsesInclusiveRangeAndTransfersResponseStreamOwnership()
    {
        using var sdk = new StorageSdk();
        using var adapter = new S3VideoObjectStore(Options.Create(Settings()), sdk);
        IVideoObjectStore store = adapter;
        using var cancellation = new CancellationTokenSource();
        var content = new MemoryStream(new byte[] { 7, 8, 9 });
        sdk.Response = new GetObjectResponse
        {
            HttpStatusCode = HttpStatusCode.PartialContent,
            ContentLength = 3,
            ContentRange = "bytes 4-6/10",
            ResponseStream = content
        };
        await using (var result = await store.OpenReadAsync(Key, 4, 3, cancellation.Token))
        {
            var request = Assert.IsType<GetObjectRequest>(Assert.Single(sdk.Requests));
            Assert.Equal("test-private-videos", request.BucketName);
            Assert.Equal(Key, request.Key);
            Assert.Equal("bytes=4-6", request.ByteRange.FormattedByteRange);
            Assert.Equal(cancellation.Token, sdk.Token);
            Assert.True(content.CanRead);
            using var buffer = new MemoryStream();
            await result.CopyToAsync(buffer);
            Assert.Equal(new byte[] { 7, 8, 9 }, buffer.ToArray());
            Assert.False(result.CanWrite);
        }
        Assert.False(content.CanRead);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, 3, "bytes 4-6/10")]
    [InlineData(HttpStatusCode.PartialContent, 4, "bytes 4-6/10")]
    [InlineData(HttpStatusCode.PartialContent, 3, "bytes 0-2/10")]
    [InlineData(HttpStatusCode.PartialContent, 3, "bytes 4-6/6")]
    public async Task Read_InvalidStorageIntervalFailsAndDisposesResponse(HttpStatusCode status, long length, string range)
    {
        using var sdk = new StorageSdk();
        using var adapter = new S3VideoObjectStore(Options.Create(Settings()), sdk);
        IVideoObjectStore store = adapter;
        var content = new MemoryStream(new byte[] { 7, 8, 9 });
        sdk.Response = new GetObjectResponse { HttpStatusCode = status, ContentLength = length, ContentRange = range, ResponseStream = content };
        await Assert.ThrowsAsync<IOException>(() => store.OpenReadAsync(Key, 4, 3, default));
        Assert.False(content.CanRead);
    }

    [Fact]
    public async Task Delete_RepeatedRequestsRemainIdempotentAndUseExplicitBucketAndKey()
    {
        using var sdk = new StorageSdk();
        using var adapter = new S3VideoObjectStore(Options.Create(Settings()), sdk);
        IVideoObjectStore store = adapter;
        using var cancellation = new CancellationTokenSource();
        await store.DeleteAsync(Key, cancellation.Token);
        await store.DeleteAsync(Key, cancellation.Token);
        Assert.Equal(2, sdk.Requests.Count);
        Assert.All(sdk.Requests, value =>
        {
            var request = Assert.IsType<DeleteObjectRequest>(value);
            Assert.Equal("test-private-videos", request.BucketName);
            Assert.Equal(Key, request.Key);
        });
        Assert.Equal(cancellation.Token, sdk.Token);
    }

    [Theory]
    [InlineData("put")]
    [InlineData("read")]
    [InlineData("delete")]
    public async Task StorageFailure_IsPropagatedWithoutFakeSuccess(string operation)
    {
        using var sdk = new StorageSdk { Failure = new AmazonS3Exception("Injected storage failure.") };
        using var adapter = new S3VideoObjectStore(Options.Create(Settings()), sdk);
        IVideoObjectStore store = adapter;
        using var content = new MemoryStream(new byte[] { 1 });
        var error = await Assert.ThrowsAsync<AmazonS3Exception>(() => ExecuteAsync(store, operation, content, default));
        Assert.Same(sdk.Failure, error);
        Assert.Single(sdk.Requests);
        Assert.True(content.CanRead);
    }

    [Theory]
    [InlineData("put")]
    [InlineData("read")]
    [InlineData("delete")]
    public async Task Cancellation_PreventsExternalCalls(string operation)
    {
        using var sdk = new StorageSdk();
        using var adapter = new S3VideoObjectStore(Options.Create(Settings()), sdk);
        IVideoObjectStore store = adapter;
        using var content = new MemoryStream(new byte[] { 1 });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExecuteAsync(store, operation, content, cancellation.Token));
        Assert.Empty(sdk.Requests);
    }

    [Theory]
    [InlineData("bucket")]
    [InlineData("region")]
    [InlineData("access")]
    [InlineData("secret")]
    [InlineData("endpoint")]
    public async Task MissingOrUnsafeConfiguration_IsLazyAndFailsBeforeStorageCalls(string field)
    {
        var options = Settings();
        if (field == "bucket") options.BucketName = "";
        if (field == "region") options.Region = "";
        if (field == "access") options.AccessKeyId = "";
        if (field == "secret") options.SecretAccessKey = "";
        if (field == "endpoint") options.BucketName = "https://untrusted.invalid/bucket";
        using var adapter = new S3VideoObjectStore(Options.Create(options));
        IVideoObjectStore store = adapter;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAsync(Key, default));
    }

    [Fact]
    public async Task InvalidInputs_DoNotReachStorage()
    {
        using var sdk = new StorageSdk();
        using var adapter = new S3VideoObjectStore(Options.Create(Settings()), sdk);
        IVideoObjectStore store = adapter;
        using var content = new MemoryStream(new byte[] { 1 });
        await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync(Key, content, 2, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync(Key, content, 100000001, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync("https://untrusted.invalid/video", default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(Key + "\n", default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.OpenReadAsync(Key, -1, 1, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.OpenReadAsync(Key, 0, 0, default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.OpenReadAsync(Key, long.MaxValue, 2, default));
        Assert.Empty(sdk.Requests);
    }

    private static Task ExecuteAsync(IVideoObjectStore store, string operation, Stream content, CancellationToken token) => operation switch
    {
        "put" => store.PutAsync(Key, content, 1, token),
        "read" => store.OpenReadAsync(Key, 0, 1, token),
        "delete" => store.DeleteAsync(Key, token),
        _ => throw new ArgumentException(nameof(operation))
    };

    private static VideoStorageOptions Settings() => new VideoStorageOptions
    {
        BucketName = "test-private-videos",
        Region = "us-east-1",
        AccessKeyId = "test-access",
        SecretAccessKey = "test-secret"
    };

    // Replaces SDK operations completely: no HTTP handler, DNS, credential discovery, or real bucket is used.
    private sealed class StorageSdk : AmazonS3Client
    {
        public List<AmazonWebServiceRequest> Requests { get; } = new List<AmazonWebServiceRequest>();
        public CancellationToken Token { get; private set; }
        public Exception? Failure { get; set; }
        public GetObjectResponse Response { get; set; } = new GetObjectResponse();

        public StorageSdk() : base(new AnonymousAWSCredentials(), new AmazonS3Config { RegionEndpoint = RegionEndpoint.USEast1, IgnoreConfiguredEndpointUrls = true }) { }

        public override Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default) =>
            Capture(request, cancellationToken, new PutObjectResponse());
        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default) =>
            Capture(request, cancellationToken, Response);
        public override Task<DeleteObjectResponse> DeleteObjectAsync(DeleteObjectRequest request, CancellationToken cancellationToken = default) =>
            Capture(request, cancellationToken, new DeleteObjectResponse());

        private Task<T> Capture<T>(AmazonWebServiceRequest request, CancellationToken token, T result)
        {
            Requests.Add(request);
            Token = token;
            return Failure is null ? Task.FromResult(result) : Task.FromException<T>(Failure);
        }
    }
}
