using System.Collections;
using System.Diagnostics;
using System.Net.Http.Headers;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace BtmPizza.Notifications.Observability;

/// <summary>
/// The BTM transaction a request belongs to: <c>btm_tid</c> (one id for the whole call, from the app through the
/// gateway to the last service), the <c>client_type</c> (iphone / android) and, once the gateway has authenticated
/// the caller, the <c>customer_id</c>.
/// <para>
/// Pushed as a logging scope, so every log line written while handling the request carries these three fields
/// (OpenTelemetry exports scope values as log attributes in Loki). Also stored in an AsyncLocal so outgoing
/// HttpClient calls forward the same headers to the next service.
/// </para>
/// </summary>
public sealed class BtmTraceContext : IReadOnlyList<KeyValuePair<string, object>>
{
    /// <summary>Header carrying the transaction id. Hyphenated on the wire: nginx drops headers with underscores by default.</summary>
    public const string TidHeader = "btm-tid";

    /// <summary>Underscore spelling, accepted on input only (in case a client sends it and nothing in between strips it).</summary>
    public const string TidHeaderUnderscore = "btm_tid";

    public const string ClientTypeHeader = "client-type";
    public const string CustomerIdHeader = "customer-id";

    public const string TidField = "btm_tid";
    public const string ClientTypeField = "client_type";
    public const string CustomerIdField = "customer_id";

    public const string UnknownClientType = "unknown";

    /// <summary>The client types the apps send.</summary>
    public static readonly IReadOnlySet<string> KnownClientTypes = new HashSet<string>(StringComparer.Ordinal) { "iphone", "android" };

    private const int MaxValueLength = 64;
    private static readonly AsyncLocal<BtmTraceContext?> CurrentContext = new();

    public BtmTraceContext(string tid, string clientType, string? customerId)
    {
        Tid = tid;
        ClientType = clientType;
        CustomerId = customerId;
    }

    public string Tid { get; }

    public string ClientType { get; }

    /// <summary>Settable because the gateway only learns it after authentication, part-way through the request.</summary>
    public string? CustomerId { get; set; }

    /// <summary>True when the request arrived without a usable btm-tid and this one was generated here.</summary>
    public bool TidGenerated { get; private init; }

    /// <summary>The transaction of the request being handled on this async flow, or null outside a request.</summary>
    public static BtmTraceContext? Current
    {
        get => CurrentContext.Value;
        set => CurrentContext.Value = value;
    }

    /// <summary>
    /// Reads the three headers. A missing or malformed btm-tid is replaced by a new one. <paramref name="trustCustomerId"/>
    /// is false at the gateway (clients must not choose their own customer id) and true behind it.
    /// </summary>
    public static BtmTraceContext FromHeaders(IHeaderDictionary headers, bool trustCustomerId)
    {
        var tid = Clean(headers[TidHeader]) ?? Clean(headers[TidHeaderUnderscore]);
        var clientType = NormalizeClientType(headers[ClientTypeHeader]);
        var customerId = trustCustomerId ? Clean(headers[CustomerIdHeader]) : null;
        return new BtmTraceContext(tid ?? NewTid(), clientType, customerId) { TidGenerated = tid is null };
    }

    public static string NewTid() => Guid.NewGuid().ToString("N");

    /// <summary>Lower-cased iphone / android, or "unknown" for anything else (including nothing).</summary>
    public static string NormalizeClientType(string? value)
    {
        var clean = Clean(value)?.ToLowerInvariant();
        return clean is not null && KnownClientTypes.Contains(clean) ? clean : UnknownClientType;
    }

    /// <summary>Stamps the headers onto a request to the next service, replacing any already there.</summary>
    public void ApplyTo(HttpRequestHeaders headers)
    {
        headers.Remove(TidHeader);
        headers.Remove(TidHeaderUnderscore);
        headers.Remove(ClientTypeHeader);
        headers.Remove(CustomerIdHeader);
        headers.TryAddWithoutValidation(TidHeader, Tid);
        headers.TryAddWithoutValidation(ClientTypeHeader, ClientType);
        if (CustomerId is not null)
        {
            headers.TryAddWithoutValidation(CustomerIdHeader, CustomerId);
        }
    }

    /// <summary>Adds the fields to a span so a Tempo trace can be found by btm_tid.</summary>
    public void Tag(Activity? activity)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(TidField, Tid);
        activity.SetTag(ClientTypeField, ClientType);
        if (CustomerId is not null)
        {
            activity.SetTag(CustomerIdField, CustomerId);
        }
    }

    // Header values end up in logs: keep them short and free of control characters.
    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxValueLength)
        {
            return null;
        }

        foreach (var c in trimmed)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':'))
            {
                return null;
            }
        }

        return trimmed;
    }

    // ---- Logging scope: read at log time, so a customer id set after authentication shows up from then on ----

    public int Count => CustomerId is null ? 2 : 3;

    public KeyValuePair<string, object> this[int index] => index switch
    {
        0 => new(TidField, Tid),
        1 => new(ClientTypeField, ClientType),
        2 when CustomerId is not null => new(CustomerIdField, CustomerId),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() =>
        $"{TidField}:{Tid} {ClientTypeField}:{ClientType} {CustomerIdField}:{CustomerId ?? "-"}";
}

/// <summary>
/// Picks up the transaction from the incoming headers (set by the gateway), puts it on every log line and span for
/// the request, and echoes btm-tid on the response. Register first, before the exception handler, so errors logged
/// by the exception handler carry the transaction too.
/// </summary>
public sealed class BtmTraceMiddleware(RequestDelegate next, ILogger<BtmTraceMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var trace = BtmTraceContext.FromHeaders(context.Request.Headers, trustCustomerId: true);
        BtmTraceContext.Current = trace;
        trace.Tag(Activity.Current);

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[BtmTraceContext.TidHeader] = trace.Tid;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(trace))
        {
            await next(context);
        }
    }
}

/// <summary>Forwards btm-tid, client-type and customer-id on every outgoing HttpClient call made while handling a request.</summary>
public sealed class BtmTracePropagationHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        BtmTraceContext.Current?.ApplyTo(request.Headers);
        return base.SendAsync(request, cancellationToken);
    }
}

public static class BtmObservabilityExtensions
{
    /// <summary>
    /// OpenTelemetry logs, traces and metrics over OTLP. Endpoint, protocol, service name and resource attributes come
    /// from the OTEL_* environment variables set in the Helm chart. Also forwards the BTM transaction headers on every
    /// HttpClient created through IHttpClientFactory.
    /// </summary>
    public static WebApplicationBuilder AddBtmObservability(this WebApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !IsProbe(ctx.Request.Path))
                .AddHttpClientInstrumentation())
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .UseOtlpExporter();

        builder.Services.AddTransient<BtmTracePropagationHandler>();
        builder.Services.ConfigureHttpClientDefaults(http => http.AddHttpMessageHandler<BtmTracePropagationHandler>());
        return builder;
    }

    // Kubernetes probes hit these every few seconds; tracing them would bury the real requests in Tempo.
    private static bool IsProbe(PathString path) =>
        path.StartsWithSegments("/health") || path.StartsWithSegments("/healthz");
}
