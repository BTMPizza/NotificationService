using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BtmPizza.Notifications.Payload;

public static class NotificationTypes
{
    public static readonly string[] All = ["OFFER", "WALLET_UPDATE", "PIZZA_NEWS"];
}

public sealed class ValidationException(IReadOnlyList<string> errors) : Exception(string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
    public ValidationException(string error) : this([error]) { }
}

public sealed record Target(bool All, IReadOnlyList<string> UserIds);

public sealed record SendRequest(
    string Type,
    string Title,
    string Body,
    string? Kicker,
    IReadOnlyList<string>? Article,
    string? ImageUrl,
    Target Target,
    bool DryRun);

public static class PayloadJson
{
    // Keep "₹" and "·" as UTF-8 rather than \uXXXX escapes: the 4 KB limit counts bytes.
    public static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions Indented = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    public static string Serialize(JsonNode node) => node.ToJsonString(Compact);
    public static int ByteSize(JsonNode node) => Encoding.UTF8.GetByteCount(Serialize(node));
}

public static partial class SendRequestValidator
{
    public const int TitleMax = 50;
    public const int BodyMax = 150;
    public const int KickerMax = 25;
    private const int MaxUserIds = 10_000;
    private static readonly HashSet<string> AllowedFields =
        ["type", "title", "body", "kicker", "article", "image_url", "target", "dry_run"];

    // The app loads the string as-is, so spaces and other unsafe characters must already be percent-encoded.
    [GeneratedRegex("""[^\x21-\x7e]|["<>\\^`{|}]""")]
    private static partial Regex UnsafeUrlChars();

    /// <summary>Counts user-visible characters, not UTF-16 code units ("₹" or an emoji is one).</summary>
    public static int CharLength(string s) => new StringInfo(s).LengthInTextElements;

    internal static bool IsNonEmptyString(JsonNode? node, out string value)
    {
        value = node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool IsMissing(JsonObject obj, string field) => obj[field] is null;

    private static string? CheckText(List<string> errors, JsonObject obj, string field, bool required, int max)
    {
        if (IsMissing(obj, field))
        {
            if (required) errors.Add($"{field} is required");
            return null;
        }
        if (!IsNonEmptyString(obj[field], out var value))
        {
            errors.Add($"{field} must be a non-empty string");
            return null;
        }
        var len = CharLength(value);
        if (len > max) errors.Add($"{field} must be at most {max} characters (got {len})");
        return value;
    }

    private static void CheckImageUrlSyntax(List<string> errors, string value)
    {
        if (UnsafeUrlChars().IsMatch(value))
            errors.Add("image_url contains spaces or unencoded special characters; percent-encode them (e.g. %20)");
        // On macOS/Linux "/banner.jpg" parses as an absolute file:// URI, so reject implicit file paths too.
        else if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.IsFile && !value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)))
            errors.Add("image_url must be an absolute URL");
        else if (uri.Scheme != Uri.UriSchemeHttps)
            errors.Add("image_url must use https");
    }

    /// <summary>Synchronous checks on the send request. Throws ValidationException listing every problem.</summary>
    public static SendRequest Validate(JsonNode? input)
    {
        if (input is not JsonObject obj) throw new ValidationException("request body must be a JSON object");
        var errors = new List<string>();

        foreach (var (key, _) in obj)
            if (!AllowedFields.Contains(key)) errors.Add($"unknown field \"{key}\"");

        var type = obj["type"]?.GetValueKind() == JsonValueKind.String ? obj["type"]!.GetValue<string>() : null;
        if (type is null || !NotificationTypes.All.Contains(type))
            errors.Add($"type must be exactly one of {string.Join(", ", NotificationTypes.All)} (uppercase)");

        var title = CheckText(errors, obj, "title", required: true, TitleMax);
        var body = CheckText(errors, obj, "body", required: true, BodyMax);
        var kicker = CheckText(errors, obj, "kicker", required: false, KickerMax);

        List<string>? article = null;
        if (!IsMissing(obj, "article"))
        {
            if (obj["article"] is not JsonArray arr || arr.Count == 0)
                errors.Add("article must be a non-empty array of paragraph strings");
            else if (!arr.All(p => IsNonEmptyString(p, out _)))
                errors.Add("every article paragraph must be a non-empty string");
            else
                article = arr.Select(p => p!.GetValue<string>()).ToList();
        }

        string? imageUrl = null;
        if (!IsMissing(obj, "image_url"))
        {
            if (!IsNonEmptyString(obj["image_url"], out var url)) errors.Add("image_url must be a non-empty string");
            else
            {
                CheckImageUrlSyntax(errors, url);
                imageUrl = url;
            }
        }

        Target? target = null;
        if (obj["target"] is not JsonObject t)
        {
            errors.Add("target is required: { \"user_ids\": [...] } or { \"all\": true }");
        }
        else
        {
            var keys = t.Select(kv => kv.Key).ToList();
            if (keys is ["all"])
            {
                if (t["all"]?.GetValueKind() != JsonValueKind.True) errors.Add("target.all must be true");
                else target = new Target(true, []);
            }
            else if (keys is ["user_ids"])
            {
                if (t["user_ids"] is not JsonArray ids || ids.Count == 0 || !ids.All(id => IsNonEmptyString(id, out _)))
                    errors.Add("target.user_ids must be a non-empty array of user id strings");
                else if (ids.Count > MaxUserIds)
                    errors.Add($"target.user_ids can list at most {MaxUserIds} users; use {{ \"all\": true }} for broadcasts");
                else
                    target = new Target(false, ids.Select(id => id!.GetValue<string>()).Distinct().ToList());
            }
            else
            {
                errors.Add("target must be exactly { \"user_ids\": [...] } or { \"all\": true }");
            }
        }

        var dryRun = false;
        if (!IsMissing(obj, "dry_run"))
        {
            var kind = obj["dry_run"]!.GetValueKind();
            if (kind is JsonValueKind.True or JsonValueKind.False) dryRun = kind == JsonValueKind.True;
            else errors.Add("dry_run must be a boolean");
        }

        if (errors.Count > 0) throw new ValidationException(errors);
        return new SendRequest(type!, title!, body!, kicker, article, imageUrl, target!, dryRun);
    }
}

public static class PayloadBuilder
{
    public const string Sound = "notification_chime.caf";
    public const int MaxPayloadBytes = 4096;

    /// <summary>Builds the APNs payload in the exact shape (and key order) the app expects.</summary>
    public static JsonObject Build(SendRequest req, string notificationId)
    {
        var payload = new JsonObject
        {
            ["aps"] = new JsonObject
            {
                ["alert"] = new JsonObject { ["title"] = req.Title, ["body"] = req.Body },
                ["sound"] = Sound,
                ["thread-id"] = req.Type,
            },
            ["type"] = req.Type,
            ["notification_id"] = notificationId,
        };
        if (req.Kicker is not null) payload["kicker"] = req.Kicker;
        if (req.ImageUrl is not null) payload["image_url"] = req.ImageUrl;
        if (req.Article is not null) payload["article"] = new JsonArray(req.Article.Select(p => (JsonNode)p!).ToArray());

        var size = PayloadJson.ByteSize(payload);
        if (size > MaxPayloadBytes)
            throw new ValidationException($"payload is {size} bytes, over the APNs limit of {MaxPayloadBytes}; shorten the article");

        var problems = IosContract.Check(payload);
        if (problems.Count > 0)
            throw new InvalidOperationException($"built payload does not match the iOS contract: {string.Join("; ", problems)}");
        return payload;
    }
}

/// <summary>
/// Checks a finished payload against what the iOS app expects, independently of how it was built.
/// An empty list means the payload is valid.
/// </summary>
public static partial class IosContract
{
    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex Uuid();

    private static bool SameKeys(JsonObject obj, string[] required, string[]? optional = null) =>
        required.All(obj.ContainsKey) && obj.All(kv => required.Contains(kv.Key) || (optional?.Contains(kv.Key) ?? false));

    private static string? Str(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    private static bool TextOk(JsonNode? node, int max) =>
        SendRequestValidator.IsNonEmptyString(node, out var s) && SendRequestValidator.CharLength(s) <= max;

    public static List<string> Check(JsonObject payload)
    {
        var problems = new List<string>();
        if (!SameKeys(payload, ["aps", "type", "notification_id"], ["kicker", "image_url", "article"]))
            problems.Add($"top-level keys must be aps, type, notification_id [+ kicker, image_url, article]; got {string.Join(",", payload.Select(kv => kv.Key))}");

        var aps = payload["aps"] as JsonObject;
        if (aps is null || !SameKeys(aps, ["alert", "sound", "thread-id"]))
            problems.Add("aps must contain exactly alert, sound, thread-id (no category)");
        if (aps is not null)
        {
            var alert = aps["alert"] as JsonObject;
            if (alert is null || !SameKeys(alert, ["title", "body"])) problems.Add("aps.alert must contain exactly title and body");
            if (Str(aps["sound"]) != PayloadBuilder.Sound) problems.Add($"aps.sound must be \"{PayloadBuilder.Sound}\"");
            if (Str(aps["thread-id"]) != Str(payload["type"])) problems.Add("aps.thread-id must equal type");
            if (!TextOk(alert?["title"], SendRequestValidator.TitleMax)) problems.Add("aps.alert.title must be 1-50 characters");
            if (!TextOk(alert?["body"], SendRequestValidator.BodyMax)) problems.Add("aps.alert.body must be 1-150 characters");
        }

        if (!NotificationTypes.All.Contains(Str(payload["type"]))) problems.Add($"type must be one of {string.Join(", ", NotificationTypes.All)}");
        if (!Uuid().IsMatch(Str(payload["notification_id"]) ?? "")) problems.Add("notification_id must be a lowercase UUID");
        if (payload.ContainsKey("kicker") && !TextOk(payload["kicker"], SendRequestValidator.KickerMax))
            problems.Add("kicker must be 1-25 characters");
        if (payload.ContainsKey("article") &&
            (payload["article"] is not JsonArray article || !article.All(p => SendRequestValidator.IsNonEmptyString(p, out _))))
            problems.Add("article must be an array of non-empty strings");
        if (payload.ContainsKey("image_url") && !(Str(payload["image_url"])?.StartsWith("https://") ?? false))
            problems.Add("image_url must be https");

        var size = PayloadJson.ByteSize(payload);
        if (size > PayloadBuilder.MaxPayloadBytes) problems.Add($"payload is {size} bytes, over {PayloadBuilder.MaxPayloadBytes}");
        return problems;
    }
}
