using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Encore.Api.Library;
using Encore.Core.Messaging;
using Encore.Recommender.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Encore.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class RecommenderSqlTests(EncoreApiFactory app)
{
    private async Task<(HttpClient Client, Guid AlbumId)> UserWithAlbumAsync(string user, int? rating)
    {
        var client = await app.CreateClientForAsync(user);
        var mbId = Guid.NewGuid();
        var entry = await (await client.PostAsJsonAsync("/api/v1/library", new AddToLibraryRequest(mbId)))
            .Content.ReadFromJsonAsync<LibraryEntryDto>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            });
        await app.CreateEnricher().EnrichAsync(Guid.NewGuid(), new AlbumImportRequested(entry!.AlbumId, mbId), default);
        if (rating is { } r) await client.PutAsJsonAsync($"/api/v1/library/{entry.AlbumId}/rating", new RateRequest(r, null));
        return (client, entry.AlbumId);
    }

    [Fact]
    public async Task Recommends_albums_sharing_tags_with_liked_ones_and_skips_owned()
    {
        var alice = $"alice-{Guid.NewGuid():N}"[..20];
        var (aliceClient, owned) = await UserWithAlbumAsync(alice, rating: 5);
        var (_, candidate) = await UserWithAlbumAsync($"bob-{Guid.NewGuid():N}"[..20], rating: null);

        await using var connection = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        var rows = (await connection.QueryAsync<RecommendationsService.Row>(RecommendationsService.Sql, new { UserId = alice, Limit = 500 })).ToList();

        Assert.Contains(rows, r => r.AlbumId == candidate && r.MatchedTags.Contains("trip hop"));
        Assert.DoesNotContain(rows, r => r.AlbumId == owned);
    }

    [Fact]
    public async Task No_liked_albums_means_no_recommendations()
    {
        var carol = $"carol-{Guid.NewGuid():N}"[..20];
        await UserWithAlbumAsync(carol, rating: 2);

        await using var connection = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        var rows = await connection.QueryAsync<RecommendationsService.Row>(RecommendationsService.Sql, new { UserId = carol, Limit = 10 });

        Assert.Empty(rows);
    }
}
