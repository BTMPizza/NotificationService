using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BtmPizza.Notifications.Configuration;

public static class ApnsEnvironments
{
    public const string Sandbox = "sandbox";
    public const string Production = "production";
    public static readonly string[] All = [Sandbox, Production];
}

public sealed record ApnsCredentials(
    string? TeamId,
    string? KeyId,
    string? PrivateKeyPem,
    string DefaultEnvironment,
    IReadOnlyList<string> Problems)
{
    public bool Configured => Problems.Count == 0;
}

/// <summary>
/// Where the admin app's notification images go: an S3-compatible bucket (MinIO) that is publicly readable at
/// <paramref name="PublicBaseUrl"/>. Null in <see cref="Credentials.ImageStorage"/> when not configured.
/// </summary>
public sealed record ImageStorageCredentials(string Endpoint, string AccessKey, string SecretKey, string Bucket, string PublicBaseUrl);

public sealed record Credentials(string AdminApiKey, string? RabbitMqUrl, string? MongoDbUrl, ApnsCredentials Apns, string RootDir,
    ImageStorageCredentials? ImageStorage = null);

/// <summary>
/// Loads config/credentials.json, which is kept out of git and out of appsettings.
/// Placeholder APNs values don't stop the service from starting (device registration
/// still works); sending is refused until they are filled in, except for dry runs.
/// </summary>
public static partial class CredentialsLoader
{
    private const string RelativePath = "config/credentials.json";

    // Values in credentials.example.json contain these markers; treat them as "not configured yet".
    [GeneratedRegex("YOUR_|^REPLACE_|USER:PASSWORD")]
    private static partial Regex PlaceholderRegex();

    private static bool IsPlaceholder(string? value) => string.IsNullOrWhiteSpace(value) || PlaceholderRegex().IsMatch(value);

    /// <summary>
    /// Uses the "CredentialsPath" setting if present, otherwise looks for config/credentials.json
    /// in the current directory and its parents (so `dotnet run` works from any project folder).
    /// Relative paths inside the file (the .p8 key) resolve against the folder that holds config/.
    /// </summary>
    public static Credentials Load(IConfiguration configuration)
    {
        var path = configuration["CredentialsPath"] ?? FindUpwards(Directory.GetCurrentDirectory(), RelativePath)
            ?? throw new InvalidOperationException(
                $"{RelativePath} not found. Copy config/credentials.example.json to config/credentials.json and fill it in.");
        path = Path.GetFullPath(path);
        var rootDir = Path.GetDirectoryName(Path.GetDirectoryName(path))!;

        var file = JsonSerializer.Deserialize<CredentialsFile>(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"{path} is empty");
        var apns = file.Apns ?? new ApnsSection();

        var defaultEnvironment = apns.DefaultEnvironment ?? ApnsEnvironments.Sandbox;
        if (!ApnsEnvironments.All.Contains(defaultEnvironment))
            throw new InvalidOperationException($"apns.default_environment must be one of {string.Join(", ", ApnsEnvironments.All)}");

        var problems = new List<string>();
        if (IsPlaceholder(apns.TeamId)) problems.Add("apns.team_id is a placeholder");
        if (IsPlaceholder(apns.KeyId)) problems.Add("apns.key_id is a placeholder");

        string? privateKey = null;
        if (IsPlaceholder(apns.PrivateKeyPath))
        {
            problems.Add("apns.private_key_path is a placeholder");
        }
        else
        {
            var keyPath = Path.GetFullPath(apns.PrivateKeyPath!, rootDir);
            if (File.Exists(keyPath)) privateKey = File.ReadAllText(keyPath);
            else problems.Add($"APNs .p8 key not found at {keyPath}");
        }

        if (IsPlaceholder(file.AdminApiKey))
            throw new InvalidOperationException("admin_api_key in credentials.json is a placeholder; set it to a long random string.");

        // Without a URL the service runs HTTP-only and doesn't consume from RabbitMQ.
        var rabbitUrl = IsPlaceholder(file.RabbitMq?.Url) ? null : file.RabbitMq!.Url;
        // Without a URL sent notifications are only logged to the console and SQLite, not MongoDB.
        var mongoUrl = IsPlaceholder(file.MongoDb?.Url) ? null : file.MongoDb!.Url;

        return new Credentials(
            file.AdminApiKey!,
            rabbitUrl,
            mongoUrl,
            new ApnsCredentials(apns.TeamId, apns.KeyId, privateKey, defaultEnvironment, problems),
            rootDir,
            LoadImageStorage(file.S3));
    }

    /// <summary>
    /// The "s3" section, with S3_ENDPOINT / S3_ACCESS_KEY / S3_SECRET_KEY / S3_BUCKET / S3_PUBLIC_BASE_URL
    /// environment variables taking precedence (so a k8s Secret can supply just the keys). Null when the endpoint
    /// or either key is missing: image uploads are then refused with 503.
    /// </summary>
    private static ImageStorageCredentials? LoadImageStorage(S3Section? s3)
    {
        static string? Pick(string env, string? file) =>
            Environment.GetEnvironmentVariable(env) is { Length: > 0 } v ? v : IsPlaceholder(file) ? null : file;

        var endpoint = Pick("S3_ENDPOINT", s3?.Endpoint);
        var accessKey = Pick("S3_ACCESS_KEY", s3?.AccessKey);
        var secretKey = Pick("S3_SECRET_KEY", s3?.SecretKey);
        if (endpoint is null || accessKey is null || secretKey is null) return null;

        var bucket = Pick("S3_BUCKET", s3?.Bucket) ?? "notificationimages";
        var publicBase = Pick("S3_PUBLIC_BASE_URL", s3?.PublicBaseUrl) ?? $"https://s3.flavourslane.com/{bucket}";
        if (!Uri.TryCreate(publicBase, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("s3.public_base_url must be an absolute https URL (the app only shows https images).");
        return new ImageStorageCredentials(endpoint, accessKey, secretKey, bucket, publicBase.TrimEnd('/'));
    }

    private static string? FindUpwards(string startDir, string relativePath)
    {
        for (var dir = new DirectoryInfo(startDir); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private sealed class CredentialsFile
    {
        [JsonPropertyName("apns")] public ApnsSection? Apns { get; set; }
        [JsonPropertyName("admin_api_key")] public string? AdminApiKey { get; set; }
        [JsonPropertyName("rabbitmq")] public RabbitMqSection? RabbitMq { get; set; }
        [JsonPropertyName("mongodb")] public MongoDbSection? MongoDb { get; set; }
        [JsonPropertyName("s3")] public S3Section? S3 { get; set; }
    }

    private sealed class S3Section
    {
        [JsonPropertyName("endpoint")] public string? Endpoint { get; set; }
        [JsonPropertyName("access_key")] public string? AccessKey { get; set; }
        [JsonPropertyName("secret_key")] public string? SecretKey { get; set; }
        [JsonPropertyName("bucket")] public string? Bucket { get; set; }
        [JsonPropertyName("public_base_url")] public string? PublicBaseUrl { get; set; }
    }

    private sealed class ApnsSection
    {
        [JsonPropertyName("team_id")] public string? TeamId { get; set; }
        [JsonPropertyName("key_id")] public string? KeyId { get; set; }
        [JsonPropertyName("private_key_path")] public string? PrivateKeyPath { get; set; }
        [JsonPropertyName("default_environment")] public string? DefaultEnvironment { get; set; }
    }

    private sealed class RabbitMqSection
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
    }

    private sealed class MongoDbSection
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
    }
}
