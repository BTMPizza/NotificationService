using System.Text.Encodings.Web;
using System.Text.Json;
using BtmPizza.Notifications;
using BtmPizza.Notifications.Apns;
using BtmPizza.Notifications.Cli;
using BtmPizza.Notifications.Configuration;
using BtmPizza.Notifications.Data;
using BtmPizza.Notifications.Messaging;
using BtmPizza.Notifications.Payload;

// `dotnet run -- send-test ...` runs the test-send CLI instead of the web API.
var isCli = args is ["send-test", ..];
var builder = WebApplication.CreateBuilder(isCli ? [] : args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

builder.Services.AddSingleton(sp => CredentialsLoader.Load(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(sp =>
{
    var root = sp.GetRequiredService<Credentials>().RootDir;
    var dbPath = sp.GetRequiredService<IConfiguration>()["DbPath"];
    return new DeviceStore(dbPath ?? Path.Combine(root, "data", "notifications.db"));
});
builder.Services.AddSingleton<IApnsClient>(sp =>
{
    var credentials = sp.GetRequiredService<Credentials>();
    if (!credentials.Apns.Configured)
    {
        sp.GetRequiredService<ILogger<ApnsClient>>().LogWarning(
            "APNs not configured, sending disabled (dry runs still work): {Problems}", string.Join("; ", credentials.Apns.Problems));
    }
    var config = sp.GetRequiredService<IConfiguration>();
    return new ApnsClient(credentials.Apns, new ApnsHosts(config["Apns:SandboxUrl"]!, config["Apns:ProductionUrl"]!));
});
builder.Services.AddSingleton<IImageUrlVerifier>(_ => new ImageUrlVerifier(
    new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(5) }));
builder.Services.AddSingleton<NotificationSender>();
if (!isCli) builder.Services.AddHostedService<RabbitMqConsumer>();

var app = builder.Build();

// Load credentials and open the database up front so misconfiguration fails at startup.
app.Services.GetRequiredService<IApnsClient>();
app.Services.GetRequiredService<DeviceStore>();

if (isCli)
{
    var exitCode = await SendTestCommand.RunAsync(args[1..], app.Services);
    await app.DisposeAsync(); // flushes the console logger
    return exitCode;
}

app.UseApiErrors();
app.MapNotificationEndpoints();
app.Lifetime.ApplicationStarted.Register(() =>
    app.Logger.LogInformation("BTM Pizza notification service on {Urls} (admin form: /admin)", string.Join(", ", app.Urls)));
await app.RunAsync();
return 0;

public partial class Program
{
    public static JsonSerializerOptions ResponseJson(bool indented = false) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = indented,
    };
}
