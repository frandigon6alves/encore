using System.Security.Claims;
using Encore.Api.Auth;
using Encore.Core.Data;
using Encore.Core.Domain;
using HotChocolate;
using HotChocolate.Authorization;
using HotChocolate.Types;
using Microsoft.EntityFrameworkCore;

namespace Encore.Api.GraphQL;

/// <summary>
/// Read side of the API as a graph. Clients pick exactly the fields they need
/// (e.g. the library grid skips tracks, the album page asks for them).
/// </summary>
public sealed class Query
{
    [Authorize]
    public async Task<IReadOnlyList<LibraryEntry>> GetMyLibrary(
        ClaimsPrincipal user, [Service] IDbContextFactory<EncoreDbContext> dbFactory, CancellationToken ct)
    {
        var userId = user.GetUserId();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.LibraryEntries.AsNoTracking()
            .Where(e => e.UserId == userId)
            .OrderByDescending(e => e.AddedAt)
            .ToListAsync(ct);
    }

    public Task<Album?> GetAlbum(Guid id, AlbumByIdDataLoader albums, CancellationToken ct) =>
        albums.LoadAsync(id, ct);

    public async Task<IReadOnlyList<Album>> GetAlbumsByTag(
        string tag, [Service] IDbContextFactory<EncoreDbContext> dbFactory, CancellationToken ct)
    {
        var normalized = tag.Trim().ToLowerInvariant();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Albums.AsNoTracking()
            .Where(a => a.Tags.Any(t => t.Tag == normalized))
            .OrderBy(a => a.ReleaseYear)
            .Take(50)
            .ToListAsync(ct);
    }
}

/// <summary>
/// Explicit schema for domain entities: hide internals (user id, xmin version) and resolve
/// relations through DataLoaders instead of EF navigation properties.
/// </summary>
public sealed class LibraryEntryType : ObjectType<LibraryEntry>
{
    protected override void Configure(IObjectTypeDescriptor<LibraryEntry> d)
    {
        d.Ignore(e => e.UserId);
        d.Field(e => e.Album).ResolveWith<Resolvers>(r => r.GetAlbum(default!, default!, default));
    }

    private sealed class Resolvers
    {
        public Task<Album?> GetAlbum([Parent] LibraryEntry entry, AlbumByIdDataLoader albums, CancellationToken ct) =>
            albums.LoadAsync(entry.AlbumId, ct);
    }
}

public sealed class AlbumType : ObjectType<Album>
{
    protected override void Configure(IObjectTypeDescriptor<Album> d)
    {
        d.Ignore(a => a.Version);
        d.Field(a => a.Artist).ResolveWith<Resolvers>(r => r.GetArtist(default!, default!, default));
        d.Field(a => a.Tracks).ResolveWith<Resolvers>(r => r.GetTracks(default!, default!, default));
        d.Field(a => a.Tags)
            .Type<NonNullType<ListType<NonNullType<StringType>>>>()
            .ResolveWith<Resolvers>(r => r.GetTags(default!, default!, default));
        d.Field("totalLengthMs")
            .Description("Computed; reuses the tracks batch, so it costs no extra query.")
            .ResolveWith<Resolvers>(r => r.GetTotalLengthMs(default!, default!, default));
    }

    private sealed class Resolvers
    {
        public Task<Artist?> GetArtist([Parent] Album album, ArtistByIdDataLoader artists, CancellationToken ct) =>
            artists.LoadAsync(album.ArtistId, ct);

        public async Task<Track[]> GetTracks([Parent] Album album, TracksByAlbumIdDataLoader tracks, CancellationToken ct) =>
            await tracks.LoadAsync(album.Id, ct) ?? [];

        public async Task<string[]> GetTags([Parent] Album album, TagsByAlbumIdDataLoader tags, CancellationToken ct) =>
            await tags.LoadAsync(album.Id, ct) ?? [];

        public async Task<int> GetTotalLengthMs([Parent] Album album, TracksByAlbumIdDataLoader tracks, CancellationToken ct) =>
            (await tracks.LoadAsync(album.Id, ct) ?? []).Sum(t => t.LengthMs ?? 0);
    }
}
