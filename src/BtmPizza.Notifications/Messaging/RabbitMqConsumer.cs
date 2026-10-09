using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BtmPizza.Notifications.Configuration;
using BtmPizza.Notifications.Payload;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Credentials = BtmPizza.Notifications.Configuration.Credentials;

namespace BtmPizza.Notifications.Messaging;

/// <summary>
/// Consumes send requests from RabbitMQ. Publishers send to <see cref="Exchange"/> with
/// <see cref="RoutingKey"/>; the message body is the same JSON as POST /notifications/send.
/// Messages that fail (bad JSON, validation errors, APNs not configured) are dead-lettered
/// to <see cref="DeadLetterQueue"/> instead of retried.
/// </summary>
public sealed partial class RabbitMqConsumer(
    Credentials credentials,
    NotificationSender sender,
    ILogger<RabbitMqConsumer> logger) : BackgroundService
{
    public const string Exchange = "btm.notifications";
    public const string RoutingKey = "notification.send";
    public const string Queue = "notification-service.send";
    public const string DeadLetterExchange = "btm.notifications.dlx";
    public const string DeadLetterQueue = "notification-service.send.dlq";

    private const ushort Prefetch = 10;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    [GeneratedRegex("//([^:/]+):[^@]*@")]
    private static partial Regex UrlPassword();

    private static string Redact(string url) => UrlPassword().Replace(url, "//$1:***@");

    public static async Task DeclareTopologyAsync(IChannel channel, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.QueueDeclareAsync(DeadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(DeadLetterQueue, DeadLetterExchange, "", cancellationToken: ct);
        await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = DeadLetterExchange }, cancellationToken: ct);
        await channel.QueueBindAsync(Queue, Exchange, RoutingKey, cancellationToken: ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (credentials.RabbitMqUrl is not { } url)
        {
            logger.LogWarning("rabbitmq.url not set in credentials.json; not consuming from RabbitMQ");
            return;
        }

        var factory = new ConnectionFactory
        {
            Uri = new Uri(url),
            AutomaticRecoveryEnabled = false, // the loop below reconnects and re-declares everything
            ConsumerDispatchConcurrency = Prefetch,
            ClientProvidedName = "btm-notification-service",
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await factory.CreateConnectionAsync(stoppingToken);
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.ConnectionShutdownAsync += (_, e) =>
                {
                    closed.TrySetResult();
                    return Task.CompletedTask;
                };

                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await DeclareTopologyAsync(channel, stoppingToken);
                await channel.BasicQosAsync(0, Prefetch, global: false, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += (_, ea) => HandleMessageAsync(channel, ea, stoppingToken);
                await channel.BasicConsumeAsync(Queue, autoAck: false, consumer, stoppingToken);

                logger.LogInformation(
                    "Consuming RabbitMQ {Url}: exchange \"{Exchange}\" (topic), routing key \"{RoutingKey}\", queue \"{Queue}\"",
                    Redact(url), Exchange, RoutingKey, Queue);

                await closed.Task.WaitAsync(stoppingToken);
                logger.LogWarning("RabbitMQ connection closed; reconnecting");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("RabbitMQ unavailable ({Error}); retrying in {Seconds}s", ex.Message, ReconnectDelay.TotalSeconds);
            }

            await Task.Delay(ReconnectDelay, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private async Task HandleMessageAsync(IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        var raw = Encoding.UTF8.GetString(ea.Body.Span);
        logger.LogDebug(
            "RabbitMQ message received (exchange={Exchange} routingKey={RoutingKey} redelivered={Redelivered}):\n{Body}",
            ea.Exchange, ea.RoutingKey, ea.Redelivered, raw);

        JsonNode? input;
        try
        {
            input = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            logger.LogError("RabbitMQ message is not valid JSON; dead-lettering it to {Queue}", DeadLetterQueue);
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
            return;
        }

        try
        {
            var result = await sender.SendAsync(input, ct);
            if (result.DryRun)
                logger.LogInformation("Dry run {NotificationId}: {TargetCount} target device(s), nothing sent",
                    result.NotificationId, result.TargetCount);
            foreach (var warning in result.Warnings) logger.LogWarning("{Warning}", warning);
            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var reason = ex is ValidationException v ? $"validation failed: {string.Join("; ", v.Errors)}" : ex.Message;
            logger.LogError("RabbitMQ message rejected and dead-lettered to {Queue}: {Reason}", DeadLetterQueue, reason);
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
        }
    }
}
