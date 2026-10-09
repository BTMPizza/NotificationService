using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BtmPizza.Notifications.Configuration;

namespace BtmPizza.Notifications.Apns;

public sealed record ApnsResult(bool Ok, int Status, string? Reason);

public interface IApnsClient
{
    bool IsConfigured { get; }
    Task<ApnsResult> SendAsync(string environment, string deviceToken, string payloadJson, string notificationId, CancellationToken ct = default);
}

public sealed record ApnsHosts(string SandboxUrl, string ProductionUrl)
{
    public string For(string environment) => environment switch
    {
        ApnsEnvironments.Sandbox => SandboxUrl,
        ApnsEnvironments.Production => ProductionUrl,
        _ => throw new ArgumentException($"Unknown APNs environment \"{environment}\""),
    };
}

/// <summary>
/// Minimal APNs provider: HTTP/2, token-based (.p8) auth. HttpClient keeps one
/// long-lived HTTP/2 connection per host (sandbox / production) and multiplexes requests on it.
/// </summary>
public sealed class ApnsClient : IApnsClient, IDisposable
{
    public const string BundleId = "com.nayakkarthik.BTMPizza";

    // APNs rejects provider tokens older than an hour and throttles refreshes more
    // frequent than every 20 minutes, so reuse each token for 50 minutes.
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(50);
    private static readonly HashSet<int> RetryableStatuses = [429, 500, 503];

    private readonly ApnsCredentials _credentials;
    private readonly ApnsHosts _hosts;
    private readonly HttpClient _http;
    private readonly TimeSpan _retryDelay;
    private readonly ECDsa? _signingKey;
    private readonly Lock _tokenLock = new();
    private string? _token;
    private DateTimeOffset _tokenIssuedAt;

    public ApnsClient(ApnsCredentials credentials, ApnsHosts hosts, TimeSpan? retryDelay = null)
    {
        _credentials = credentials;
        _hosts = hosts;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
        _http = new HttpClient(new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(30),
        })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        if (credentials.Configured)
        {
            _signingKey = ECDsa.Create();
            _signingKey.ImportFromPem(credentials.PrivateKeyPem);
        }
    }

    public bool IsConfigured => _credentials.Configured;

    public string ProviderToken(bool forceRefresh = false)
    {
        lock (_tokenLock)
        {
            if (!forceRefresh && _token is not null && DateTimeOffset.UtcNow - _tokenIssuedAt < TokenTtl) return _token;
            if (_signingKey is null) throw new InvalidOperationException("APNs credentials are not configured");

            var issuedAt = DateTimeOffset.UtcNow;
            var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = _credentials.KeyId }));
            var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = _credentials.TeamId, iat = issuedAt.ToUnixTimeSeconds() }));
            // ECDsa.SignData produces the raw r||s (IEEE P1363) signature that JWT ES256 requires.
            var signature = Base64Url(_signingKey.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256));
            _token = $"{header}.{claims}.{signature}";
            _tokenIssuedAt = issuedAt;
            return _token;
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Sends one notification. Never throws for delivery problems; network failures come back as status 0.
    /// </summary>
    public async Task<ApnsResult> SendAsync(string environment, string deviceToken, string payloadJson, string notificationId, CancellationToken ct = default)
    {
        ApnsResult result = new(false, 0, "NotSent");
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            result = await RequestAsync(environment, deviceToken, payloadJson, notificationId, ct);
            if (result.Ok) return result;
            if (result is { Status: 403, Reason: "ExpiredProviderToken" })
            {
                ProviderToken(forceRefresh: true);
                continue;
            }
            if (result.Status == 0 || RetryableStatuses.Contains(result.Status))
            {
                await Task.Delay(_retryDelay * attempt, ct);
                continue;
            }
            return result;
        }
        return result;
    }

    private async Task<ApnsResult> RequestAsync(string environment, string deviceToken, string payloadJson, string notificationId, CancellationToken ct)
    {
        // APNs only speaks HTTP/2. A hand-built HttpRequestMessage defaults to HTTP/1.1
        // (HttpClient.DefaultRequestVersion only applies to GetAsync/PostAsync), so set it here.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_hosts.For(environment)}/3/device/{deviceToken}")
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json"),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("bearer", ProviderToken());
        request.Headers.Add("apns-push-type", "alert");
        request.Headers.Add("apns-topic", BundleId);
        request.Headers.Add("apns-priority", "10");
        request.Headers.Add("apns-id", notificationId);

        try
        {
            using var response = await _http.SendAsync(request, ct);
            var status = (int)response.StatusCode;
            if (status == 200) return new ApnsResult(true, status, null);

            var reason = "Unknown";
            try
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("reason", out var r)) reason = r.GetString() ?? reason;
            }
            catch (JsonException)
            {
                // APNs always sends a JSON body on errors; keep "Unknown" if it didn't.
            }
            return new ApnsResult(false, status, reason);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new ApnsResult(false, 0, ex is TaskCanceledException ? "Timeout" : ex.Message);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _signingKey?.Dispose();
    }
}
