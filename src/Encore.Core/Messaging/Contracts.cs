namespace Encore.Core.Messaging;

/// <summary>Published when an album enters the catalog. The worker fetches tracks and tags.</summary>
public sealed record AlbumImportRequested(Guid AlbumId, Guid MbId)
{
    public const string RoutingKey = "album.import-requested";
}

public static class Topology
{
    public const string EventsExchange = "encore.events";

    public const string AlbumImportQueue = "worker.album-import";

    /// <summary>Messages wait here (TTL) then dead-letter back to the main queue: delayed retry.</summary>
    public const string AlbumImportRetryQueue = "worker.album-import.retry";

    /// <summary>Poison messages after the last attempt. Inspect in the RabbitMQ UI.</summary>
    public const string AlbumImportDeadLetterQueue = "worker.album-import.dlq";

    public const string AttemptHeader = "x-attempt";
}
