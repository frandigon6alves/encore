using Encore.Core.Data;
using Encore.Core.Messaging;
using Encore.Core.MusicBrainz;
using Encore.Core.Telemetry;
using Encore.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddEncoreTelemetry("encore-worker");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddEncoreData(builder.Configuration);
builder.Services.AddMusicBrainz(builder.Configuration);
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.AddSingleton<RabbitMqConnection>();
builder.Services.AddSingleton<AlbumEnricher>();
builder.Services.AddHostedService<AlbumImportConsumer>();

// Give in-flight messages time to finish on SIGTERM (docker stop, k8s rollout).
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));

builder.Build().Run();
