using PlateTrace.Core.Models;

namespace PlateTrace.Core.Store;

/// <summary>
/// Durable append-only store. Batch boundaries are preserved: a call to
/// <see cref="AppendBatch"/> persists the accepted outcome together with its
/// events, or nothing of that submission except its rejection record.
/// </summary>
public interface ITraceStore
{
    string DataDir { get; }

    Task InitializeAsync(CancellationToken ct = default);

    // batches / immutable event log
    Task<IReadOnlyList<BatchRecord>> ListBatchesAsync(CancellationToken ct = default);
    Task<BatchRecord?> FindIdempotentAsync(string idempotencyKey, CancellationToken ct = default);
    Task AppendBatchAsync(BatchRecord batch, CancellationToken ct = default);

    // hypothesis revisions (append-only per id)
    Task<IReadOnlyList<HypothesisRecord>> ListHypothesesAsync(CancellationToken ct = default);
    Task AppendHypothesisAsync(HypothesisRecord hypothesis, CancellationToken ct = default);

    // conclusion revisions (append-only per id)
    Task<IReadOnlyList<ConclusionRecord>> ListConclusionsAsync(CancellationToken ct = default);
    Task AppendConclusionAsync(ConclusionRecord conclusion, CancellationToken ct = default);

    // recoverable background jobs
    Task<IReadOnlyList<JobRecord>> ListJobsAsync(CancellationToken ct = default);
    Task<JobRecord?> FindJobByIdempotencyAsync(string key, CancellationToken ct = default);
    Task UpsertJobAsync(JobRecord job, CancellationToken ct = default);
}
