using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace BtmPizza.Notifications.Tests;

public sealed record ReceivedPush(string Host, IHeaderDictionary Headers, JsonObject Body, string DeviceToken);

/// <summary>
/// Runs the real API against two fake APNs servers (sandbox and production) speaking HTTP/2.
/// The outcome of each push is chosen by the device token's first byte.
/// </summary>
public sealed class ServiceFixture : IAsyncLifetime
{
    public const string AdminKey = "test-admin-key";
    public readonly ECDsa SigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public readonly ConcurrentQueue<ReceivedPush> Received = new();
    public string DbPath => Path.Combine(_dir, "test.db");
    public HttpClient Client { get; private set; } = null!;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "btm-notifications-tests-" + Guid.NewGuid());
    private WebApplication _sandbox = null!;
    private WebApplication _production = null!;
    private WebApplicationFactory<Program> _factory = null!;

    private async Task<WebApplication> StartFakeApns(string name)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http2));
        var app = builder.Build();
        app.Run(async ctx =>
        {
            var token = ctx.Request.Path.Value!.Split('/').Last();
            var body = (JsonObject)(await JsonNode.ParseAsync(ctx.Request.Body))!;
            Received.Enqueue(new ReceivedPush(name, ctx.Request.Headers, body, token));
            var (status, reason) = token[..2] switch
            {
                "bb" => (410, "Unregistered"),
                "cc" => (400, "BadDeviceToken"),
                "dd" => (400, "TopicDisallowed"),
                _ => (200, null),
            };
            ctx.Response.StatusCode = status;
            if (reason is not null) await ctx.Response.WriteAsJsonAsync(new { reason });
        });
        await app.StartAsync();
        return app;
    }

    public async Task InitializeAsync()
    {
        _sandbox = await StartFakeApns("sandbox");
        _production = await StartFakeApns("production");

        Directory.CreateDirectory(Path.Combine(_dir, "config"));
        File.WriteAllText(Path.Combine(_dir, "config", "AuthKey_TEST.p8"), SigningKey.ExportPkcs8PrivateKeyPem());
        var credentialsPath = Path.Combine(_dir, "config", "credentials.json");
        File.WriteAllText(credentialsPath, JsonSerializer.Serialize(new
        {
            apns = new { team_id = "TEAM123456", key_id = "KEY1234567", private_key_path = "config/AuthKey_TEST.p8", default_environment = "sandbox" },
            admin_api_key = AdminKey,
        }));

        // Added after appsettings.json so these win (UseSetting would be overridden by it).
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureAppConfiguration(c =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CredentialsPath"] = credentialsPath,
                ["DbPath"] = DbPath,
                ["Apns:SandboxUrl"] = _sandbox.Urls.First(),
                ["Apns:ProductionUrl"] = _production.Urls.First(),
            })));
        Client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _sandbox.DisposeAsync();
        await _production.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}

public sealed class ServiceTests(ServiceFixture fx) : IClassFixture<ServiceFixture>
{
    private static string Token(string prefix) => string.Concat(Enumerable.Repeat(prefix, 32)); // 64 hex chars

    private async Task<(HttpStatusCode Status, JsonNode? Json)> Post(string path, object body, string? adminKey = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (adminKey is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);
        var res = await fx.Client.SendAsync(req);
        return (res.StatusCode, JsonNode.Parse(await res.Content.ReadAsStringAsync()));
    }

    private Task<(HttpStatusCode, JsonNode?)> Register(string token, string userId, string? deviceId = null, string? environment = null) =>
        Post("/devices", new Dictionary<string, string?>
        {
            ["device_token"] = token, ["platform"] = "ios", ["user_id"] = userId,
            ["device_id"] = deviceId, ["environment"] = environment,
        }.Where(kv => kv.Value is not null).ToDictionary());

    private Task<(HttpStatusCode, JsonNode?)> Send(object body) => Post("/notifications/send", body, ServiceFixture.AdminKey);

    private object? Scalar(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={fx.DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    [Fact]
    public async Task Device_registration_validates_and_stores_one_token_per_device()
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Register("not-hex", "r1")).Item1);

        var (status, json) = await Register(Token("a1"), "r1", deviceId: "phone-1");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("sandbox", json!["environment"]!.GetValue<string>());

        // Same device, new token: the old token is replaced, not duplicated.
        await Register(Token("a2"), "r1", deviceId: "phone-1");
        Assert.Equal(Token("a2"), Scalar("SELECT device_token FROM devices WHERE device_id = 'phone-1'"));
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM devices WHERE user_id = 'r1'"));

        // Re-registering the same token (no device_id) just updates its user.
        await Register(Token("a3"), "r2");
        await Register(Token("a3").ToUpperInvariant(), "r3");
        Assert.Equal("r3", Scalar($"SELECT user_id FROM devices WHERE device_token = '{Token("a3")}'"));
    }

    [Fact]
    public async Task Send_requires_the_admin_API_key()
    {
        var (status, _) = await Post("/notifications/send", new { }, "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task Send_returns_422_with_every_validation_error()
    {
        var (status, json) = await Send(new { type = "offer", title = new string('x', 60), target = new { } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.True(json!["details"]!.AsArray().Count >= 3);
    }

    [Fact]
    public async Task Dry_run_returns_the_payload_without_contacting_APNs()
    {
        await Register(Token("e1"), "dry-user");
        var before = fx.Received.Count;
        var (status, json) = await Send(new { type = "OFFER", title = "Hi", body = "There", target = new { user_ids = new[] { "dry-user" } }, dry_run = true });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(json!["dry_run"]!.GetValue<bool>());
        Assert.Equal(1, json["target_count"]!.GetValue<int>());
        Assert.Equal("notification_chime.caf", json["payload"]!["aps"]!["sound"]!.GetValue<string>());
        Assert.Equal(before, fx.Received.Count);
    }

    [Fact]
    public async Task Sends_over_HTTP2_with_token_auth_removes_dead_tokens_and_logs_counts()
    {
        await Register(Token("bb"), "u9");
        await Register(Token("cc"), "u9");
        await Register(Token("dd"), "u9");
        await Register(Token("ab"), "u9", environment: "production");

        var (status, json) = await Send(new
        {
            type = "WALLET_UPDATE",
            title = "You earned 120 Dough Coins",
            body = "Balance is now 860.",
            target = new { user_ids = new[] { "u9", "nobody" } },
        });
        Assert.Equal(HttpStatusCode.OK, status);
        var notificationId = json!["notification_id"]!.GetValue<string>();
        Assert.Equal(4, json["target_count"]!.GetValue<int>());
        Assert.Equal(1, json["success_count"]!.GetValue<int>());
        Assert.Equal(3, json["failure_count"]!.GetValue<int>());
        Assert.Equal(2, json["removed_tokens"]!.GetValue<int>());

        var received = fx.Received.Where(r => r.Body["notification_id"]!.GetValue<string>() == notificationId).ToList();
        Assert.Equal(4, received.Count);
        // Routed by each device's environment.
        Assert.Equal("production", received.Single(r => r.DeviceToken == Token("ab")).Host);
        Assert.Equal(3, received.Count(r => r.Host == "sandbox"));

        foreach (var r in received)
        {
            Assert.Equal("alert", r.Headers["apns-push-type"]);
            Assert.Equal("com.nayakkarthik.BTMPizza", r.Headers["apns-topic"]);
            Assert.Equal(notificationId, r.Headers["apns-id"]);

            var jwt = r.Headers.Authorization.ToString()["bearer ".Length..].Split('.');
            var header = JsonNode.Parse(Base64UrlDecode(jwt[0]))!;
            Assert.Equal("ES256", header["alg"]!.GetValue<string>());
            Assert.Equal("KEY1234567", header["kid"]!.GetValue<string>());
            Assert.Equal("TEAM123456", JsonNode.Parse(Base64UrlDecode(jwt[1]))!["iss"]!.GetValue<string>());
            Assert.True(fx.SigningKey.VerifyData(Encoding.ASCII.GetBytes($"{jwt[0]}.{jwt[1]}"), Base64UrlDecode(jwt[2]), HashAlgorithmName.SHA256));

            Assert.Empty(IosContractProblems(r.Body));
            Assert.Equal("WALLET_UPDATE", r.Body["aps"]!["thread-id"]!.GetValue<string>());
        }

        // 410 and 400 BadDeviceToken are deleted; other 400s are kept.
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM devices WHERE user_id = 'u9'"));
        Assert.Equal(0L, Scalar($"SELECT COUNT(*) FROM devices WHERE device_token IN ('{Token("bb")}', '{Token("cc")}')"));

        Assert.Equal("WALLET_UPDATE", Scalar($"SELECT type FROM notification_log WHERE notification_id = '{notificationId}'"));
        Assert.Equal(1L, Scalar($"SELECT success_count FROM notification_log WHERE notification_id = '{notificationId}'"));
        Assert.Equal(3L, Scalar($"SELECT failure_count FROM notification_log WHERE notification_id = '{notificationId}'"));
    }

    private static List<string> IosContractProblems(JsonObject body) => Payload.IosContract.Check(body);

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
