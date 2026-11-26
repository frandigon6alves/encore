namespace Encore.Core.Domain;

/// <summary>One album in one user's library, with the user's rating and review.</summary>
public sealed class LibraryEntry
{
    public const int MaxReviewLength = 2000;

    private LibraryEntry() { } // EF Core

    public LibraryEntry(string userId, Album album, DateTimeOffset now)
    {
        UserId = userId;
        AlbumId = album.Id;
        Album = album;
        AddedAt = now;
    }

    public string UserId { get; private set; } = "";
    public Guid AlbumId { get; private set; }
    public Album Album { get; private set; } = null!;
    public short? Rating { get; private set; }
    public string? Review { get; private set; }
    public DateTimeOffset AddedAt { get; private set; }
    public DateTimeOffset? RatedAt { get; private set; }

    public void Rate(int rating, string? review, DateTimeOffset now)
    {
        if (rating is < 1 or > 5) throw new DomainException("Rating must be between 1 and 5.");
        review = string.IsNullOrWhiteSpace(review) ? null : review.Trim();
        if (review?.Length > MaxReviewLength) throw new DomainException($"Review must be at most {MaxReviewLength} characters.");

        Rating = (short)rating;
        Review = review;
        RatedAt = now;
    }
}
