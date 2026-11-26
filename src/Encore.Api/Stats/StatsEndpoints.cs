using Dapper;
using Encore.Api.Auth;
using Npgsql;

namespace Encore.Api.Stats;

public sealed record StatsTotals(long Albums, long Rated, decimal? AvgRating, long TotalMinutes);
public sealed record DecadeStat(int Decade, long Albums, decimal? AvgRating);
public sealed record TagStat(string Tag, long Albums, decimal? AvgRating);
public sealed record LibraryStats(StatsTotals Totals, IReadOnlyList<DecadeStat> Decades, IReadOnlyList<TagStat> TopTags);

/// <summary>
/// Read-only reporting query. Dapper + hand-written SQL instead of EF Core: aggregates,
/// LATERAL joins and exact control of the plan (check it with EXPLAIN ANALYZE, see docs).
/// Three result sets, one round trip.
/// </summary>
public static class StatsEndpoints
{
    internal const string Sql = """
        SELECT COUNT(*)                                   AS "Albums",
               COUNT(le.rating)                           AS "Rated",
               ROUND(AVG(le.rating), 2)                   AS "AvgRating",
               COALESCE(SUM(t.total_ms), 0)::bigint / 60000 AS "TotalMinutes"
        FROM library_entries le
        LEFT JOIN LATERAL (SELECT SUM(length_ms) AS total_ms FROM tracks WHERE album_id = le.album_id) t ON TRUE
        WHERE le.user_id = @UserId;

        SELECT (a.release_year / 10 * 10)::int AS "Decade",
               COUNT(*)                        AS "Albums",
               ROUND(AVG(le.rating), 2)        AS "AvgRating"
        FROM library_entries le
        JOIN albums a ON a.id = le.album_id
        WHERE le.user_id = @UserId AND a.release_year IS NOT NULL
        GROUP BY 1
        ORDER BY 1;

        SELECT at.tag                   AS "Tag",
               COUNT(*)                 AS "Albums",
               ROUND(AVG(le.rating), 2) AS "AvgRating"
        FROM library_entries le
        JOIN album_tags at ON at.album_id = le.album_id
        WHERE le.user_id = @UserId
        GROUP BY at.tag
        ORDER BY COUNT(*) DESC, AVG(le.rating) DESC NULLS LAST, at.tag
        LIMIT 10;
        """;

    public static RouteGroupBuilder MapStatsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/stats", async (HttpContext http, NpgsqlDataSource dataSource, CancellationToken ct) =>
            {
                await using var connection = await dataSource.OpenConnectionAsync(ct);
                await using var grid = await connection.QueryMultipleAsync(
                    new CommandDefinition(Sql, new { UserId = http.User.GetUserId() }, cancellationToken: ct));

                var totals = await grid.ReadSingleAsync<StatsTotals>();
                var decades = (await grid.ReadAsync<DecadeStat>()).ToList();
                var tags = (await grid.ReadAsync<TagStat>()).ToList();
                return TypedResults.Ok(new LibraryStats(totals, decades, tags));
            })
            .RequireAuthorization()
            .WithTags("Stats")
            .WithSummary("Listening stats for the current user");

        return api;
    }
}
