using Encore.Core.Data;
using Encore.Core.Domain;
using GreenDonut;
using Microsoft.EntityFrameworkCore;

namespace Encore.Api.GraphQL;

// DataLoaders fix GraphQL's N+1 problem: a query for 50 albums with their artist and
// tracks runs 3 SQL statements (albums, artists IN (...), tracks IN (...)), not 101.
// Each batch opens its own short-lived DbContext because resolvers run in parallel.

public sealed class AlbumByIdDataLoader(IDbContextFactory<EncoreDbContext> dbFactory, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<Guid, Album>(scheduler, options)
{
    protected override async Task<IReadOnlyDictionary<Guid, Album>> LoadBatchAsync(IReadOnlyList<Guid> keys, CancellationToken ct)
    {
        var ids = keys.ToArray();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Albums.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
    }
}

public sealed class ArtistByIdDataLoader(IDbContextFactory<EncoreDbContext> dbFactory, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<Guid, Artist>(scheduler, options)
{
    protected override async Task<IReadOnlyDictionary<Guid, Artist>> LoadBatchAsync(IReadOnlyList<Guid> keys, CancellationToken ct)
    {
        var ids = keys.ToArray();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Artists.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
    }
}

public sealed class TracksByAlbumIdDataLoader(IDbContextFactory<EncoreDbContext> dbFactory, IBatchScheduler scheduler, DataLoaderOptions options)
    : GroupedDataLoader<Guid, Track>(scheduler, options)
{
    protected override async Task<ILookup<Guid, Track>> LoadGroupedBatchAsync(IReadOnlyList<Guid> keys, CancellationToken ct)
    {
        var ids = keys.ToArray();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tracks = await db.Tracks.AsNoTracking().Where(t => ids.Contains(t.AlbumId)).OrderBy(t => t.Position).ToListAsync(ct);
        return tracks.ToLookup(t => t.AlbumId);
    }
}

public sealed class TagsByAlbumIdDataLoader(IDbContextFactory<EncoreDbContext> dbFactory, IBatchScheduler scheduler, DataLoaderOptions options)
    : GroupedDataLoader<Guid, string>(scheduler, options)
{
    protected override async Task<ILookup<Guid, string>> LoadGroupedBatchAsync(IReadOnlyList<Guid> keys, CancellationToken ct)
    {
        var ids = keys.ToArray();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var tags = await db.AlbumTags.AsNoTracking().Where(t => ids.Contains(t.AlbumId)).OrderBy(t => t.Tag).ToListAsync(ct);
        return tags.ToLookup(t => t.AlbumId, t => t.Tag);
    }
}
