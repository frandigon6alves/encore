using System.Net.Http.Json;
using System.Text.Json;
using Encore.Api.Library;
using Encore.Api.Stats;
using Encore.Core.Messaging;

namespace Encore.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class GraphQLAndStatsTests(EncoreApiFactory app)
{
    private async Task<(HttpClient Client, Guid AlbumId)> UserWithEnrichedRatedAlbumAsync()
    {
        var client = await app.CreateClientForAsync($"user-{Guid.NewGuid():N}"[..20]);
        var mbId = Guid.NewGuid();
        var entry = await (await client.PostAsJsonAsync("/api/v1/library", new AddToLibraryRequest(mbId)))
            .Content.ReadFromJsonAsync<LibraryEntryDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            });
        await app.CreateEnricher().EnrichAsync(Guid.NewGuid(), new AlbumImportRequested(entry!.AlbumId, mbId), default);
        await client.PutAsJsonAsync($"/api/v1/library/{entry.AlbumId}/rating", new RateRequest(4, null));
        return (client, entry.AlbumId);
    }

    [Fact]
    public async Task GraphQL_resolves_library_with_nested_album_artist_and_tracks()
    {
        var (client, albumId) = await UserWithEnrichedRatedAlbumAsync();

        var response = await client.PostAsJsonAsync("/graphql", new
        {
            query = "{ myLibrary { rating album { id title totalLengthMs tags artist { name } tracks { position title } } } }",
        });
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(json.RootElement.TryGetProperty("errors", out var errors), errors.ToString());
        var entry = json.RootElement.GetProperty("data").GetProperty("myLibrary")[0];
        var album = entry.GetProperty("album");
        Assert.Equal(4, entry.GetProperty("rating").GetInt32());
        Assert.Equal(albumId, album.GetProperty("id").GetGuid());
        Assert.Equal("Fake Artist", album.GetProperty("artist").GetProperty("name").GetString());
        Assert.Equal(3, album.GetProperty("tracks").GetArrayLength());
        Assert.Equal(306000 + 254000 + 238000, album.GetProperty("totalLengthMs").GetInt32());
    }

    [Fact]
    public async Task GraphQL_myLibrary_requires_auth()
    {
        var response = await app.CreateClient().PostAsJsonAsync("/graphql", new { query = "{ myLibrary { rating } }" });
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("AUTH_NOT_AUTHENTICATED", body);
    }

    [Fact]
    public async Task Stats_aggregate_the_user_library()
    {
        var (client, _) = await UserWithEnrichedRatedAlbumAsync();

        var stats = await client.GetFromJsonAsync<LibraryStats>("/api/v1/stats");

        Assert.Equal(1, stats!.Totals.Albums);
        Assert.Equal(1, stats.Totals.Rated);
        Assert.Equal(4m, stats.Totals.AvgRating);
        Assert.Equal(13, stats.Totals.TotalMinutes); // 798 s of music
        Assert.Equal(1990, Assert.Single(stats.Decades).Decade);
        Assert.Contains(stats.TopTags, t => t.Tag == "trip hop");
    }
}
