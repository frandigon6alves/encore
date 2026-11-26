using Encore.Core.Data;
using Encore.Core.Domain;
using Encore.Core.Messaging;
using Encore.Core.MusicBrainz;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Encore.Worker;

public enum EnrichResult { Enriched, AlreadyProcessed, AlbumMissing, NoTracklistUpstream }

/// <summary>
/// Handles one AlbumImportRequested. Idempotent: the message id goes into processed_messages
/// in the same transaction as the album changes, so a redelivered message is a no-op.
/// </summary>
public sealed class AlbumEnricher(
    IDbContextFactory<EncoreDbContext> dbFactory,
    IMusicBrainzClient musicBrainz,
    TimeProvider clock,
    ILogger<AlbumEnricher> logger)
{
    public const string ConsumerName = "album-import";

    public async Task<EnrichResult> EnrichAsync(Guid messageId, AlbumImportRequested message, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (await db.ProcessedMessages.AnyAsync(p => p.MessageId == messageId && p.Consumer == ConsumerName, ct))
            return EnrichResult.AlreadyProcessed;

        var album = await db.Albums
            .Include(a => a.Tracks)
            .Include(a => a.Tags)
            .AsSplitQuery()
            .SingleOrDefaultAsync(a => a.Id == message.AlbumId, ct);

        EnrichResult result;
        if (album is null)
        {
            result = EnrichResult.AlbumMissing;
        }
        else if (album.Status == AlbumStatus.Enriched)
        {
            result = EnrichResult.AlreadyProcessed;
        }
        else
        {
            // Network calls first, writes last: no DB transaction stays open while waiting on MusicBrainz.
            var details = await musicBrainz.GetAlbumAsync(message.MbId, ct);
            var release = details?.PreferredReleaseId is { } releaseId
                ? await musicBrainz.GetReleaseWithTracksAsync(releaseId, ct)
                : null;
            var tracks = release is null ? [] : MusicBrainzMapper.ToTracks(release);

            if (details is null || tracks.Count == 0)
            {
                album.MarkFailed("MusicBrainz has no tracklist for this album.");
                result = EnrichResult.NoTracklistUpstream;
            }
            else
            {
                album.MarkEnriched(tracks, details.Tags, clock.GetUtcNow());
                result = EnrichResult.Enriched;
            }
        }

        db.ProcessedMessages.Add(new ProcessedMessage { MessageId = messageId, Consumer = ConsumerName, ProcessedAt = clock.GetUtcNow() });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The same message was redelivered to another consumer that committed first.
            logger.LogInformation("Message {MessageId} already processed by a concurrent consumer", messageId);
            return EnrichResult.AlreadyProcessed;
        }

        return result;
    }

    /// <summary>Called when the message is dead-lettered, so the UI stops showing "Pending" forever.</summary>
    public async Task MarkFailedAsync(Guid albumId, string reason, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var album = await db.Albums.SingleOrDefaultAsync(a => a.Id == albumId, ct);
        if (album is null) return;
        album.MarkFailed(reason);
        await db.SaveChangesAsync(ct);
    }
}
