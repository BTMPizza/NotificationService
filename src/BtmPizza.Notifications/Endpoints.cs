using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BtmPizza.Notifications.Apns;
using BtmPizza.Notifications.Configuration;
using BtmPizza.Notifications.Data;
using BtmPizza.Notifications.Images;
using BtmPizza.Notifications.Payload;
using Microsoft.AspNetCore.Http.Features;

namespace BtmPizza.Notifications;

public static partial class Endpoints
{
    private const long MaxBodyBytes = 64 * 1024;

    [GeneratedRegex("^[0-9a-f]{64,200}$")]
    private static partial Regex HexToken();

    private sealed class HttpError(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }

    /// <summary>Maps errors to the same JSON responses for every endpoint.</summary>
    public static void UseApiErrors(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next(ctx);
            }
            catch (Exception ex)
            {
                var (status, body) = ex switch
                {
                    ValidationException v => (422, (object)new { error = "validation_failed", details = v.Errors }),
                    ApnsNotConfiguredException => (503, new { error = ex.Message }),
                    ImageStorageNotConfiguredException => (503, new { error = ex.Message }),
                    HttpError h => (h.Status, new { error = h.Message }),
                    JsonException => (400, new { error = "request body must be valid JSON" }),
                    BadHttpRequestException b => (b.StatusCode, new { error = b.StatusCode == 413 ? "request body too large" : "bad request" }),
                    _ => (500, new { error = "internal error" }),
                };
                if (status == 500) app.Logger.LogError(ex, "Unhandled error");
                ctx.Response.StatusCode = status;
                await ctx.Response.WriteAsJsonAsync(body);
            }
        });
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpContext ctx)
    {
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxBodyBytes;
        if (ctx.Request.ContentLength > MaxBodyBytes) throw new HttpError(413, "request body too large");
        return await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
    }

    private static void RequireAdmin(HttpContext ctx, string adminApiKey)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        var supplied = Encoding.UTF8.GetBytes(header.StartsWith("Bearer ") ? header[7..] : "");
        if (!CryptographicOperations.FixedTimeEquals(supplied, Encoding.UTF8.GetBytes(adminApiKey)))
            throw new HttpError(401, "missing or invalid admin API key");
    }

    private static DeviceRegistration ParseDeviceRegistration(JsonNode? body, string defaultEnvironment)
    {
        var errors = new List<string>();
        var obj = body as JsonObject;
        string? Str(string key) => obj?[key]?.GetValueKind() == JsonValueKind.String ? obj[key]!.GetValue<string>() : null;

        var token = (Str("device_token") ?? "").Trim().ToLowerInvariant();
        if (!HexToken().IsMatch(token)) errors.Add("device_token must be the hex APNs device token");
        if (Str("platform") != "ios") errors.Add("platform must be \"ios\"");
        var userId = Str("user_id")?.Trim();
        if (string.IsNullOrEmpty(userId)) errors.Add("user_id is required");
        var environment = obj?["environment"] is null ? defaultEnvironment : Str("environment");
        if (environment is null || !ApnsEnvironments.All.Contains(environment))
            errors.Add($"environment must be one of {string.Join(", ", ApnsEnvironments.All)}");
        string? deviceId = null;
        if (obj?["device_id"] is not null)
        {
            deviceId = Str("device_id")?.Trim();
            if (string.IsNullOrEmpty(deviceId)) errors.Add("device_id must be a non-empty string when provided");
        }

        if (errors.Count > 0) throw new ValidationException(errors);
        return new DeviceRegistration(token, "ios", userId!, environment!, deviceId);
    }

    /// <summary>Header the gateway sets from the verified bearer token (client-supplied values are stripped there).</summary>
    public const string CustomerIdHeader = "customer-id";

    private static string CallerCustomerId(HttpContext ctx) =>
        ctx.Request.Headers[CustomerIdHeader].ToString().Trim() is { Length: > 0 } id
            ? id
            : throw new HttpError(401, "not signed in: call this through the gateway with a bearer token");

    private static string? JsonStr(JsonObject? obj, string key) =>
        obj?[key]?.GetValueKind() == JsonValueKind.String ? obj[key]!.GetValue<string>().Trim() : null;

    private static string ParseToken(JsonObject? obj, string key, List<string> errors)
    {
        var token = (JsonStr(obj, key) ?? "").ToLowerInvariant();
        if (!HexToken().IsMatch(token)) errors.Add($"{key} must be the hex APNs device token");
        return token;
    }

    /// <summary>Body of POST /api/devices/register, sent by the iOS app after sign-in.</summary>
    private static DeviceRegistration ParseAppRegistration(JsonNode? body, string callerCustomerId)
    {
        var errors = new List<string>();
        if (body is not JsonObject obj) throw new ValidationException("request body must be a JSON object");

        var customerId = JsonStr(obj, "customerId");
        if (string.IsNullOrEmpty(customerId)) errors.Add("customerId is required");
        else if (customerId != callerCustomerId) throw new HttpError(403, "customerId does not match the signed-in customer");
        var token = ParseToken(obj, "deviceToken", errors);
        if (JsonStr(obj, "platform") != "ios") errors.Add("platform must be \"ios\"");
        var environment = JsonStr(obj, "apnsEnvironment");
        if (environment is null || !ApnsEnvironments.All.Contains(environment))
            errors.Add($"apnsEnvironment must be one of {string.Join(", ", ApnsEnvironments.All)}");
        var bundleId = JsonStr(obj, "bundleId");
        if (string.IsNullOrEmpty(bundleId)) errors.Add("bundleId is required");
        var appVersion = JsonStr(obj, "appVersion");

        if (errors.Count > 0) throw new ValidationException(errors);
        return new DeviceRegistration(token, "ios", customerId!, environment!, DeviceId: null, bundleId, appVersion);
    }

    /// <summary>Header the gateway sets with the caller's AuthService role (client-supplied values are stripped there).</summary>
    public const string UserRoleHeader = "X-User-Role";

    /// <summary>
    /// Admin-app endpoints are routed by the gateway for OWNER tokens only; this re-checks the identity it forwards.
    /// </summary>
    private static void RequireOwner(HttpContext ctx)
    {
        CallerCustomerId(ctx);
        if (ctx.Request.Headers[UserRoleHeader].ToString().Trim() != "OWNER")
            throw new HttpError(403, "only BTM Pizza owners can use the admin API");
    }

    private static int QueryInt(HttpContext ctx, string name, int fallback, int min, int max)
    {
        var raw = ctx.Request.Query[name].ToString();
        if (raw.Length == 0) return fallback;
        return int.TryParse(raw, out var value) && value >= min && value <= max
            ? value
            : throw new ValidationException($"{name} must be a whole number from {min} to {max}");
    }

    public static void MapNotificationEndpoints(this WebApplication app)
    {
        // ---- BTM Pizza Admin app, through the gateway (OWNER only) ----
        app.MapPost("/api/admin/notifications/send", async (HttpContext ctx, NotificationSender sender) =>
        {
            RequireOwner(ctx);
            var result = await sender.SendAsync(await ReadJsonAsync(ctx), ctx.RequestAborted);
            app.Logger.LogInformation("Owner {CustomerId} sent notification {NotificationId} (dry run {DryRun})",
                CallerCustomerId(ctx), result.NotificationId, result.DryRun);
            return Results.Ok(result);
        });

        app.MapGet("/api/admin/notifications", (HttpContext ctx, DeviceStore store) =>
        {
            RequireOwner(ctx);
            var page = QueryInt(ctx, "page", 1, 1, 100_000);
            var pageSize = QueryInt(ctx, "page_size", 20, 1, 100);
            var userId = ctx.Request.Query["user_id"].ToString().Trim() is { Length: > 0 } u ? u : null;
            var (items, total) = store.ListSendLog(page, pageSize, userId);
            return Results.Ok(new
            {
                items = items.Select(i => new
                {
                    notification_id = i.NotificationId, type = i.Type, title = i.Title, body = i.Body, image_url = i.ImageUrl,
                    target = i.TargetAll is null ? null : new { all = i.TargetAll.Value, user_ids = i.TargetUserIds ?? [] },
                    target_count = i.TargetCount, success_count = i.SuccessCount, failure_count = i.FailureCount, sent_at = i.SentAt,
                }),
                page, page_size = pageSize, total_count = total, has_more = (long)page * pageSize < total,
            });
        });

        // The image goes in the raw body (Content-Type image/jpeg, image/png or image/gif; checked by its bytes), at
        // most 5 MB. It's stored in the public notification-images bucket and its https URL is returned for image_url.
        app.MapPost("/api/admin/notifications/images", async (HttpContext ctx, IImageStore images) =>
        {
            RequireOwner(ctx);
            if (!images.IsConfigured) throw new ImageStorageNotConfiguredException();
            if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = ImageFormat.MaxBytes;
            if (ctx.Request.ContentLength > ImageFormat.MaxBytes) throw new HttpError(413, "image too large: the limit is 5 MB");

            using var buffer = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(buffer, ctx.RequestAborted);
            var data = buffer.ToArray();
            if (data.Length == 0) throw new ValidationException("send the image file as the request body");
            if (data.Length > ImageFormat.MaxBytes) throw new HttpError(413, "image too large: the limit is 5 MB");
            var format = ImageFormat.Detect(data) ?? throw new ValidationException("the file must be a JPEG, PNG or GIF image");

            var key = $"admin/{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid():N}.{format.Extension}";
            var url = await images.PutAsync(key, data, format.ContentType, ctx.RequestAborted);
            app.Logger.LogInformation("Owner {CustomerId} uploaded notification image {Key} ({Bytes} bytes)", CallerCustomerId(ctx), key, data.Length);
            return Results.Ok(new { image_url = url, key, size = data.Length, content_type = format.ContentType });
        });

        app.MapGet("/api/admin/notifications/audience", (HttpContext ctx, DeviceStore store) =>
        {
            RequireOwner(ctx);
            var (customers, devices) = store.CountAudience();
            return Results.Ok(new { customers, devices });
        });

        app.MapGet("/health", (IApnsClient apns) => Results.Ok(new { ok = true, apns_configured = apns.IsConfigured }));

        app.MapGet("/admin", (IWebHostEnvironment env) =>
            Results.File(Path.Combine(env.WebRootPath, "admin.html"), "text/html; charset=utf-8"));

        app.MapGet("/admin/samples", () => Results.Text(Samples.All.ToJsonString(PayloadJson.Compact), "application/json; charset=utf-8"));

        // Called by the app to register (or refresh) its device token.
        app.MapPost("/devices", async (HttpContext ctx, Credentials credentials, DeviceStore store) =>
        {
            var device = ParseDeviceRegistration(await ReadJsonAsync(ctx), credentials.Apns.DefaultEnvironment);
            var row = store.Upsert(device);
            return Results.Ok(new { device_token = row.DeviceToken, user_id = row.UserId, environment = row.Environment, updated_at = row.UpdatedAt });
        });

        // Called by the app through the gateway (protected routes), which sets customer-id from the bearer token.
        app.MapPost("/api/devices/register", async (HttpContext ctx, DeviceStore store) =>
        {
            var customerId = CallerCustomerId(ctx);
            var device = ParseAppRegistration(await ReadJsonAsync(ctx), customerId);
            var row = store.Upsert(device);
            app.Logger.LogInformation("Registered {Environment} device ...{TokenTail} for customer {CustomerId} ({BundleId} {AppVersion})",
                row.Environment, row.DeviceToken[^8..], customerId, device.BundleId, device.AppVersion);
            return Results.Ok(new { customer_id = row.UserId, environment = row.Environment, updated_at = row.UpdatedAt });
        });

        // Logout. Only removes the token if it belongs to the caller; unknown tokens are not an error.
        app.MapPost("/api/devices/unregister", async (HttpContext ctx, DeviceStore store) =>
        {
            var customerId = CallerCustomerId(ctx);
            var errors = new List<string>();
            var token = ParseToken(await ReadJsonAsync(ctx) as JsonObject, "deviceToken", errors);
            if (errors.Count > 0) throw new ValidationException(errors);
            var removed = store.DeleteToken(token, customerId);
            app.Logger.LogInformation("Unregistered device ...{TokenTail} for customer {CustomerId} (removed={Removed})",
                token[^8..], customerId, removed);
            return Results.Ok(new { removed });
        });

        // AuthService removes a customer who deleted their account. In-cluster only: the gateway never routes it.
        // Safe to repeat, because AuthService retries until this succeeds.
        app.MapPost("/api/internal/customers/{customerId}/delete", (string customerId, HttpContext ctx, IConfiguration config, DeviceStore store) =>
        {
            var expected = config["AccountDeletion:ServiceKey"] ?? "";
            var presented = ctx.Request.Headers["X-Service-Key"].ToString();
            if (expected.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected)))
                throw new HttpError(401, "missing or invalid service key");
            var devices = store.DeleteCustomer(customerId);
            app.Logger.LogInformation("Deleted notification data for customer {CustomerId} ({Devices} device(s))", customerId, devices);
            return Results.NoContent();
        });

        app.MapPost("/notifications/send", async (HttpContext ctx, Credentials credentials, NotificationSender sender) =>
        {
            RequireAdmin(ctx, credentials.AdminApiKey);
            var result = await sender.SendAsync(await ReadJsonAsync(ctx), ctx.RequestAborted);
            return Results.Ok(result);
        });
    }
}
