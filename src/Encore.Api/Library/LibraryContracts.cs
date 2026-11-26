using System.ComponentModel.DataAnnotations;
using System.Text;
using Encore.Core.Domain;

namespace Encore.Api.Library;

public sealed record AddToLibraryRequest([property: Required] Guid MbId);

public sealed record RateRequest(
    [property: Range(1, 5)] int Rating,
    [property: MaxLength(LibraryEntry.MaxReviewLength)] string? Review);

public sealed record LibraryEntryDto(
    Guid AlbumId,
    Guid MbId,
    string Title,
    string Artist,
    int? ReleaseYear,
    string? CoverUrl,
    AlbumStatus Status,
    int? Rating,
    string? Review,
    DateTimeOffset AddedAt);

/// <summary>v2 list response: keyset (cursor) pagination instead of v1's full array.</summary>
public sealed record LibraryPage(IReadOnlyList<LibraryEntryDto> Items, string? NextCursor);

/// <summary>
/// Opaque cursor = position of the last row seen, (added_at, album_id).
/// Keyset beats OFFSET: cost does not grow with the page number and rows added meanwhile
/// do not shift pages.
/// </summary>
public readonly record struct LibraryCursor(DateTimeOffset AddedAt, Guid AlbumId)
{
    public string Encode() =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{AddedAt.UtcTicks}|{AlbumId:N}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryDecode(string? value, out LibraryCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrEmpty(value)) return false;
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(base64)).Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) || !Guid.TryParseExact(parts[1], "N", out var id))
                return false;
            cursor = new LibraryCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
