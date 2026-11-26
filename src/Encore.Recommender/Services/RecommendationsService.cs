using Dapper;
using Encore.Recommendations.V1;
using Grpc.Core;
using Npgsql;

namespace Encore.Recommender.Services;

/// <summary>
/// Content-based recommendations in one SQL statement:
/// 1. "taste" = tags of albums the user rated 4-5, weighted by how often they appear;
/// 2. score every enriched catalog album by the taste weights of its tags;
/// 3. drop albums already in the user's library.
/// </summary>
public sealed class RecommendationsService(NpgsqlDataSource dataSource, ILogger<RecommendationsService> logger)
    : Encore.Recommendations.V1.Recommendations.RecommendationsBase
{
    internal const string Sql = """
        WITH liked AS (
            SELECT album_id FROM library_entries WHERE user_id = @UserId AND rating >= 4
        ), taste AS (
            SELECT t.tag, COUNT(*) AS weight
            FROM album_tags t JOIN liked l ON l.album_id = t.album_id
            GROUP BY t.tag
        )
        SELECT a.id                                         AS "AlbumId",
               a.title                                      AS "Title",
               ar.name                                      AS "Artist",
               COALESCE(a.cover_url, '')                    AS "CoverUrl",
               SUM(taste.weight)::float8                    AS "Score",
               array_agg(t.tag ORDER BY taste.weight DESC)  AS "MatchedTags"
        FROM album_tags t
        JOIN taste       ON taste.tag = t.tag
        JOIN albums a    ON a.id = t.album_id AND a.status = 'Enriched'
        JOIN artists ar  ON ar.id = a.artist_id
        WHERE NOT EXISTS (
            SELECT 1 FROM library_entries le WHERE le.user_id = @UserId AND le.album_id = a.id
        )
        GROUP BY a.id, a.title, ar.name, a.cover_url
        ORDER BY "Score" DESC, a.title
        LIMIT @Limit;
        """;

    /// <summary>
    /// Settable properties, not a positional record: Dapper matches constructors by exact CLR
    /// type and Npgsql reports text[] as System.Array, so a string[] constructor parameter never matches.
    /// </summary>
    internal sealed class Row
    {
        public Guid AlbumId { get; init; }
        public string Title { get; init; } = "";
        public string Artist { get; init; } = "";
        public string CoverUrl { get; init; } = "";
        public double Score { get; init; }
        public string[] MatchedTags { get; init; } = [];
    }

    public override async Task<GetForUserResponse> GetForUser(GetForUserRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "user_id is required"));

        var limit = request.Limit is > 0 and <= 50 ? request.Limit : 10;
        await using var connection = await dataSource.OpenConnectionAsync(context.CancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            Sql, new { request.UserId, Limit = limit }, cancellationToken: context.CancellationToken));

        var response = new GetForUserResponse();
        foreach (var r in rows)
        {
            response.Items.Add(new Recommendation
            {
                AlbumId = r.AlbumId.ToString(),
                Title = r.Title,
                Artist = r.Artist,
                CoverUrl = r.CoverUrl,
                Score = r.Score,
                MatchedTags = { r.MatchedTags.Take(5) },
            });
        }

        logger.LogInformation("Returned {Count} recommendations", response.Items.Count);
        return response;
    }
}
