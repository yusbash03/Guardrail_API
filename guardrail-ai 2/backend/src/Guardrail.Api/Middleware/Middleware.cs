using System.Net;
using System.Security.Claims;
using Guardrail.Api.Http;
using Guardrail.Application.Abstractions;
using Guardrail.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serilog.Context;

namespace Guardrail.Api;

/// <summary>Accepts/echoes X-Correlation-Id and attaches it to every log line (SEC-82).</summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string Header = "X-Correlation-Id";
    public const string ItemKey = "CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[Header].ToString();
        var id = !string.IsNullOrWhiteSpace(incoming) && incoming.Length <= 64 && incoming.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            ? incoming
            : Guid.CreateVersion7().ToString("N");
        context.Items[ItemKey] = id;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[Header] = id;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", id))
        {
            await next(context);
        }
    }
}

/// <summary>Global exception → ProblemDetails mapping (ERROR_HANDLING.md §3). 5xx never include stack traces.</summary>
public sealed class ExceptionToProblemDetails(ILogger<ExceptionToProblemDetails> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        DomainError error;
        switch (exception)
        {
            case DomainException domain:
                error = domain.Error;
                log.LogInformation("Domain rule violation {Code}: {Message}", error.Code, error.Message);
                break;
            case DbUpdateConcurrencyException:
                error = DomainError.InvalidState("The resource was changed concurrently. Reload and try again.");
                log.LogWarning("Concurrency conflict on {Path}", context.Request.Path);
                break;
            case DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg }:
                error = DomainError.InvalidState("The request conflicts with an existing record.");
                log.LogWarning("Unique violation on {Constraint}", pg.ConstraintName);
                break;
            case BadHttpRequestException bad:
                error = DomainError.Validation(bad.InnerException is System.Text.Json.JsonException ? "Malformed JSON body." : bad.Message);
                break;
            case OperationCanceledException when context.RequestAborted.IsCancellationRequested:
                return true;
            default:
                error = new DomainError(ErrorCodes.InternalError, "An unexpected error occurred.");
                log.LogError(exception, "Unhandled exception on {Method} {Path}", context.Request.Method, context.Request.Path);
                break;
        }

        await Problems.Result(error, context).ExecuteAsync(context);
        return true;
    }
}

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? UserId =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirstValue("sub"), out var id) ? id : null;

    public string? CorrelationId => accessor.HttpContext?.Items[CorrelationIdMiddleware.ItemKey] as string;

    public IPAddress? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress;

    public string? UserAgent => accessor.HttpContext?.Request.Headers.UserAgent.ToString();
}

public static class HttpContextExtensions
{
    public static Guid UserId(this HttpContext context) =>
        Guid.Parse(context.User.FindFirstValue("sub") ?? throw new UnauthorizedAccessException());

    public static bool HasWebClientHeader(this HttpContext context) =>
        string.Equals(context.Request.Headers["X-Guardrail-Client"], "web", StringComparison.Ordinal);
}
