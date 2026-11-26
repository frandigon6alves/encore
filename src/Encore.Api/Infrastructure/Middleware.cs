using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Encore.Api.Infrastructure;

/// <summary>
/// Accepts X-Correlation-Id from the caller (or uses the trace id), echoes it back,
/// and adds it to every log line and the current span. Lets support match a user report
/// ("request abc123 failed") to logs and traces.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var correlationId = SafeId().IsMatch(incoming)
            ? incoming
            : Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

        context.Response.Headers[HeaderName] = correlationId;
        Activity.Current?.SetTag("correlation.id", correlationId);

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

    // Never trust header input blindly: it lands in logs (log injection) and responses.
    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex SafeId();
}

/// <summary>
/// Adds a Server-Timing header (visible in the browser DevTools Network tab): quick
/// latency profiling without opening the tracing UI.
/// </summary>
public sealed class ServerTimingMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var start = Stopwatch.GetTimestamp();
        context.Response.OnStarting(() =>
        {
            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context.Response.Headers.Append("Server-Timing", string.Create(CultureInfo.InvariantCulture, $"app;dur={ms:F1}"));
            return Task.CompletedTask;
        });
        return next(context);
    }
}
