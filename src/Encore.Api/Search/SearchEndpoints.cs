using Encore.Core.MusicBrainz;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Hybrid;

namespace Encore.Api.Search;

public static class SearchEndpoints
{
    public const string RateLimitPolicy = "musicbrainz";

    public static RouteGroupBuilder MapSearchEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/search/albums", async Task<Results<Ok<IReadOnlyList<AlbumSummary>>, ValidationProblem>> (
                string? q, IMusicBrainzClient musicBrainz, HybridCache cache, CancellationToken ct) =>
            {
                var query = q?.Trim() ?? "";
                if (query.Length is < 2 or > 100)
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["q"] = ["2 to 100 characters."] });

                // Cache-aside with stampede protection: 50 concurrent requests for the same
                // query run the factory once. L1 = memory, L2 = Redis (when configured).
                var results = await cache.GetOrCreateAsync(
                    // Bump the version when the search logic changes, or Redis keeps serving old results.
                    $"mb:search:v2:{query.ToLowerInvariant()}",
                    async token => await musicBrainz.SearchAlbumsAsync(query, token),
                    new HybridCacheEntryOptions { Expiration = TimeSpan.FromHours(1), LocalCacheExpiration = TimeSpan.FromMinutes(10) },
                    tags: ["musicbrainz"],
                    cancellationToken: ct);

                return TypedResults.Ok(results);
            })
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicy)
            .WithTags("Search")
            .WithSummary("Search albums on MusicBrainz (cached, rate limited)");

        return api;
    }
}
