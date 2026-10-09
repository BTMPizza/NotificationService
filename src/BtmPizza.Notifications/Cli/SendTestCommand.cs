using System.Text.Json;
using System.Text.Json.Nodes;
using BtmPizza.Notifications.Payload;

namespace BtmPizza.Notifications.Cli;

/// <summary>
/// Sends a test notification of one type (or all three) using the sample content.
///
///   dotnet run --project src/BtmPizza.Notifications -- send-test --type OFFER --user user-123
///   dotnet run --project src/BtmPizza.Notifications -- send-test --all-types --all-devices --dry-run
/// </summary>
public static class SendTestCommand
{
    private static readonly string Usage = $"""
        Usage: send-test (--type <{string.Join("|", NotificationTypes.All)}> | --all-types)
                         (--user <user_id> [--user ...] | --all-devices)
                         [--title ..] [--body ..] [--kicker ..] [--image-url ..] [--dry-run]
        """;

    private static readonly HashSet<string> Flags = ["--all-types", "--all-devices", "--dry-run", "--help", "-h"];

    public static async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        var options = new Dictionary<string, List<string>>();
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (Flags.Contains(key)) options[key] = [];
            else if (key.StartsWith("--") && i + 1 < args.Length)
                (options.TryGetValue(key, out var list) ? list : options[key] = []).Add(args[++i]);
            else
            {
                Console.Error.WriteLine($"Unexpected argument \"{key}\"\n{Usage}");
                return 1;
            }
        }
        string? One(string key) => options.TryGetValue(key, out var v) ? v[^1] : null;

        var types = options.ContainsKey("--all-types") ? NotificationTypes.All
            : One("--type") is { } t ? [t.ToUpperInvariant()] : [];
        var users = options.GetValueOrDefault("--user") ?? [];
        var allDevices = options.ContainsKey("--all-devices");
        if (options.ContainsKey("--help") || options.ContainsKey("-h") || types.Length == 0 || (!allDevices && users.Count == 0))
        {
            Console.WriteLine(Usage);
            return options.ContainsKey("--help") || options.ContainsKey("-h") ? 0 : 1;
        }
        if (!types.All(NotificationTypes.All.Contains))
        {
            Console.Error.WriteLine($"--type must be one of {string.Join(", ", NotificationTypes.All)}");
            return 1;
        }

        var sender = services.GetRequiredService<NotificationSender>();
        var failed = false;
        foreach (var type in types)
        {
            var input = Samples.For(type);
            if (One("--title") is { } title) input["title"] = title;
            if (One("--body") is { } body) input["body"] = body;
            if (One("--kicker") is { } kicker) input["kicker"] = kicker;
            if (One("--image-url") is { } imageUrl) input["image_url"] = imageUrl;
            input["target"] = allDevices
                ? new JsonObject { ["all"] = true }
                : new JsonObject { ["user_ids"] = new JsonArray(users.Select(u => (JsonNode)u!).ToArray()) };
            input["dry_run"] = options.ContainsKey("--dry-run");

            try
            {
                var result = await sender.SendAsync(input);
                Console.WriteLine($"\n=== {type} ===");
                Console.WriteLine(JsonSerializer.Serialize(result, Program.ResponseJson(indented: true)));
            }
            catch (Exception ex)
            {
                failed = true;
                Console.Error.WriteLine($"\n=== {type} failed ===");
                Console.Error.WriteLine(ex is ValidationException v ? string.Join("\n", v.Errors) : ex.Message);
            }
        }
        return failed ? 1 : 0;
    }
}
