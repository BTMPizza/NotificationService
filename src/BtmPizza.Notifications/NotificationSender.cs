using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using BtmPizza.Notifications.Apns;
using BtmPizza.Notifications.Data;
using BtmPizza.Notifications.Payload;

namespace BtmPizza.Notifications;

public sealed class ApnsNotConfiguredException() : Exception(
    "APNs credentials are not configured yet (see config/credentials.json). Use \"dry_run\": true to preview.");

public sealed record SendResult(
    string NotificationId,
    bool DryRun,
    string Type,
    int TargetCount,
    int SuccessCount,
    int FailureCount,
    int RemovedTokens,
    IReadOnlyDictionary<string, int> FailureReasons,
    JsonObject Payload,
    IReadOnlyList<string> Warnings);

/// <summary>Validates a send request, builds the payload and delivers it to every targeted device.</summary>
public sealed class NotificationSender(
    DeviceStore store,
    IApnsClient apns,
    IImageUrlVerifier imageVerifier,
    ILogger<NotificationSender> logger)
{
    private const int Concurrency = 100;

    // 410 means the token is no longer valid for this app (Unregistered / ExpiredToken).
    private static bool IsDeadToken(ApnsResult r) => r.Status == 410 || (r.Status == 400 && r.Reason == "BadDeviceToken");

    public async Task<SendResult> SendAsync(JsonNode? input, CancellationToken ct = default)
    {
        var req = SendRequestValidator.Validate(input);
        var warnings = req.ImageUrl is null ? [] : await imageVerifier.VerifyAsync(req.ImageUrl, ct);
        var notificationId = Guid.NewGuid().ToString();
        var payload = PayloadBuilder.Build(req, notificationId);
        var devices = store.FindTargets(req.Target);

        logger.LogDebug(
            "APNs payload for {NotificationId} ({Bytes} bytes, matches iOS contract, {TargetCount} target device(s)):\n{Payload}",
            notificationId, PayloadJson.ByteSize(payload), devices.Count, payload.ToJsonString(PayloadJson.Indented));

        if (req.DryRun)
            return new SendResult(notificationId, true, req.Type, devices.Count, 0, 0, 0, new Dictionary<string, int>(), payload, warnings);
        if (!apns.IsConfigured) throw new ApnsNotConfiguredException();

        var success = 0;
        var failure = 0;
        var removed = 0;
        var failureReasons = new ConcurrentDictionary<string, int>();
        var body = PayloadJson.Serialize(payload);

        await Parallel.ForEachAsync(devices, new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = ct },
            async (device, token) =>
            {
                var result = await apns.SendAsync(device.Environment, device.DeviceToken, body, notificationId, token);
                if (result.Ok)
                {
                    Interlocked.Increment(ref success);
                    return;
                }
                Interlocked.Increment(ref failure);
                failureReasons.AddOrUpdate($"{result.Status} {result.Reason}", 1, (_, n) => n + 1);
                if (IsDeadToken(result)) Interlocked.Add(ref removed, store.DeleteToken(device.DeviceToken));
            });

        logger.LogInformation(
            "Notification sent {Summary}",
            JsonSerializer.Serialize(new
            {
                notification_id = notificationId,
                type = req.Type,
                target_count = devices.Count,
                success_count = success,
                failure_count = failure,
                removed_tokens = removed,
                failure_reasons = failureReasons,
            }));
        store.InsertSendLog(new SendLogEntry(notificationId, req.Type, devices.Count, success, failure, removed, body));

        return new SendResult(notificationId, false, req.Type, devices.Count, success, failure, removed,
            new Dictionary<string, int>(failureReasons), payload, warnings);
    }
}
