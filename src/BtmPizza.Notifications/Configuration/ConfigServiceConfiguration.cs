// BTM Pizza ConfigService client. Canonical copy: configService/client/ConfigServiceConfiguration.cs. Copy it unchanged.
//
// Loads settings changed in the Admin app, on top of appsettings and env vars, and refreshes them every 30 seconds.
// Off unless ConfigService__BaseUrl is set. ConfigService being down never stops a service: it starts with its
// deployed values, keeps the last values it fetched, and catches up when ConfigService returns.

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BtmPizza.ConfigClient;

public static class ConfigServiceExtensions
{
    public const string BaseUrlKey = "ConfigService:BaseUrl";
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>Call right after CreateBuilder: added last, so these values win over appsettings and env vars.</summary>
    public static ConfigurationManager AddConfigService(this ConfigurationManager configuration, string service)
    {
        if (configuration[BaseUrlKey] is { Length: > 0 } baseUrl)
        {
            var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(2) };
            ((IConfigurationBuilder)configuration).Add(new ConfigServiceSource(service, http, RefreshInterval));
        }

        return configuration;
    }

    /// <summary>Makes IOptions&lt;T&gt;.Value follow config changes, so consumers need no change beyond not caching .Value.</summary>
    public static IServiceCollection AddLiveOptions<T>(this IServiceCollection services) where T : class, new() =>
        services.AddSingleton<IOptions<T>, LiveOptions<T>>();
}

/// <summary>A value that fails to bind or validate is ignored: the last good options stay in use.</summary>
public sealed class LiveOptions<T>(IOptionsMonitor<T> monitor) : IOptions<T> where T : class, new()
{
    private T _last = new();

    public T Value
    {
        get
        {
            try
            {
                return _last = monitor.CurrentValue;
            }
            catch (Exception ex) when (ex is InvalidOperationException or OptionsValidationException)
            {
                return _last;
            }
        }
    }
}

public sealed class ConfigServiceSource(string service, HttpClient http, TimeSpan interval) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new ConfigServiceProvider(service, http, interval);
}

public sealed class ConfigServiceProvider(string service, HttpClient http, TimeSpan interval) : ConfigurationProvider, IDisposable
{
    private readonly string _path = $"api/config/internal/services/{Uri.EscapeDataString(service)}";
    private Timer? _timer;
    private bool _unreachable;

    public override void Load()
    {
        // Task.Run: no sync context, and the first load blocks startup for at most the 2 second timeout.
        if (Task.Run(FetchAsync).GetAwaiter().GetResult() is { } values)
        {
            Data = values;
        }

        _timer ??= new Timer(_ => _ = RefreshAsync(), null, interval, interval);
    }

    public async Task RefreshAsync()
    {
        if (await FetchAsync() is { } values && !Same(values, Data))
        {
            Data = values;
            try
            {
                OnReload();
            }
            catch (Exception ex)
            {
                // A listener refused the new values (bad format or failed validation). LiveOptions keeps the last good
                // ones; the polling thread must survive this and pick up the corrected value later.
                Console.Error.WriteLine($"ConfigService values not applied: {ex.Message}");
            }
        }
    }

    private async Task<Dictionary<string, string?>?> FetchAsync()
    {
        try
        {
            using var response = await http.GetAsync(_path);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(body);

            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in json.RootElement.GetProperty("values").EnumerateObject())
            {
                values[value.Name] = value.Value.GetString();
            }

            _unreachable = false;
            return values;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            if (!_unreachable)
            {
                Console.Error.WriteLine($"ConfigService unreachable, keeping current settings: {ex.Message}");
            }

            _unreachable = true;
            return null;
        }
    }

    private static bool Same(Dictionary<string, string?> a, IDictionary<string, string?> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);

    public void Dispose() => _timer?.Dispose();
}
