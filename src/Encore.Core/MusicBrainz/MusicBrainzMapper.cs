using Encore.Core.Domain;

namespace Encore.Core.MusicBrainz;

/// <summary>Pure functions from MusicBrainz wire models to app models. Unit tested.</summary>
public static class MusicBrainzMapper
{
    public static AlbumSummary? ToSummary(MbReleaseGroup rg)
    {
        var credit = rg.ArtistCredit?.FirstOrDefault();
        if (credit is null) return null;
        return new AlbumSummary(rg.Id, rg.Title, credit.Artist.Id, credit.Artist.Name, ParseYear(rg.FirstReleaseDate));
    }

    public static int? ParseYear(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out var year) ? year : null;

    /// <summary>Tags voted by more people first. MusicBrainz tags are folksonomy: "rock", "90s", "seen live"...</summary>
    public static IReadOnlyList<string> TopTags(IEnumerable<MbTag>? tags, int max = 10) =>
        tags?.Where(t => t.Count > 0).OrderByDescending(t => t.Count).ThenBy(t => t.Name)
            .Select(t => t.Name).Take(max).ToList() ?? [];

    /// <summary>
    /// A release group (the "album") has many releases (CD, vinyl, remaster...).
    /// Prefer the earliest official one; it holds the canonical tracklist.
    /// </summary>
    public static Guid? PickRelease(IEnumerable<MbRelease>? releases) =>
        releases?
            .OrderBy(r => string.Equals(r.Status, "Official", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(r => string.IsNullOrEmpty(r.Date) ? "9999" : r.Date, StringComparer.Ordinal)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefault();

    /// <summary>Flattens multi-disc releases into one running track number.</summary>
    public static IReadOnlyList<TrackInfo> ToTracks(MbRelease release)
    {
        var position = 0;
        return (release.Media ?? [])
            .OrderBy(m => m.Position)
            .SelectMany(m => (m.Tracks ?? []).OrderBy(t => t.Position))
            .Select(t => new TrackInfo(++position, t.Title, t.Length))
            .ToList();
    }
}
