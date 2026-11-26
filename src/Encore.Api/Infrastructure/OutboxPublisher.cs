using System.Diagnostics;
using System.Text;
using Encore.Core.Data;
using Encore.Core.Messaging;
using Encore.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Encore.Api.Infrastructure;

/// <summary>
/// Polls outbox_messages and publishes them to RabbitMQ with publisher confirms.
/// At-least-once: a crash between publish and commit re-sends the message, so consumers
/// must be idempotent (see the worker's processed_messages table).
/// FOR UPDATE SKIP LOCKED lets several API replicas run this loop without double-sending.
/// </summary>
public sealed class OutboxPublisher(
    IDbContextFactory<EncoreDbContext> dbFactory,
    RabbitMqConnection rabbit,
    IOptions<RabbitMqOptions> options,
    TimeProvider clock,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IChannel? channel = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    channel ??= await OpenChannelAsync(stoppingToken);
                    while (await PublishBatchAsync(channel, stoppingToken) == BatchSize)
                    {
                        // full batch: there may be more, keep draining
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Outbox publishing failed; will retry");
                    if (channel is { IsOpen: false })
                    {
                        await channel.DisposeAsync();
                        channel = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
        finally
        {
            if (channel is not null) await channel.DisposeAsync();
        }
    }

    private async Task<IChannel> OpenChannelAsync(CancellationToken ct)
    {
        var connection = await rabbit.GetAsync(ct);
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            ct);
        await RabbitMqConnection.DeclareTopologyAsync(channel, options.Value, ct);
        return channel;
    }

    /// <returns>How many messages were published.</returns>
    private async Task<int> PublishBatchAsync(IChannel channel, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // User transactions + EF retry-on-failure must run inside the execution strategy.
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var batch = await db.OutboxMessages
                .FromSql($"""
                    SELECT * FROM outbox_messages
                    WHERE processed_at IS NULL
                    ORDER BY occurred_at
                    LIMIT {BatchSize}
                    FOR UPDATE SKIP LOCKED
                    """)
                .ToListAsync(ct);

            var published = 0;
            foreach (var message in batch)
            {
                ActivityContext.TryParse(message.TraceParent, null, out var parent);
                using var activity = EncoreTelemetry.Source.StartActivity($"{message.Type} publish", ActivityKind.Producer, parent);
                activity?.SetTag("messaging.system", "rabbitmq");
                activity?.SetTag("messaging.destination.name", Topology.EventsExchange);
                activity?.SetTag("messaging.message.id", message.Id);

                try
                {
                    var props = new BasicProperties
                    {
                        MessageId = message.Id.ToString(),
                        Type = message.Type,
                        ContentType = "application/json",
                        DeliveryMode = DeliveryModes.Persistent,
                        Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds()),
                        Headers = new Dictionary<string, object?> { [Topology.AttemptHeader] = 1 },
                    };
                    TraceContextHeaders.Inject(activity?.Context ?? parent, props.Headers);

                    // Awaits the broker's confirm; throws if the broker nacks or cannot route it.
                    await channel.BasicPublishAsync(Topology.EventsExchange, message.Type, mandatory: true, props,
                        Encoding.UTF8.GetBytes(message.Payload), ct);

                    message.ProcessedAt = clock.GetUtcNow();
                    published++;
                    EncoreTelemetry.OutboxPublished.Add(1);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    message.Attempts++;
                    message.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    logger.LogWarning(ex, "Could not publish outbox message {MessageId}", message.Id);
                    break; // keep order: stop at the first failure, retry next tick
                }
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return published;
        });
    }
}
