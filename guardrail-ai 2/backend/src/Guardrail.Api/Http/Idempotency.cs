using System.Security.Cryptography;
using System.Text;
using Guardrail.Application.Abstractions;
using Guardrail.Application.Common;
using Guardrail.Domain.Audit;
using Guardrail.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Guardrail.Api.Http;

/// <summary>
/// Idempotency-Key handling for 🔁 endpoints (API_CONTRACT.md): same key + same body → stored response replayed;
/// same key + different body → 422 IDEMPOTENCY_KEY_REUSED; key still running → 409 IDEMPOTENCY_IN_PROGRESS.
/// 5xx and 202 outcomes are not stored so the client can retry with the same key.
/// </summary>
public sealed class Idempotency(IAppDbContext db, TimeProvider clock, ILogger<Idempotency> log)
{
    public const string Header = "Idempotency-Key";
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    public async Task<IResult> ExecuteAsync(HttpContext context, object? request, Func<Task<ApiResponse>> action)
    {
        var key = context.Request.Headers[Header].ToString();
        if (key.Length is < 8 or > 100)
        {
            return Problems.Result(DomainError.Validation("Idempotency-Key header (8–100 chars) is required.",
                new Dictionary<string, string[]> { [Header] = ["Required, 8–100 characters."] }), context);
        }

        var userId = context.UserId();
        var path = context.Request.Path.Value!;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{context.Request.Method} {path}\n{(request is null ? string.Empty : JsonDefaults.Serialize(request))}")));
        var now = clock.GetUtcNow();
        var cancellationToken = context.RequestAborted;

        var existing = await db.IdempotencyKeys.AsNoTracking()
            .SingleOrDefaultAsync(k => k.UserId == userId && k.Key == key && k.RequestPath == path, cancellationToken);
        if (existing is not null && existing.ExpiresAt <= now)
        {
            await db.IdempotencyKeys.Where(k => k.Id == existing.Id).ExecuteDeleteAsync(cancellationToken);
            existing = null;
        }

        if (existing is not null)
        {
            if (existing.RequestHash != hash)
            {
                return Problems.Result(new DomainError(ErrorCodes.IdempotencyKeyReused, "This Idempotency-Key was used with a different request."), context);
            }

            if (existing.ResponseStatus is not { } status)
            {
                return Problems.Result(new DomainError(ErrorCodes.IdempotencyInProgress, "A request with this Idempotency-Key is still running."), context);
            }

            context.Response.Headers["Idempotent-Replayed"] = "true";
            return Results.Content(existing.ResponseBody ?? "null", status >= 400 ? "application/problem+json" : "application/json",
                Encoding.UTF8, status);
        }

        var row = IdempotencyKey.Begin(userId, key, path, hash, now, Ttl);
        db.IdempotencyKeys.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ClearTracking();
            return Problems.Result(new DomainError(ErrorCodes.IdempotencyInProgress, "A request with this Idempotency-Key is still running."), context);
        }

        ApiResponse response;
        try
        {
            response = await action();
        }
        catch
        {
            await ForgetAsync(row.Id);
            throw;
        }

        if (response.Status >= 500 || response.Status == StatusCodes.Status202Accepted)
        {
            await ForgetAsync(row.Id);
        }
        else
        {
            var body = JsonDefaults.Serialize(response.Body);
            await db.IdempotencyKeys.Where(k => k.Id == row.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.ResponseStatus, response.Status).SetProperty(k => k.ResponseBody, body), CancellationToken.None);
        }

        return response.ToResult();
    }

    private async Task ForgetAsync(Guid id)
    {
        try
        {
            await db.IdempotencyKeys.Where(k => k.Id == id).ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not release idempotency key {Id}", id);
        }
    }
}
