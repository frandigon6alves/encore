using Asp.Versioning;
using Encore.Api.Auth;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Encore.Api.Library;

public static class LibraryEndpoints
{
    public static RouteGroupBuilder MapLibraryEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/library").WithTags("Library").RequireAuthorization();

        // v1: whole library as an array. Kept for old clients.
        group.MapGet("/", async (HttpContext http, LibraryService library, CancellationToken ct) =>
                TypedResults.Ok(await library.ListAllAsync(http.User.GetUserId(), ct)))
            .MapToApiVersion(new ApiVersion(1))
            .WithSummary("List the library (v1: no pagination)");

        // v2: breaking change in the response shape -> new version, v1 untouched.
        group.MapGet("/", async Task<Results<Ok<LibraryPage>, ValidationProblem>> (
                    HttpContext http, LibraryService library, string? cursor, int? limit, CancellationToken ct) =>
                {
                    LibraryCursor? after = null;
                    if (cursor is not null)
                    {
                        if (!LibraryCursor.TryDecode(cursor, out var decoded))
                            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["cursor"] = ["Invalid cursor."] });
                        after = decoded;
                    }
                    return TypedResults.Ok(await library.PageAsync(http.User.GetUserId(), after, Math.Clamp(limit ?? 20, 1, 100), ct));
                })
            .MapToApiVersion(new ApiVersion(2))
            .WithSummary("List the library (v2: cursor pagination)");

        group.MapGet("/{albumId:guid}", async Task<Results<Ok<LibraryEntryDto>, NotFound>> (
                Guid albumId, HttpContext http, LibraryService library, CancellationToken ct) =>
                await library.GetAsync(http.User.GetUserId(), albumId, ct) is { } entry
                    ? TypedResults.Ok(entry)
                    : TypedResults.NotFound())
            .WithName("GetLibraryEntry");

        group.MapPost("/", async Task<Results<Accepted<LibraryEntryDto>, Ok<LibraryEntryDto>, ProblemHttpResult>> (
                AddToLibraryRequest request, HttpContext http, LibraryService library, LinkGenerator links, CancellationToken ct) =>
            {
                var (outcome, entry) = await library.AddAsync(http.User.GetUserId(), request.MbId, ct);
                return outcome switch
                {
                    // 202: the entry exists, enrichment continues in the background. Poll the Location.
                    AddOutcome.Added => TypedResults.Accepted(
                        links.GetPathByName(http, "GetLibraryEntry", new { albumId = entry!.AlbumId }), entry),
                    AddOutcome.AlreadyInLibrary => TypedResults.Ok(entry!),
                    _ => TypedResults.Problem(statusCode: 404, title: "Album not found on MusicBrainz"),
                };
            })
            .WithSummary("Add an album (by MusicBrainz release-group id) to the library");

        group.MapPut("/{albumId:guid}/rating", async Task<Results<Ok<LibraryEntryDto>, NotFound>> (
                Guid albumId, RateRequest request, HttpContext http, LibraryService library, CancellationToken ct) =>
                await library.RateAsync(http.User.GetUserId(), albumId, request, ct) is { } entry
                    ? TypedResults.Ok(entry)
                    : TypedResults.NotFound());

        group.MapDelete("/{albumId:guid}", async Task<Results<NoContent, NotFound>> (
                Guid albumId, HttpContext http, LibraryService library, CancellationToken ct) =>
                await library.RemoveAsync(http.User.GetUserId(), albumId, ct)
                    ? TypedResults.NoContent()
                    : TypedResults.NotFound());

        return api;
    }
}
