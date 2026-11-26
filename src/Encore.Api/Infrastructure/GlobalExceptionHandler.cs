using Encore.Core.Domain;
using Grpc.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Polly.CircuitBreaker;

namespace Encore.Api.Infrastructure;

/// <summary>Maps exceptions to RFC 9457 problem details. One place, consistent error shape.</summary>
public sealed class GlobalExceptionHandler(IProblemDetailsService problemDetails, ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title) = exception switch
        {
            DomainException => (StatusCodes.Status400BadRequest, "Business rule violated"),
            // Malformed JSON, wrong content type... the framework already knows the right 4xx.
            BadHttpRequestException bad => (bad.StatusCode, "Bad request"),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "The resource was changed by someone else; reload and retry"),
            BrokenCircuitException => (StatusCodes.Status503ServiceUnavailable, "Upstream service is failing; try again shortly"),
            RpcException { StatusCode: StatusCode.Unavailable or StatusCode.DeadlineExceeded } =>
                (StatusCodes.Status503ServiceUnavailable, "Dependency unavailable"),
            HttpRequestException or TimeoutException => (StatusCodes.Status502BadGateway, "Upstream service error"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error"),
        };

        if (status >= 500) logger.LogError(exception, "Request failed with {Status}", status);
        else logger.LogInformation("Request rejected with {Status}: {Message}", status, exception.Message);

        context.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails =
            {
                Status = status,
                Title = title,
                // Client errors explain themselves; server errors never leak internals.
                Detail = status < 500 ? exception.Message : null,
            },
        });
    }
}
