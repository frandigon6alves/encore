using System.Threading.RateLimiting;
using StackExchange.Redis;

namespace Encore.Core.MusicBrainz;

/// <summary>Waits until the caller may send one more request to a rate-limited upstream.</summary>
public interface IRateGate
{
    ValueTask WaitAsync(CancellationToken ct);
}

/// <summary>Per-process token bucket. Correct only while a single process talks to the upstream.</summary>
public sealed class LocalRateGate(TimeSpan interval) : IRateGate, IDisposable
{
    private readonly TokenBucketRateLimiter _limiter = new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = 1,
        TokensPerPeriod = 1,
        ReplenishmentPeriod = interval,
        QueueLimit = 100,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    });

    public async ValueTask WaitAsync(CancellationToken ct)
    {
        using var lease = await _limiter.AcquireAsync(1, ct);
        if (!lease.IsAcquired) throw new HttpRequestException("Local rate limit queue is full.");
    }

    public void Dispose() => _limiter.Dispose();
}

/// <summary>
/// Cluster-wide gate: MusicBrainz limits by IP, and the API plus N workers share one IP.
/// Each request must win "SET key NX PX interval"; losers sleep for the key's remaining TTL.
/// One Redis round trip per slot, no Lua, good enough for a 1 req/s budget.
/// </summary>
public sealed class RedisRateGate(IConnectionMultiplexer redis, string key, TimeSpan interval) : IRateGate
{
    public async ValueTask WaitAsync(CancellationToken ct)
    {
        var db = redis.GetDatabase();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await db.StringSetAsync(key, Environment.MachineName, interval, When.NotExists)) return;

            var ttl = await db.KeyTimeToLiveAsync(key) ?? TimeSpan.FromMilliseconds(50);
            // Jitter so waiting processes do not all retry on the same millisecond.
            await Task.Delay(ttl + TimeSpan.FromMilliseconds(Random.Shared.Next(5, 40)), ct);
        }
    }
}
