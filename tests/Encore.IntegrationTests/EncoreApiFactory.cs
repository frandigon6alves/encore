using System.Net.Http.Headers;
using System.Net.Http.Json;
using Encore.Api.Auth;
using Encore.Core.Data;
using Encore.Core.MusicBrainz;
using Encore.Worker;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Encore.IntegrationTests;

/// <summary>
/// Boots the real API in-memory against real Postgres and RabbitMQ containers.
/// Only MusicBrainz (a third party we do not control) is faked.
/// </summary>
public sealed class EncoreApiFactory : WebApplicationFactory<Encore.Api.IApiAssemblyMarker>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-alpine").Build();

    public FakeMusicBrainz MusicBrainz { get; } = new();
    public string RabbitConnectionString => _rabbit.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
        builder.UseSetting("ConnectionStrings:Redis", "");
        builder.UseSetting("RabbitMq:Uri", _rabbit.GetConnectionString());
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Services:Recommender", "http://localhost:1"); // nothing listens: exercises the 503 path
        builder.ConfigureTestServices(services => services.AddSingleton<IMusicBrainzClient>(MusicBrainz));
    }

    public async Task<HttpClient> CreateClientForAsync(string username)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new TokenRequest(username));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    public async Task<T> WithDbAsync<T>(Func<EncoreDbContext, Task<T>> action)
    {
        await using var db = await Services.GetRequiredService<IDbContextFactory<EncoreDbContext>>().CreateDbContextAsync();
        return await action(db);
    }

    public AlbumEnricher CreateEnricher() => new(
        Services.GetRequiredService<IDbContextFactory<EncoreDbContext>>(),
        MusicBrainz,
        TimeProvider.System,
        NullLogger<AlbumEnricher>.Instance);

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<EncoreApiFactory>
{
    public const string Name = "api";
}
