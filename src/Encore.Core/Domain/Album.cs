namespace Encore.Core.Domain;

public enum AlbumStatus
{
    /// <summary>Saved from the search result; tracks and tags not fetched yet.</summary>
    Pending,
    Enriched,
    Failed,
}

public sealed record TrackInfo(int Position, string Title, int? LengthMs);

/// <summary>
/// An album in the shared catalog. Users point at it through <see cref="LibraryEntry"/>.
/// The worker fills tracks and tags asynchronously (see AlbumImportRequested).
/// </summary>
public sealed class Album
{
    private Album() { } // EF Core

    public static Album CreatePending(Guid mbId, string title, Artist artist, int? releaseYear, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("Album title is required.");
        if (releaseYear is < 1900 or > 2100) releaseYear = null;

        return new Album
        {
            Id = Guid.CreateVersion7(now),
            MbId = mbId,
            Title = title.Trim(),
            Artist = artist,
            ArtistId = artist.Id,
            ReleaseYear = releaseYear,
            CoverUrl = $"https://coverartarchive.org/release-group/{mbId}/front-250",
            Status = AlbumStatus.Pending,
            CreatedAt = now,
        };
    }

    public Guid Id { get; private set; }
    public Guid MbId { get; private set; }
    public string Title { get; private set; } = "";
    public Guid ArtistId { get; private set; }
    public Artist Artist { get; private set; } = null!;
    public int? ReleaseYear { get; private set; }
    public string? CoverUrl { get; private set; }
    public AlbumStatus Status { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? EnrichedAt { get; private set; }

    /// <summary>Optimistic concurrency token, mapped to Postgres' xmin system column.</summary>
    public uint Version { get; private set; }

    public List<Track> Tracks { get; private set; } = [];
    public List<AlbumTag> Tags { get; private set; } = [];

    /// <summary>
    /// Replaces tracks and tags in one go. Safe to call twice (a redelivered message):
    /// the second call produces the same state.
    /// </summary>
    public void MarkEnriched(IReadOnlyCollection<TrackInfo> tracks, IEnumerable<string> tags, DateTimeOffset now)
    {
        if (tracks.Count == 0) throw new DomainException("An enriched album needs at least one track.");

        Tracks.Clear();
        Tracks.AddRange(tracks
            .OrderBy(t => t.Position)
            .Select(t => new Track(Id, t.Position, t.Title, t.LengthMs)));

        Tags.Clear();
        Tags.AddRange(tags
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length is > 0 and <= 64)
            .Distinct()
            .Take(15)
            .Select(t => new AlbumTag(Id, t)));

        Status = AlbumStatus.Enriched;
        FailureReason = null;
        EnrichedAt = now;
    }

    public void MarkFailed(string reason)
    {
        if (Status == AlbumStatus.Enriched) return; // never downgrade good data
        Status = AlbumStatus.Failed;
        FailureReason = reason.Length > 500 ? reason[..500] : reason;
    }
}

public sealed class Track
{
    private Track() { } // EF Core

    internal Track(Guid albumId, int position, string title, int? lengthMs)
    {
        Id = Guid.CreateVersion7();
        AlbumId = albumId;
        Position = position;
        Title = title;
        LengthMs = lengthMs;
    }

    public Guid Id { get; private set; }
    public Guid AlbumId { get; private set; }
    public int Position { get; private set; }
    public string Title { get; private set; } = "";
    public int? LengthMs { get; private set; }
}

public sealed class AlbumTag
{
    private AlbumTag() { } // EF Core

    internal AlbumTag(Guid albumId, string tag)
    {
        AlbumId = albumId;
        Tag = tag;
    }

    public Guid AlbumId { get; private set; }
    public string Tag { get; private set; } = "";
}
