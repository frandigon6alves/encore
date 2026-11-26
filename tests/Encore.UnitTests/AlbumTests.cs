using Encore.Core.Domain;

namespace Encore.UnitTests;

public class AlbumTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private static Album NewAlbum() => Album.CreatePending(Guid.NewGuid(), " OK Computer ", new Artist(Guid.NewGuid(), "Radiohead"), 1997, Now);

    [Fact]
    public void CreatePending_trims_title_and_starts_pending()
    {
        var album = NewAlbum();

        Assert.Equal("OK Computer", album.Title);
        Assert.Equal(AlbumStatus.Pending, album.Status);
        Assert.Contains(album.MbId.ToString(), album.CoverUrl);
    }

    [Theory]
    [InlineData(1800)]
    [InlineData(3000)]
    public void CreatePending_drops_impossible_years(int year)
    {
        var album = Album.CreatePending(Guid.NewGuid(), "X", new Artist(Guid.NewGuid(), "Y"), year, Now);
        Assert.Null(album.ReleaseYear);
    }

    [Fact]
    public void MarkEnriched_orders_tracks_and_normalizes_tags()
    {
        var album = NewAlbum();

        album.MarkEnriched(
            [new TrackInfo(2, "Paranoid Android", 383000), new TrackInfo(1, "Airbag", 284000)],
            ["Alternative Rock", "alternative rock", " art rock ", ""],
            Now);

        Assert.Equal(AlbumStatus.Enriched, album.Status);
        Assert.Equal(["Airbag", "Paranoid Android"], album.Tracks.Select(t => t.Title));
        Assert.Equal(["alternative rock", "art rock"], album.Tags.Select(t => t.Tag));
    }

    [Fact]
    public void MarkEnriched_twice_gives_the_same_state()
    {
        var album = NewAlbum();
        TrackInfo[] tracks = [new(1, "Airbag", 284000)];

        album.MarkEnriched(tracks, ["rock"], Now);
        album.MarkEnriched(tracks, ["rock"], Now);

        Assert.Single(album.Tracks);
        Assert.Single(album.Tags);
    }

    [Fact]
    public void MarkEnriched_without_tracks_is_rejected()
    {
        Assert.Throws<DomainException>(() => NewAlbum().MarkEnriched([], ["rock"], Now));
    }

    [Fact]
    public void MarkFailed_never_downgrades_an_enriched_album()
    {
        var album = NewAlbum();
        album.MarkEnriched([new TrackInfo(1, "Airbag", null)], [], Now);

        album.MarkFailed("late failure from a duplicate message");

        Assert.Equal(AlbumStatus.Enriched, album.Status);
    }
}
