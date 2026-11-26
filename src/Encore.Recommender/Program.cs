using Encore.Core.Data;
using Encore.Core.Telemetry;
using Encore.Recommender.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddEncoreTelemetry("encore-recommender");
builder.Services.AddEncoreData(builder.Configuration);
builder.Services.AddGrpc(o => o.EnableDetailedErrors = builder.Environment.IsDevelopment());

var app = builder.Build();

app.MapGrpcService<RecommendationsService>();
app.MapGet("/", () => "Encore.Recommender speaks gRPC (HTTP/2). See protos/recommendations.proto.");

app.Run();
