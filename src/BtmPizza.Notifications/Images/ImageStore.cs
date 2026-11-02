using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using BtmPizza.Notifications.Configuration;

namespace BtmPizza.Notifications.Images;

/// <summary>Stores notification images and returns the public https URL the app loads them from.</summary>
public interface IImageStore
{
    bool IsConfigured { get; }

    /// <summary>Stores <paramref name="data"/> under <paramref name="key"/> and returns its public URL.</summary>
    Task<string> PutAsync(string key, byte[] data, string contentType, CancellationToken ct);
}

public sealed class ImageStorageNotConfiguredException() : Exception(
    "Image uploads aren't configured on the server yet (s3 section in credentials.json). Paste an image link instead.");

/// <summary>An S3-compatible bucket (the server's MinIO), path-style, publicly readable.</summary>
public sealed class S3ImageStore : IImageStore, IDisposable
{
    private readonly ImageStorageCredentials? _config;
    private readonly AmazonS3Client? _client;

    public S3ImageStore(ImageStorageCredentials? config)
    {
        _config = config;
        if (config is null) return;
        _client = new AmazonS3Client(new BasicAWSCredentials(config.AccessKey, config.SecretKey), new AmazonS3Config
        {
            ServiceURL = config.Endpoint,
            ForcePathStyle = true,                 // MinIO: http://host:9000/bucket/key
            AuthenticationRegion = "us-east-1",
            // Only send checksums when S3 requires them; older MinIO builds reject the newer default headers.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = TimeSpan.FromSeconds(20),
        });
    }

    public bool IsConfigured => _client is not null;

    public async Task<string> PutAsync(string key, byte[] data, string contentType, CancellationToken ct)
    {
        if (_client is null || _config is null) throw new ImageStorageNotConfiguredException();
        using var stream = new MemoryStream(data, writable: false);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _config.Bucket,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
            UseChunkEncoding = false,
            Headers = { CacheControl = "public, max-age=31536000, immutable" },
        }, ct);
        return $"{_config.PublicBaseUrl}/{key}";
    }

    public void Dispose() => _client?.Dispose();
}

/// <summary>Checks an upload really is an image the iOS app can show, by its first bytes (not the claimed type).</summary>
public static class ImageFormat
{
    public const int MaxBytes = 5 * 1024 * 1024;

    public static (string ContentType, string Extension)? Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return ("image/jpeg", "jpg");
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return ("image/png", "png");
        if (data.Length >= 6 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8))) return ("image/gif", "gif");
        return null;
    }
}
