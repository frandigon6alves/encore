using Encore.Core.Domain;

namespace Encore.UnitTests;

public class LibraryEntryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static LibraryEntry NewEntry() =>
        new("fran", Album.CreatePending(Guid.NewGuid(), "Blue", new Artist(Guid.NewGuid(), "Joni Mitchell"), 1971, Now), Now);

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void Rate_rejects_out_of_range(int rating)
    {
        Assert.Throws<DomainException>(() => NewEntry().Rate(rating, null, Now));
    }

    [Fact]
    public void Rate_trims_review_and_turns_blank_into_null()
    {
        var entry = NewEntry();

        entry.Rate(5, "   ", Now);
        Assert.Null(entry.Review);

        entry.Rate(4, "  A river  ", Now);
        Assert.Equal("A river", entry.Review);
        Assert.Equal((short)4, entry.Rating);
    }

    [Fact]
    public void Rate_rejects_long_reviews()
    {
        Assert.Throws<DomainException>(() => NewEntry().Rate(3, new string('a', LibraryEntry.MaxReviewLength + 1), Now));
    }
}
