using System.Diagnostics;
using System.Text.Json;
using Encore.Core.Messaging;
using Encore.Core.Telemetry;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Encore.Worker;

/// <summary>
/// Consumes worker.album-import with manual acks.
/// Failure policy: transient error -> retry queue (delay via TTL) up to MaxAttempts -> DLQ.
/// Bad payload -> straight to DLQ (retrying cannot fix it).
/// Shutdown: unacked messages go back to the queue when the channel closes (at-least-once).
/// </summary>
public sealed class AlbumImportConsumer(
    RabbitMqConnection rabbit,
    IOptions<RabbitMqOptions> options,
    AlbumEnricher enricher,
    ILogger<AlbumImportConsumer> logger) : BackgroundService
{
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private IChannel? _channel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        var connection = await rabbit.GetAsync(stoppingToken);
        _channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true,
                consumerDispatchConcurrency: opts.ConsumerConcurrency),
            stoppingToken);

        await RabbitMqConnection.DeclareTopologyAsync(_channel, opts, stoppingToken);
        // Prefetch = how many unacked messages the broker pushes to us. Backpressure.
        await _channel.BasicQosAsync(prefetchSize: 0, prefetchCount: opts.ConsumerConcurrency, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += (_, delivery) => HandleAsync(delivery, stoppingToken);
        await _channel.BasicConsumeAsync(Topology.AlbumImportQueue, autoAck: false, consumer, stoppingToken);

        logger.LogInformation("Consuming {Queue} with concurrency {Concurrency}", Topology.AlbumImportQueue, opts.ConsumerConcurrency);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    private async Task HandleAsync(BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        var channel = _channel!;
        var body = delivery.Body.ToArray(); // the buffer is only valid during this callback
        var headers = delivery.BasicProperties.Headers;
        var attempt = ReadAttempt(headers);

        var parent = TraceContextHeaders.Extract(headers);
        using var activity = EncoreTelemetry.Source.StartActivity($"{Topology.AlbumImportQueue} process", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.message.id", delivery.BasicProperties.MessageId);
        activity?.SetTag("messaging.delivery.attempt", attempt);
        var start = Stopwatch.GetTimestamp();

        AlbumImportRequested? message = null;
        try
        {
            message = JsonSerializer.Deserialize<AlbumImportRequested>(body)
                      ?? throw new JsonException("Empty message body.");
            if (!Guid.TryParse(delivery.BasicProperties.MessageId, out var messageId))
                throw new JsonException("Message without a valid MessageId.");

            using var scope = logger.BeginScope(new Dictionary<string, object> { ["AlbumId"] = message.AlbumId, ["Attempt"] = attempt });

            var result = await enricher.EnrichAsync(messageId, message, ct);
            activity?.SetTag("encore.result", result.ToString());
            if (result == EnrichResult.Enriched) EncoreTelemetry.AlbumsEnriched.Add(1);
            logger.LogInformation("Album import finished: {Result}", result);

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: no ack, the broker redelivers to another instance.
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Poison message, sending to DLQ");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            await ForwardAsync(delivery, body, Topology.AlbumImportDeadLetterQueue, attempt, ct);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            EncoreTelemetry.AlbumImportsFailed.Add(1, new KeyValuePair<string, object?>("attempt", attempt));

            if (attempt < options.Value.MaxAttempts)
            {
                logger.LogWarning(ex, "Album import failed (attempt {Attempt}), scheduling retry", attempt);
                await ForwardAsync(delivery, body, Topology.AlbumImportRetryQueue, attempt + 1, ct);
            }
            else
            {
                logger.LogError(ex, "Album import failed {Attempt} times, sending to DLQ", attempt);
                await ForwardAsync(delivery, body, Topology.AlbumImportDeadLetterQueue, attempt, ct);
                if (message is not null)
                    await enricher.MarkFailedAsync(message.AlbumId, "Import failed after retries.", ct);
            }
        }
        finally
        {
            EncoreTelemetry.AlbumImportDuration.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }

    /// <summary>Publish a copy to another queue, then ack the original. Publish first: never lose it.</summary>
    private async Task ForwardAsync(BasicDeliverEventArgs delivery, byte[] body, string queue, int attempt, CancellationToken ct)
    {
        var props = new BasicProperties(delivery.BasicProperties)
        {
            Headers = new Dictionary<string, object?>(delivery.BasicProperties.Headers ?? new Dictionary<string, object?>())
            {
                [Topology.AttemptHeader] = attempt,
            },
        };

        await _publishLock.WaitAsync(ct); // one publisher at a time on this channel
        try
        {
            await _channel!.BasicPublishAsync(exchange: "", routingKey: queue, mandatory: true, props, body, ct);
        }
        finally
        {
            _publishLock.Release();
        }
        await _channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, ct);
    }

    private static int ReadAttempt(IDictionary<string, object?>? headers) =>
        headers?.TryGetValue(Topology.AttemptHeader, out var raw) == true && raw is IConvertible c ? c.ToInt32(null) : 1;

    public override void Dispose()
    {
        _channel?.Dispose();
        _publishLock.Dispose();
        base.Dispose();
    }
}
