using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Encore.Api.Library;
using Encore.Core.Domain;
using Encore.Core.Messaging;
using Encore.Worker;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;

namespace Encore.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class LibraryApiTests(EncoreApiFactory app)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static string NewUser() => $"user-{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task Requests_without_a_token_get_401()
    {
        var response = await app.CreateClient().GetAsync("/api/v1/library");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Add_album_end_to_end_outbox_rabbitmq_worker()
    {
        var client = await app.CreateClientForAsync(NewUser());
        var mbId = Guid.NewGuid();

        // 1. API accepts and returns a pending entry plus a Location to poll.
        var response = await client.PostAsJsonAsync("/api/v1/library", new AddToLibraryRequest(mbId));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var entry = await response.Content.ReadFromJsonAsync<LibraryEntryDto>(Json);
        Assert.Equal(AlbumStatus.Pending, entry!.Status);

        // 2. The outbox publisher (a BackgroundService in the API) publishes the event.
        var albumId = entry.AlbumId.ToString();
        var outboxId = (await Eventually(() => app.WithDbAsync(db => db.OutboxMessages
            .FromSql($"SELECT * FROM outbox_messages WHERE payload->>'AlbumId' = {albumId} AND processed_at IS NOT NULL")
            .Select(m => (Guid?)m.Id).SingleOrDefaultAsync())))!.Value;

        // 3. The message is in the worker queue, with the trace context header.
        var delivered = await GetFromQueueAsync(entry.AlbumId);
        Assert.Equal(outboxId.ToString(), delivered.BasicProperties.MessageId);
        Assert.True(delivered.BasicProperties.Headers!.ContainsKey("traceparent"));

        // 4. The worker logic enriches the album; a redelivery is a no-op (idempotent consumer).
        var message = JsonSerializer.Deserialize<AlbumImportRequested>(delivered.Body.Span)!;
        var enricher = app.CreateEnricher();
        Assert.Equal(EnrichResult.Enriched, await enricher.EnrichAsync(outboxId, message, default));
        Assert.Equal(EnrichResult.AlreadyProcessed, await enricher.EnrichAsync(outboxId, message, default));

        var tracks = await app.WithDbAsync(db => db.Tracks.CountAsync(t => t.AlbumId == entry.AlbumId));
        Assert.Equal(3, tracks);

        // 5. Polling the Location now shows the enriched album.
        var polled = await client.GetFromJsonAsync<LibraryEntryDto>(response.Headers.Location, Json);
        Assert.Equal(AlbumStatus.Enriched, polled!.Status);
    }

    [Fact]
    public async Task Adding_the_same_album_twice_is_idempotent()
    {
        var client = await app.CreateClientForAsync(NewUser());
        var request = new AddToLibraryRequest(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/v1/library", request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/library", request)).StatusCode);
    }

    [Fact]
    public async Task Concurrent_adds_of_the_same_album_create_one_catalog_row()
    {
        var mbId = Guid.NewGuid();
        var clients = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => app.CreateClientForAsync(NewUser())));

        var responses = await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync("/api/v1/library", new AddToLibraryRequest(mbId))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Assert.Equal(1, await app.WithDbAsync(db => db.Albums.CountAsync(a => a.MbId == mbId)));
        Assert.Equal(5, await app.WithDbAsync(db => db.LibraryEntries.CountAsync(e => e.Album.MbId == mbId)));
    }

    [Fact]
    public async Task Unknown_album_returns_404_problem()
    {
        var client = await app.CreateClientForAsync(NewUser());
        var response = await client.PostAsJsonAsync("/api/v1/library", new AddToLibraryRequest(FakeMusicBrainz.UnknownAlbum));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Rating_is_validated()
    {
        var client = await app.CreateClientForAsync(NewUser());
        var added = await (await client.PostAsJsonAsync("/api/v1/library", new AddToLibraryRequest(Guid.NewGuid())))
            .Content.ReadFromJsonAsync<LibraryEntryDto>(Json);

        var invalid = await client.PutAsJsonAsync($"/api/v1/library/{added!.AlbumId}/rating", new RateRequest(9, null));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var valid = await client.PutAsJsonAsync($"/api/v1/library/{added.AlbumId}/rating", new RateRequest(5, "Classic"));
        var rated = await valid.Content.ReadFromJsonAsync<LibraryEntryDto>(Json);
        Assert.Equal(5, rated!.Rating);
    }

    [Fact]
    public async Task V2_paginates_with_a_cursor()
    {
        var client = await app.CreateClientForAsync(NewUser());
        for (var i = 0; i < 3; i++)
            await client.PostAsJsonAsync("/api/v2/library", new AddToLibraryRequest(Guid.NewGuid()));

        var first = await client.GetFromJsonAsync<LibraryPage>("/api/v2/library?limit=2", Json);
        Assert.Equal(2, first!.Items.Count);
        Assert.NotNull(first.NextCursor);

        var second = await client.GetFromJsonAsync<LibraryPage>($"/api/v2/library?limit=2&cursor={first.NextCursor}", Json);
        Assert.Single(second!.Items);
        Assert.Null(second.NextCursor);
        Assert.Empty(first.Items.Select(e => e.AlbumId).Intersect(second.Items.Select(e => e.AlbumId)));

        // v1 keeps its old contract: a plain array.
        var v1 = await client.GetFromJsonAsync<List<LibraryEntryDto>>("/api/v1/library", Json);
        Assert.Equal(3, v1!.Count);
    }

    [Fact]
    public async Task Responses_carry_correlation_id_and_server_timing()
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-Id", "test-correlation-42");

        var response = await client.GetAsync("/health/live");

        Assert.Equal("test-correlation-42", response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.StartsWith("app;dur=", response.Headers.GetValues("Server-Timing").Single());
    }

    [Fact]
    public async Task Recommender_down_degrades_to_503()
    {
        var client = await app.CreateClientForAsync(NewUser());
        var response = await client.GetAsync("/api/v1/recommendations");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private async Task<BasicGetResult> GetFromQueueAsync(Guid albumId)
    {
        var factory = new ConnectionFactory { Uri = new Uri(app.RabbitConnectionString) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        // Other tests publish too: skip their messages (and leave them unacked -> requeued).
        return await Eventually(async () =>
        {
            for (var i = 0; i < 50; i++)
            {
                var result = await channel.BasicGetAsync(Topology.AlbumImportQueue, autoAck: false);
                if (result is null) return null;
                if (JsonSerializer.Deserialize<AlbumImportRequested>(result.Body.Span)!.AlbumId == albumId)
                {
                    await channel.BasicAckAsync(result.DeliveryTag, false);
                    return result;
                }
            }
            return null;
        });
    }

    private static async Task<T> Eventually<T>(Func<Task<T?>> probe, int timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() is { } value) return value;
            await Task.Delay(200);
        }
        throw new TimeoutException("Condition not met in time.");
    }
}
