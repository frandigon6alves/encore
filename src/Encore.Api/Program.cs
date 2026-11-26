using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Encore.Api.Auth;
using Encore.Api.GraphQL;
using Encore.Api.Infrastructure;
using Encore.Api.Library;
using Encore.Api.Recommendations;
using Encore.Api.Search;
using Encore.Api.Stats;
using Encore.Core.Data;
using Encore.Core.Messaging;
using Encore.Core.MusicBrainz;
using Encore.Core.Telemetry;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---- Observability -------------------------------------------------------------
builder.AddEncoreTelemetry("encore-api");
builder.Services.ConfigureOpenTelemetryTracerProvider(t => t.AddHotChocolateInstrumentation());

// ---- Data, integrations, messaging ---------------------------------------------
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddEncoreData(config);
builder.Services.AddMusicBrainz(config);
builder.Services.Configure<RabbitMqOptions>(config.GetSection("RabbitMq"));
builder.Services.AddSingleton<RabbitMqConnection>();
if (config.GetValue("Outbox:Enabled", true))
{
    builder.Services.AddHostedService<OutboxPublisher>();
}

builder.Services.AddScoped<LibraryService>();

builder.Services.AddGrpcClient<Encore.Recommendations.V1.Recommendations.RecommendationsClient>(o =>
    o.Address = new Uri(config["Services:Recommender"] ?? "http://localhost:5081"));

// ---- Caching: HybridCache = in-memory L1 + optional Redis L2 ----------------------
if (config.GetConnectionString("Redis") is { Length: > 0 } redis)
{
    builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redis);
}
builder.Services.AddHybridCache();

// ---- Security ---------------------------------------------------------------------
builder.Services.Configure<JwtOptions>(config.GetSection("Jwt"));
var jwt = config.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (jwt.SigningKey.Length < 32) throw new InvalidOperationException("Jwt:SigningKey must be at least 32 characters.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false; // keep "sub" as "sub"
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = jwt.Key,
            NameClaimType = "sub",
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Protects our MusicBrainz quota: each user gets 20 searches per minute.
    o.AddPolicy(SearchEndpoints.RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.Identity?.Name ?? http.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
});

// ---- HTTP API: validation, errors, versioning, OpenAPI -----------------------------
// .NET 10: DataAnnotations on minimal API parameters. Fully qualified: HotChocolate also has an AddValidation.
ValidationServiceCollectionExtensions.AddValidation(builder.Services);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
    ctx.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? ctx.HttpContext.TraceIdentifier);
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddApiVersioning(o =>
    {
        o.DefaultApiVersion = new ApiVersion(1);
        o.ReportApiVersions = true; // api-supported-versions response header
        o.ApiVersionReader = new UrlSegmentApiVersionReader();
    })
    .AddApiExplorer(o =>
    {
        o.GroupNameFormat = "'v'V";
        o.SubstituteApiVersionInUrl = true;
    })
    // One OpenAPI document per API version (/openapi/v1.json, /openapi/v2.json).
    .AddOpenApi(o => o.Document.AddDocumentTransformer<BearerSecuritySchemeTransformer>());

// ---- GraphQL --------------------------------------------------------------------------
builder.Services.AddGraphQLServer()
    .AddAuthorization()
    .AddQueryType<Query>()
    .AddType<LibraryEntryType>()
    .AddType<AlbumType>()
    .AddDataLoader<AlbumByIdDataLoader>()
    .AddDataLoader<ArtistByIdDataLoader>()
    .AddDataLoader<TracksByAlbumIdDataLoader>()
    .AddDataLoader<TagsByAlbumIdDataLoader>()
    .AddMaxExecutionDepthRule(8)
    .AddInstrumentation()
    .ModifyRequestOptions(o => o.IncludeExceptionDetails = builder.Environment.IsDevelopment());

// ---- Health ---------------------------------------------------------------------------
builder.Services.AddHealthChecks()
    .AddNpgSql(config.GetConnectionString("Postgres")!, tags: ["ready"])
    .AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: ["ready"]);

var app = builder.Build();

// ---- Pipeline (order matters) ------------------------------------------------------------
app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ServerTimingMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().WithDocumentPerVersion();
    app.MapScalarApiReference(o => o.AddDocument("v1").AddDocument("v2"));
}

if (config.GetValue("Database:MigrateOnStartup", false))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<EncoreDbContext>().Database.MigrateAsync();
}

var api = app.NewVersionedApi("Encore")
    .MapGroup("/api/v{version:apiVersion}")
    .HasApiVersion(new ApiVersion(1))
    .HasApiVersion(new ApiVersion(2));

api.MapAuthEndpoints()
    .MapSearchEndpoints()
    .MapLibraryEndpoints()
    .MapStatsEndpoints()
    .MapRecommendationEndpoints();

app.MapGraphQL();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.Run();

