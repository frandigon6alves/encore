using System.Diagnostics;
using System.Text;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace Encore.Core.Messaging;

/// <summary>
/// Carries the W3C trace context (traceparent/tracestate) inside AMQP headers so one trace
/// spans API request -> outbox -> RabbitMQ -> worker.
/// </summary>
public static class TraceContextHeaders
{
    private static readonly TextMapPropagator Propagator =
        new CompositeTextMapPropagator([new TraceContextPropagator(), new BaggagePropagator()]);

    public static void Inject(ActivityContext context, IDictionary<string, object?> headers) =>
        Propagator.Inject(new PropagationContext(context, Baggage.Current), headers,
            static (carrier, key, value) => carrier[key] = value);

    public static ActivityContext Extract(IDictionary<string, object?>? headers)
    {
        if (headers is null) return default;
        var ctx = Propagator.Extract(default, headers, static (carrier, key) =>
            carrier.TryGetValue(key, out var raw) && ReadString(raw) is { } value ? [value] : []);
        Baggage.Current = ctx.Baggage;
        return ctx.ActivityContext;
    }

    /// <summary>AMQP delivers string headers as byte[].</summary>
    public static string? ReadString(object? raw) => raw switch
    {
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        string s => s,
        _ => raw?.ToString(),
    };
}
