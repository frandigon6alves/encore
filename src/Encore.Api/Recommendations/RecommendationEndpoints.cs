using Encore.Api.Auth;
using Encore.Recommendations.V1;
using Grpc.Core;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Encore.Api.Recommendations;

public sealed record RecommendationDto(Guid AlbumId, string Title, string Artist, string CoverUrl, double Score, IReadOnlyList<string> MatchedTags);

public static class RecommendationEndpoints
{
    public static RouteGroupBuilder MapRecommendationEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/recommendations", async Task<Results<Ok<IReadOnlyList<RecommendationDto>>, ProblemHttpResult>> (
                HttpContext http, Encore.Recommendations.V1.Recommendations.RecommendationsClient client, int? limit, CancellationToken ct) =>
            {
                try
                {
                    // Deadline: the API never waits longer than this for the internal service.
                    var response = await client.GetForUserAsync(
                        new GetForUserRequest { UserId = http.User.GetUserId(), Limit = Math.Clamp(limit ?? 10, 1, 50) },
                        deadline: DateTime.UtcNow.AddSeconds(3),
                        cancellationToken: ct);

                    IReadOnlyList<RecommendationDto> items = response.Items
                        .Select(r => new RecommendationDto(Guid.Parse(r.AlbumId), r.Title, r.Artist, r.CoverUrl, r.Score, r.MatchedTags.ToList()))
                        .ToList();
                    return TypedResults.Ok(items);
                }
                catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
                {
                    // Graceful degradation: the rest of the app keeps working without the recommender.
                    return TypedResults.Problem(statusCode: 503, title: "Recommendations are temporarily unavailable");
                }
            })
            .RequireAuthorization()
            .WithTags("Recommendations")
            .WithSummary("Albums you may like (served by the gRPC recommender)");

        return api;
    }
}
