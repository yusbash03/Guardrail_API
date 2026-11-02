using Guardrail.Application.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Guardrail.Infrastructure.Jobs;

public sealed class JobsOptions
{
    public const string Section = "Jobs";

    public bool Enabled { get; set; } = true;

    /// <summary>Shared secret for POST /internal/jobs/run (Render Cron backstop).</summary>
    public string? InternalToken { get; set; }

    public int ApprovalExpirySeconds { get; set; } = 60;

    public int CheckoutExpirySeconds { get; set; } = 300;

    public int WebhookProcessorSeconds { get; set; } = 5;

    public int ReconcilerSeconds { get; set; } = 600;
}

/// <summary>In-process IHostedService running one JobRunner step on an interval (ARCHITECTURE.md §6).</summary>
public abstract class PeriodicJob(IServiceScopeFactory scopes, IOptions<JobsOptions> options, ILogger logger) : BackgroundService
{
    protected abstract TimeSpan Interval(JobsOptions options);

    protected abstract Task<int> RunOnceAsync(JobRunner runner, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval(options.Value));
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var count = await RunOnceAsync(scope.ServiceProvider.GetRequiredService<JobRunner>(), stoppingToken);
                    if (count > 0)
                    {
                        logger.LogInformation("{Job} handled {Count} item(s)", GetType().Name, count);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "{Job} failed", GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Host shutting down (including a failed startup, e.g. port in use): not an error, and it must not mask the real cause.
        }
    }
}

public sealed class ApprovalExpirySweeper(IServiceScopeFactory scopes, IOptions<JobsOptions> options, ILogger<ApprovalExpirySweeper> logger)
    : PeriodicJob(scopes, options, logger)
{
    protected override TimeSpan Interval(JobsOptions o) => TimeSpan.FromSeconds(o.ApprovalExpirySeconds);

    protected override Task<int> RunOnceAsync(JobRunner runner, CancellationToken ct) => runner.ExpireApprovalsAsync(ct);
}

public sealed class CheckoutExpirySweeper(IServiceScopeFactory scopes, IOptions<JobsOptions> options, ILogger<CheckoutExpirySweeper> logger)
    : PeriodicJob(scopes, options, logger)
{
    protected override TimeSpan Interval(JobsOptions o) => TimeSpan.FromSeconds(o.CheckoutExpirySeconds);

    protected override Task<int> RunOnceAsync(JobRunner runner, CancellationToken ct) => runner.ExpireCheckoutsAsync(ct);
}

public sealed class WebhookProcessor(IServiceScopeFactory scopes, IOptions<JobsOptions> options, ILogger<WebhookProcessor> logger)
    : PeriodicJob(scopes, options, logger)
{
    protected override TimeSpan Interval(JobsOptions o) => TimeSpan.FromSeconds(o.WebhookProcessorSeconds);

    protected override Task<int> RunOnceAsync(JobRunner runner, CancellationToken ct) => runner.ProcessWebhooksAsync(ct);
}

public sealed class PaymentReconciler(IServiceScopeFactory scopes, IOptions<JobsOptions> options, ILogger<PaymentReconciler> logger)
    : PeriodicJob(scopes, options, logger)
{
    protected override TimeSpan Interval(JobsOptions o) => TimeSpan.FromSeconds(o.ReconcilerSeconds);

    protected override Task<int> RunOnceAsync(JobRunner runner, CancellationToken ct) => runner.ReconcileAsync(ct);
}
