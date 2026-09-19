using PlateTrace.Core.Jobs;

namespace PlateTrace.Web;

/// <summary>Polls the durable job queue; recovery happens once on startup.</summary>
public sealed class BackgroundJobService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly JobRunner _runner;

    public BackgroundJobService(IServiceProvider services, JobRunner runner)
    {
        _services = services;
        _runner = runner;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = _services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<JobRunner>();
        await runner.RecoverAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runner.PumpAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                // keep the worker alive; the failed job is durable and retried
            }
            try { await Task.Delay(TimeSpan.FromMilliseconds(700), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
