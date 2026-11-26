using Encore.Core.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Encore.Api.Infrastructure;

/// <summary>
/// Degraded, not Unhealthy: thanks to the outbox the API still accepts writes while
/// RabbitMQ is down; events wait in Postgres and go out when the broker is back.
/// </summary>
public sealed class RabbitMqHealthCheck(RabbitMqConnection rabbit) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct) =>
        Task.FromResult(rabbit.IsOpen
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded("Not connected to RabbitMQ; outbox is buffering events."));
}
