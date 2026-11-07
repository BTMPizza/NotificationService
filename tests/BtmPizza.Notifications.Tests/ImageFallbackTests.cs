using System.Text.Json.Nodes;
using BtmPizza.Notifications.Apns;
using BtmPizza.Notifications.Data;
using BtmPizza.Notifications.Payload;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BtmPizza.Notifications.Tests;

/// <summary>Automatic pushes survive a bad image; sends made by hand still report it.</summary>
public sealed class ImageFallbackTests : IDisposable
{
    private const string Url = "https://s3.test/notificationimages/a.jpg";
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"btm-fallback-{Guid.NewGuid()}.db");

    private sealed class Verifier(bool ok) : IImageUrlVerifier
    {
        public Task<IReadOnlyList<string>> VerifyAsync(string imageUrl, CancellationToken ct = default) =>
            ok ? Task.FromResult<IReadOnlyList<string>>([]) : throw new ValidationException("image_url HEAD request returned 404, expected 200");
    }

    private sealed class NoApns : IApnsClient
    {
        public bool IsConfigured => true;

        public Task<ApnsResult> SendAsync(string environment, string deviceToken, string payload, string notificationId, CancellationToken ct = default) =>
            throw new InvalidOperationException("dry runs never reach APNs");
    }

    private sealed class NoLog : INotificationLog
    {
        public Task WriteAsync(DateTime sentAtUtc, JsonObject payload, CancellationToken ct = default) => Task.CompletedTask;
    }

    private NotificationSender Sender(bool imageWorks) => new(
        new DeviceStore(_db), new NoApns(), new Verifier(imageWorks), new NoLog(),
        new ConfigurationBuilder().Build(), NullLogger<NotificationSender>.Instance);

    private static JsonObject Message(string? image = Url)
    {
        var message = new JsonObject
        {
            ["type"] = "COINS_EARNED",
            ["title"] = "+10 coins earned",
            ["body"] = "Bill 1 added 10 coins.",
            ["target"] = new JsonObject { ["user_ids"] = new JsonArray("u1") },
            ["dry_run"] = true,
        };
        if (image is not null) message["image_url"] = image;
        return message;
    }

    [Fact]
    public async Task A_good_image_is_sent()
    {
        var result = await Sender(imageWorks: true).SendAsync(Message(), imageOptional: true);
        Assert.Equal(Url, result.Payload["image_url"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_automatic_push_with_a_dead_image_goes_out_as_text()
    {
        var result = await Sender(imageWorks: false).SendAsync(Message(), imageOptional: true);
        Assert.False(result.Payload.ContainsKey("image_url"));
        Assert.Equal("+10 coins earned", result.Payload["aps"]!["alert"]!["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_automatic_push_with_a_malformed_image_goes_out_as_text()
    {
        var result = await Sender(imageWorks: true).SendAsync(Message("http://not-https.example/a.jpg"), imageOptional: true);
        Assert.False(result.Payload.ContainsKey("image_url"));
    }

    [Fact]
    public async Task Other_problems_are_still_rejected_on_automatic_pushes()
    {
        var message = Message("http://not-https.example/a.jpg");
        message.Remove("title");
        await Assert.ThrowsAsync<ValidationException>(() => Sender(imageWorks: true).SendAsync(message, imageOptional: true));
    }

    [Fact]
    public async Task A_send_by_hand_still_reports_a_dead_image() =>
        await Assert.ThrowsAsync<ValidationException>(() => Sender(imageWorks: false).SendAsync(Message()));

    public void Dispose() => File.Delete(_db);
}
