using System.Text.Json.Serialization;

namespace Encore.Core.MusicBrainz;

// Wire models: only the fields we read from https://musicbrainz.org/ws/2 (JSON).

public sealed record MbSearchResponse([property: JsonPropertyName("release-groups")] List<MbReleaseGroup> ReleaseGroups);

public sealed record MbReleaseGroup(
    Guid Id,
    string Title,
    [property: JsonPropertyName("primary-type")] string? PrimaryType,
    [property: JsonPropertyName("first-release-date")] string? FirstReleaseDate,
    [property: JsonPropertyName("artist-credit")] List<MbArtistCredit>? ArtistCredit,
    List<MbTag>? Tags,
    List<MbRelease>? Releases);

public sealed record MbArtistCredit(string Name, MbArtist Artist);

public sealed record MbArtist(Guid Id, string Name);

public sealed record MbTag(string Name, int Count);

public sealed record MbRelease(Guid Id, string? Status, string? Date, List<MbMedium>? Media);

public sealed record MbMedium(int Position, List<MbTrack>? Tracks);

public sealed record MbTrack(int Position, string Title, int? Length);

// What the rest of the app sees.

public sealed record AlbumSummary(Guid MbId, string Title, Guid ArtistMbId, string ArtistName, int? ReleaseYear)
{
    public string CoverUrl => $"https://coverartarchive.org/release-group/{MbId}/front-250";
}

public sealed record AlbumDetails(AlbumSummary Summary, IReadOnlyList<string> Tags, Guid? PreferredReleaseId);
