using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BtmPizza.Notifications.Apns;
using BtmPizza.Notifications.Configuration;
using BtmPizza.Notifications.Data;
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

    public static void MapNotificationEndpoints(this WebApplication app)
    {
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

        app.MapPost("/notifications/send", async (HttpContext ctx, Credentials credentials, NotificationSender sender) =>
        {
            RequireAdmin(ctx, credentials.AdminApiKey);
            var result = await sender.SendAsync(await ReadJsonAsync(ctx), ctx.RequestAborted);
            return Results.Ok(result);
        });
    }
}
