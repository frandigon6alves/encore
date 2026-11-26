using System.Diagnostics;
using System.Linq.Expressions;
using System.Text.Json;
using Encore.Core.Data;
using Encore.Core.Domain;
using Encore.Core.Messaging;
using Encore.Core.MusicBrainz;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Npgsql;

namespace Encore.Api.Library;

public enum AddOutcome { Added, AlreadyInLibrary, UnknownAlbum }

/// <summary>Application service: one place for the library use cases, endpoints stay thin.</summary>
public sealed class LibraryService(
    EncoreDbContext db,
    IMusicBrainzClient musicBrainz,
    HybridCache cache,
    TimeProvider clock,
    ILogger<LibraryService> logger)
{
    // Filter and sort on the entity, project to the DTO last: EF Core cannot translate a
    // Where/OrderBy over a DTO built through a constructor.
    private IQueryable<LibraryEntry> Entries(string userId) =>
        db.LibraryEntries.AsNoTracking().Where(e => e.UserId == userId);

    private static readonly Expression<Func<LibraryEntry, LibraryEntryDto>> ToDto = e => new LibraryEntryDto(
        e.AlbumId, e.Album.MbId, e.Album.Title, e.Album.Artist.Name, e.Album.ReleaseYear,
        e.Album.CoverUrl, e.Album.Status, e.Rating, e.Review, e.AddedAt);

    public async Task<IReadOnlyList<LibraryEntryDto>> ListAllAsync(string userId, CancellationToken ct) =>
        await Entries(userId).OrderByDescending(e => e.AddedAt).Select(ToDto).ToListAsync(ct);

    public Task<LibraryEntryDto?> GetAsync(string userId, Guid albumId, CancellationToken ct) =>
        Entries(userId).Where(e => e.AlbumId == albumId).Select(ToDto).SingleOrDefaultAsync(ct);

    public async Task<LibraryPage> PageAsync(string userId, LibraryCursor? after, int limit, CancellationToken ct)
    {
        var q = Entries(userId);
        if (after is { } c)
        {
            // Row-value comparison, served by the (user_id, added_at DESC, album_id DESC) index:
            // WHERE (added_at, album_id) < (@addedAt, @albumId)
            q = q.Where(e => EF.Functions.LessThan(
                ValueTuple.Create(e.AddedAt, e.AlbumId),
                ValueTuple.Create(c.AddedAt, c.AlbumId)));
        }

        var rows = await q.OrderByDescending(e => e.AddedAt).ThenByDescending(e => e.AlbumId)
            .Take(limit + 1) // one extra row tells us whether another page exists
            .Select(ToDto)
            .ToListAsync(ct);

        var hasMore = rows.Count > limit;
        var items = hasMore ? rows[..limit] : rows;
        var next = hasMore ? new LibraryCursor(items[^1].AddedAt, items[^1].AlbumId).Encode() : null;
        return new LibraryPage(items, next);
    }

    public async Task<(AddOutcome Outcome, LibraryEntryDto? Entry)> AddAsync(string userId, Guid mbId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryAddAsync(userId, mbId, ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } && attempt < 3)
            {
                // Two requests raced to insert the same artist/album/entry. The unique index kept
                // the data correct; drop our pending changes and re-read what the winner wrote.
                logger.LogInformation("Concurrent insert for {MbId}, retrying (attempt {Attempt})", mbId, attempt);
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<(AddOutcome, LibraryEntryDto?)> TryAddAsync(string userId, Guid mbId, CancellationToken ct)
    {
        var existing = await Entries(userId).Where(e => e.Album.MbId == mbId).Select(ToDto).SingleOrDefaultAsync(ct);
        if (existing is not null) return (AddOutcome.AlreadyInLibrary, existing);

        var album = await db.Albums.Include(a => a.Artist).SingleOrDefaultAsync(a => a.MbId == mbId, ct)
                    ?? await CreatePendingAlbumAsync(mbId, ct);
        if (album is null) return (AddOutcome.UnknownAlbum, null);

        db.LibraryEntries.Add(new LibraryEntry(userId, album, clock.GetUtcNow()));
        await db.SaveChangesAsync(ct); // album + outbox row + entry: one transaction

        return (AddOutcome.Added, await GetAsync(userId, album.Id, ct));
    }

    private async Task<Album?> CreatePendingAlbumAsync(Guid mbId, CancellationToken ct)
    {
        var details = await cache.GetOrCreateAsync(
            $"mb:album:{mbId}",
            async token => await musicBrainz.GetAlbumAsync(mbId, token),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromHours(6) },
            cancellationToken: ct);
        if (details is null) return null;

        var s = details.Summary;
        var artist = await db.Artists.SingleOrDefaultAsync(a => a.MbId == s.ArtistMbId, ct)
                     ?? db.Artists.Add(new Artist(s.ArtistMbId, s.ArtistName)).Entity;

        var now = clock.GetUtcNow();
        var album = Album.CreatePending(mbId, s.Title, artist, s.ReleaseYear, now);
        db.Albums.Add(album);

        // Transactional outbox: the event is saved with the album, published later.
        db.OutboxMessages.Add(new OutboxMessage
        {
            Type = AlbumImportRequested.RoutingKey,
            Payload = JsonSerializer.Serialize(new AlbumImportRequested(album.Id, mbId)),
            TraceParent = Activity.Current?.Id,
            OccurredAt = now,
        });

        return album;
    }

    public async Task<LibraryEntryDto?> RateAsync(string userId, Guid albumId, RateRequest request, CancellationToken ct)
    {
        var entry = await db.LibraryEntries.SingleOrDefaultAsync(e => e.UserId == userId && e.AlbumId == albumId, ct);
        if (entry is null) return null;

        entry.Rate(request.Rating, request.Review, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return await GetAsync(userId, albumId, ct);
    }

    public async Task<bool> RemoveAsync(string userId, Guid albumId, CancellationToken ct) =>
        await db.LibraryEntries.Where(e => e.UserId == userId && e.AlbumId == albumId).ExecuteDeleteAsync(ct) > 0;
}
