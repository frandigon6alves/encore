using Encore.Core.MusicBrainz;

namespace Encore.UnitTests;

public class MusicBrainzMapperTests
{
    [Fact]
    public void PickRelease_prefers_the_earliest_official_release()
    {
        var bootleg = new MbRelease(Guid.NewGuid(), "Bootleg", "1990-01-01", null);
        var remaster = new MbRelease(Guid.NewGuid(), "Official", "2009-06-01", null);
        var original = new MbRelease(Guid.NewGuid(), "Official", "1991-09-24", null);

        Assert.Equal(original.Id, MusicBrainzMapper.PickRelease([bootleg, remaster, original]));
    }

    [Fact]
    public void PickRelease_handles_missing_data()
    {
        Assert.Null(MusicBrainzMapper.PickRelease(null));
        Assert.Null(MusicBrainzMapper.PickRelease([]));
    }

    [Fact]
    public void ToTracks_numbers_multi_disc_releases_continuously()
    {
        var release = new MbRelease(Guid.NewGuid(), "Official", null,
        [
            new MbMedium(2, [new MbTrack(1, "Disc 2 Track 1", 1000)]),
            new MbMedium(1, [new MbTrack(2, "Disc 1 Track 2", 2000), new MbTrack(1, "Disc 1 Track 1", null)]),
        ]);

        var tracks = MusicBrainzMapper.ToTracks(release);

        Assert.Equal([1, 2, 3], tracks.Select(t => t.Position));
        Assert.Equal(["Disc 1 Track 1", "Disc 1 Track 2", "Disc 2 Track 1"], tracks.Select(t => t.Title));
    }

    [Theory]
    [InlineData("1997-05-21", 1997)]
    [InlineData("1969", 1969)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("19", null)]
    public void ParseYear(string? date, int? expected) => Assert.Equal(expected, MusicBrainzMapper.ParseYear(date));

    [Fact]
    public void TopTags_orders_by_votes_and_skips_downvoted()
    {
        var tags = MusicBrainzMapper.TopTags([new MbTag("jazz", 3), new MbTag("bad", -1), new MbTag("modal jazz", 7)]);
        Assert.Equal(["modal jazz", "jazz"], tags);
    }
}
