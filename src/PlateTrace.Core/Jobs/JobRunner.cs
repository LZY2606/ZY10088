using System.Text.Json;
using PlateTrace.Core.Engine;
using PlateTrace.Core.Models;
using PlateTrace.Core.Store;

namespace PlateTrace.Core.Jobs;

/// <summary>
/// Durable background computation. Jobs persist as Pending/Running/Completed.
/// A crash leaves a job in Running; <see cref="RecoverAsync"/> reclaims it on
/// startup, and a job that already carries a result is never recorded twice.
/// </summary>
public sealed class JobRunner
{
    private readonly ITraceStore _store;
    private readonly Func<TraceService> _serviceFactory;

    public JobRunner(ITraceStore store, Func<TraceService> serviceFactory)
    {
        _store = store;
        _serviceFactory = serviceFactory;
    }

    public async Task<JobRecord> EnqueueEvaluationAsync(HypothesisRecord hypothesis, CancellationToken ct = default)
    {
        var key = $"eval:{hypothesis.HypothesisId}:v{hypothesis.Version}";
        var existing = await _store.FindJobByIdempotencyAsync(key, ct).ConfigureAwait(false);
        if (existing is not null) return existing; // stable retry: same job, never recomputed twice

        var payload = JsonSerializer.Serialize(new { hypothesisId = hypothesis.HypothesisId, version = hypothesis.Version },
            PlateTraceJson.Options);
        var job = new JobRecord
        {
            JobId = $"J{Guid.NewGuid().ToString("N")[..12]}",
            Kind = JobKind.EvaluateHypothesis,
            State = JobState.Pending,
            IdempotencyKey = key,
            PayloadJson = payload,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _store.UpsertJobAsync(job, ct).ConfigureAwait(false);
        return job;
    }

    /// <summary>Reclaim stale Running jobs after a process restart.</summary>
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        var jobs = await _store.ListJobsAsync(ct).ConfigureAwait(false);
        foreach (var job in jobs.Where(j => j.State == JobState.Running && !j.HasResult))
        {
            job.State = JobState.Pending;
            job.Attempts += 1;
            job.Error = $"recovered after interruption; attempt {job.Attempts}";
            await _store.UpsertJobAsync(job, ct).ConfigureAwait(false);
        }
    }

    public async Task<int> PumpAsync(CancellationToken ct = default)
    {
        var service = _serviceFactory();
        var jobs = (await _store.ListJobsAsync(ct).ConfigureAwait(false))
            .Where(j => j.State is JobState.Pending or JobState.Running)
            .OrderBy(j => j.CreatedAt)
            .ToList();

        var processed = 0;
        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            if (job.HasResult && job.State == JobState.Completed) continue;
            await RunOneAsync(service, job, ct).ConfigureAwait(false);
            processed++;
        }
        return processed;
    }

    private async Task RunOneAsync(TraceService service, JobRecord job, CancellationToken ct)
    {
        job.State = JobState.Running;
        job.StartedAt ??= DateTimeOffset.UtcNow;
        job.Attempts += 1;
        await _store.UpsertJobAsync(job, ct).ConfigureAwait(false);

        try
        {
            if (job.Kind != JobKind.EvaluateHypothesis)
                throw new InvalidOperationException($"unsupported job kind {job.Kind}");

            var payload = JsonDocument.Parse(job.PayloadJson ?? "{}").RootElement;
            var hid = payload.GetProperty("hypothesisId").GetString()!;
            var version = payload.GetProperty("version").GetInt32();

            var hypothesis = (await service.ListHypothesesAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(h => h.HypothesisId == hid && h.Version == version)
                ?? throw new InvalidOperationException($"hypothesis {hid}#v{version} not found");

            var engine = new HypothesisEngine(service.Projection);
            var evaluation = engine.Evaluate(hypothesis, job.JobId, DateTimeOffset.UtcNow);

            // exactly-once record: persist result and completion state in one upsert
            var json = JsonSerializer.Serialize(evaluation, PlateTraceJson.Options);
            job.ResultJson = json;
            job.ResultFingerprint = evaluation.ProjectionFingerprint;
            job.State = JobState.Completed;
            job.FinishedAt = DateTimeOffset.UtcNow;
            job.Error = null;
            await _store.UpsertJobAsync(job, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            job.State = JobState.Failed;
            job.Error = ex.Message;
            job.FinishedAt = DateTimeOffset.UtcNow;
            await _store.UpsertJobAsync(job, ct).ConfigureAwait(false);
        }
    }

    public static async Task<HypothesisEvaluation?> ReadResultAsync(JobRecord job, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(job.ResultJson)) return null;
        return JsonSerializer.Deserialize<HypothesisEvaluation>(job.ResultJson, PlateTraceJson.Options);
    }
}
