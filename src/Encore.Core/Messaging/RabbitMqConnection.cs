using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Encore.Core.Messaging;

public sealed class RabbitMqOptions
{
    public string Uri { get; set; } = "amqp://guest:guest@localhost:5672";

    /// <summary>Messages a consumer handles in parallel. Also the prefetch count.</summary>
    public ushort ConsumerConcurrency { get; set; } = 4;

    public int RetryDelayMs { get; set; } = 5000;
    public int MaxAttempts { get; set; } = 3;
}

/// <summary>
/// One long-lived AMQP connection per process (connections are expensive, channels are cheap).
/// Created lazily so the app can boot before RabbitMQ is ready.
/// </summary>
public sealed class RabbitMqConnection(IOptions<RabbitMqOptions> options, ILogger<RabbitMqConnection> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public bool IsOpen => _connection?.IsOpen == true;

    public async Task<IConnection> GetAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true } open) return open;

        await _gate.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true } again) return again;

            var factory = new ConnectionFactory
            {
                Uri = new Uri(options.Value.Uri),
                AutomaticRecoveryEnabled = true,
                ConsumerDispatchConcurrency = options.Value.ConsumerConcurrency,
                ClientProvidedName = AppDomain.CurrentDomain.FriendlyName,
            };

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    _connection = await factory.CreateConnectionAsync(ct);
                    logger.LogInformation("Connected to RabbitMQ after {Attempt} attempt(s)", attempt);
                    return _connection;
                }
                catch (Exception ex) when (attempt < 10 && !ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "RabbitMQ not reachable (attempt {Attempt}), retrying", attempt);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt * 2, 10)), ct);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Idempotent: declaring an existing exchange/queue with the same arguments is a no-op.</summary>
    public static async Task DeclareTopologyAsync(IChannel channel, RabbitMqOptions options, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(Topology.EventsExchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);

        await channel.QueueDeclareAsync(Topology.AlbumImportQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(Topology.AlbumImportQueue, Topology.EventsExchange, AlbumImportRequested.RoutingKey, cancellationToken: ct);

        await channel.QueueDeclareAsync(Topology.AlbumImportRetryQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-message-ttl"] = options.RetryDelayMs,
                ["x-dead-letter-exchange"] = "",                       // default exchange...
                ["x-dead-letter-routing-key"] = Topology.AlbumImportQueue, // ...routes back to the main queue
            },
            cancellationToken: ct);

        await channel.QueueDeclareAsync(Topology.AlbumImportDeadLetterQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        _gate.Dispose();
    }
}
