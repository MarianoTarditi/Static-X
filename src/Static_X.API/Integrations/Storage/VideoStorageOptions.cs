using System.Text.RegularExpressions;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;

namespace Static_X.API.Integrations.Storage;

/// <summary>Explicit external settings for private, standard AWS S3 storage.</summary>
public sealed class VideoStorageOptions
{
    public string BucketName { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string AccessKeyId { get; set; } = string.Empty;
    public string SecretAccessKey { get; set; } = string.Empty;

    public VideoStorageOptions() { }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(BucketName) || !Regex.IsMatch(BucketName, "\\A[a-z0-9][a-z0-9-]{1,61}[a-z0-9]\\z") ||
            BucketName.EndsWith("--x-s3", StringComparison.Ordinal) ||
            !RegionEndpoint.EnumerableAllRegions.Any(region => region.SystemName == Region) ||
            string.IsNullOrWhiteSpace(AccessKeyId) || string.IsNullOrWhiteSpace(SecretAccessKey))
            throw new InvalidOperationException("VideoStorage requires an explicit standard S3 bucket, AWS region, and credentials.");
    }

    internal IAmazonS3 CreateClient()
    {
        Validate();
        return new AmazonS3Client(new BasicAWSCredentials(AccessKeyId, SecretAccessKey), new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(Region),
            IgnoreConfiguredEndpointUrls = true,
            UseHttp = false,
            ForcePathStyle = true,
            MaxErrorRetry = 0,
            Timeout = TimeSpan.FromSeconds(60)
        });
    }
}
