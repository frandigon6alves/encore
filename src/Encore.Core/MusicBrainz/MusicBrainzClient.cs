using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Encore.Core.MusicBrainz;

public interface IMusicBrainzClient
{
    Task<IReadOnlyList<AlbumSummary>> SearchAlbumsAsync(string query, CancellationToken ct);

    /// <returns>null when MusicBrainz does not know the id (404).</returns>
    Task<AlbumDetails?> GetAlbumAsync(Guid releaseGroupId, CancellationToken ct);

    Task<MbRelease?> GetReleaseWithTracksAsync(Guid releaseId, CancellationToken ct);
}

/// <summary>Typed HttpClient for the public MusicBrainz API (no key, 1 request/second per client).</summary>
public sealed class MusicBrainzClient(HttpClient http) : IMusicBrainzClient
{
    public async Task<IReadOnlyList<AlbumSummary>> SearchAlbumsAsync(string query, CancellationToken ct)
    {
        // Lucene: free words hit MusicBrainz' default fields (title, artist, aliases), so
        // "mezzanine massive attack" ranks the right album first. Special characters are
        // stripped, never passed through: user input must not change the query structure.
        var terms = string.Join(' ', query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string(t.Where(c => char.IsLetterOrDigit(c) || c is '\'' or '-').ToArray()))
            .Where(t => t.Length > 0));
        var lucene = $"({terms}) AND primarytype:album";
        var url = $"release-group?query={Uri.EscapeDataString(lucene)}&limit=10&fmt=json";
        var response = await http.GetFromJsonAsync<MbSearchResponse>(url, ct);
        return response?.ReleaseGroups.Select(MusicBrainzMapper.ToSummary).OfType<AlbumSummary>().ToList() ?? [];
    }

    public async Task<AlbumDetails?> GetAlbumAsync(Guid releaseGroupId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"release-group/{releaseGroupId}?inc=artist-credits+tags+releases&fmt=json", ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return null;
        response.EnsureSuccessStatusCode();

        var rg = await response.Content.ReadFromJsonAsync<MbReleaseGroup>(ct);
        var summary = rg is null ? null : MusicBrainzMapper.ToSummary(rg);
        return summary is null
            ? null
            : new AlbumDetails(summary, MusicBrainzMapper.TopTags(rg!.Tags), MusicBrainzMapper.PickRelease(rg.Releases));
    }

    public async Task<MbRelease?> GetReleaseWithTracksAsync(Guid releaseId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"release/{releaseId}?inc=recordings&fmt=json", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MbRelease>(ct);
    }
}

/// <summary>
/// Outgoing middleware: holds each request until the rate gate opens, so we never exceed
/// MusicBrainz' 1 req/s rule. Sits *inside* the resilience handler, so retries are throttled too.
/// </summary>
public sealed class ClientRateLimitHandler(IRateGate gate) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        return await base.SendAsync(request, ct);
    }
}

public static class MusicBrainzServiceCollectionExtensions
{
    public static IServiceCollection AddMusicBrainz(this IServiceCollection services, IConfiguration config)
    {
        var interval = TimeSpan.FromMilliseconds(1100);
        if (config.GetConnectionString("Redis") is { Length: > 0 } redis)
        {
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis));
            services.AddSingleton<IRateGate>(sp =>
                new RedisRateGate(sp.GetRequiredService<IConnectionMultiplexer>(), "ratelimit:musicbrainz", interval));
        }
        else
        {
            services.AddSingleton<IRateGate>(new LocalRateGate(interval));
        }

        var http = services.AddHttpClient<IMusicBrainzClient, MusicBrainzClient>(client =>
        {
            client.BaseAddress = new Uri(config["MusicBrainz:BaseUrl"] ?? "https://musicbrainz.org/ws/2/");
            // MusicBrainz blocks clients without a meaningful User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd(config["MusicBrainz:UserAgent"] ?? "Encore/0.1 (learning project)");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });

        // Order matters: first added = outermost. Retries/timeouts/circuit breaker wrap the rate limiter.
        http.AddStandardResilienceHandler(o =>
        {
            o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
            o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        });
        http.AddHttpMessageHandler(sp => new ClientRateLimitHandler(sp.GetRequiredService<IRateGate>()));

        return services;
    }
}
