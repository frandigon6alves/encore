using Encore.Api.Library;

namespace Encore.UnitTests;

public class LibraryCursorTests
{
    [Fact]
    public void Roundtrips()
    {
        var cursor = new LibraryCursor(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1230), Guid.NewGuid());

        Assert.True(LibraryCursor.TryDecode(cursor.Encode(), out var decoded));
        Assert.Equal(cursor, decoded);
    }

    [Fact]
    public void Encoded_cursor_is_url_safe()
    {
        var encoded = new LibraryCursor(DateTimeOffset.UtcNow, Guid.NewGuid()).Encode();
        Assert.DoesNotContain(encoded, c => c is '+' or '/' or '=');
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("aGVsbG8")] // "hello"
    public void Rejects_garbage(string value) => Assert.False(LibraryCursor.TryDecode(value, out _));
}
