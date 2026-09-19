using PlateTrace.Core.Models;
using PlateTrace.Core.Store;

namespace PlateTrace.Core.Tests;

/// <summary>In-memory implementation of the store contract for fast deterministic tests.</summary>
public sealed class InMemoryStore : ITraceStore
{
    private readonly object _gate = new();
    public string DataDir { get; } = ":memory:";
    public List<BatchRecord> Batches { get; } = new();
    public List<HypothesisRecord> Hypotheses { get; } = new();
    public List<ConclusionRecord> Conclusions { get; } = new();
    public List<JobRecord> Jobs { get; } = new();

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<BatchRecord>> ListBatchesAsync(CancellationToken ct = default)
    { lock (_gate) return Task.FromResult<IReadOnlyList<BatchRecord>>(Batches.ToList()); }

    public Task<BatchRecord?> FindIdempotentAsync(string idempotencyKey, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult(Batches.FirstOrDefault(b => b.IdempotencyKey == idempotencyKey)); }

    public Task AppendBatchAsync(BatchRecord batch, CancellationToken ct = default)
    { lock (_gate) { if (!Batches.Any(b => b.BatchId == batch.BatchId)) Batches.Add(batch); } return Task.CompletedTask; }

    public Task<IReadOnlyList<HypothesisRecord>> ListHypothesesAsync(CancellationToken ct = default)
    { lock (_gate) return Task.FromResult<IReadOnlyList<HypothesisRecord>>(Hypotheses.ToList()); }

    public Task AppendHypothesisAsync(HypothesisRecord hypothesis, CancellationToken ct = default)
    { lock (_gate) { Hypotheses.RemoveAll(h => h.HypothesisId == hypothesis.HypothesisId && h.Version == hypothesis.Version); Hypotheses.Add(hypothesis); } return Task.CompletedTask; }

    public Task<IReadOnlyList<ConclusionRecord>> ListConclusionsAsync(CancellationToken ct = default)
    { lock (_gate) return Task.FromResult<IReadOnlyList<ConclusionRecord>>(Conclusions.ToList()); }

    public Task AppendConclusionAsync(ConclusionRecord conclusion, CancellationToken ct = default)
    { lock (_gate) { Conclusions.RemoveAll(c => c.ConclusionId == conclusion.ConclusionId && c.CreatedAt == conclusion.CreatedAt); Conclusions.Add(conclusion); } return Task.CompletedTask; }

    public Task<IReadOnlyList<JobRecord>> ListJobsAsync(CancellationToken ct = default)
    { lock (_gate) return Task.FromResult<IReadOnlyList<JobRecord>>(Jobs.ToList()); }

    public Task<JobRecord?> FindJobByIdempotencyAsync(string key, CancellationToken ct = default)
    { lock (_gate) return Task.FromResult(Jobs.FirstOrDefault(j => j.IdempotencyKey == key)); }

    public Task UpsertJobAsync(JobRecord job, CancellationToken ct = default)
    { lock (_gate) { var i = Jobs.FindIndex(j => j.JobId == job.JobId); if (i >= 0) Jobs[i] = job; else Jobs.Add(job); } return Task.CompletedTask; }
}
