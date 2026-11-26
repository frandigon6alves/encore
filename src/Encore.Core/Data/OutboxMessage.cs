namespace Encore.Core.Data;

/// <summary>
/// Transactional outbox row. Written in the same transaction as the business change,
/// published to RabbitMQ later by a background service. Guarantees "no event lost"
/// without a distributed transaction.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required string Type { get; init; }
    public required string Payload { get; init; }

    /// <summary>W3C traceparent of the request that wrote the row, so the trace survives the hop.</summary>
    public string? TraceParent { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Idempotent-consumer ledger: a message id already here was handled before.</summary>
public sealed class ProcessedMessage
{
    public Guid MessageId { get; init; }
    public required string Consumer { get; init; }
    public DateTimeOffset ProcessedAt { get; init; }
}
