using System.Collections.Concurrent;
using Encore.Core.MusicBrainz;

namespace Encore.IntegrationTests;

/// <summary>In-memory MusicBrainz. Every new album id gets an artist, 3 tracks and 2 tags.</summary>
public sealed class FakeMusicBrainz : IMusicBrainzClient
{
    public static readonly Guid UnknownAlbum = Guid.Parse("00000000-0000-0000-0000-00000000dead");
    private readonly ConcurrentDictionary<Guid, Guid> _releaseByAlbum = new();

    public Guid ArtistMbId { get; } = Guid.NewGuid();

    public Task<IReadOnlyList<AlbumSummary>> SearchAlbumsAsync(string query, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AlbumSummary>>([new AlbumSummary(Guid.NewGuid(), query, ArtistMbId, "Fake Artist", 1999)]);

    public Task<AlbumDetails?> GetAlbumAsync(Guid releaseGroupId, CancellationToken ct)
    {
        if (releaseGroupId == UnknownAlbum) return Task.FromResult<AlbumDetails?>(null);
        var releaseId = _releaseByAlbum.GetOrAdd(releaseGroupId, _ => Guid.NewGuid());
        return Task.FromResult<AlbumDetails?>(new AlbumDetails(
            new AlbumSummary(releaseGroupId, $"Album {releaseGroupId:N}"[..14], ArtistMbId, "Fake Artist", 1994),
            ["trip hop", "electronic"],
            releaseId));
    }

    public Task<MbRelease?> GetReleaseWithTracksAsync(Guid releaseId, CancellationToken ct) =>
        Task.FromResult<MbRelease?>(new MbRelease(releaseId, "Official", "1994-08-22",
            [new MbMedium(1, [new MbTrack(1, "Mysterons", 306000), new MbTrack(2, "Sour Times", 254000), new MbTrack(3, "Strangers", 238000)])]));
}
