using System.Text.Json;
using PlateTrace.Core.Models;
using PlateTrace.Core.Store;

namespace PlateTrace.Core.Engine;

/// <summary>
/// Application facade. Owns the durable store and the cached working projection.
/// Batch submission is the only way to append operations: validation runs on a
/// candidate copy and either appends the whole batch (with stable seq numbers)
/// or records a rejection - the log is unchanged in the rejected case.
/// </summary>
public sealed partial class TraceService
{
    private readonly ITraceStore _store;
    private readonly SubmissionValidator _validator = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ProjectionBuilder _builder = new();
    private readonly double _pathMinFraction;

    private Projection _projection = null!;
    private IReadOnlyList<BatchRecord> _batches = Array.Empty<BatchRecord>();

    public TraceService(ITraceStore store, double pathMinFraction = 1e-6)
    {
        _store = store;
        _pathMinFraction = pathMinFraction;
    }

    public ITraceStore Store => _store;
    public Projection Projection => _projection;
    public double PathMinFraction => _pathMinFraction;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _store.InitializeAsync(ct).ConfigureAwait(false);
        _batches = await _store.ListBatchesAsync(ct).ConfigureAwait(false);
        _projection = _builder.Build(_batches);
    }

    public async Task<IReadOnlyList<BatchRecord>> ListBatchesAsync(CancellationToken ct = default)
        => await _store.ListBatchesAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<JobRecord>> ListJobsAsync(CancellationToken ct = default)
        => await _store.ListJobsAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Submit one atomic batch. A repeated idempotency key replays the stored
    /// outcome exactly once - no new events, no new seq numbers, no side effects.
    /// </summary>
    public async Task<SubmitResult> SubmitAsync(BatchSubmission submission, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var key = submission.IdempotencyKey?.Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                var existing = await _store.FindIdempotentAsync(key!, ct).ConfigureAwait(false);
                if (existing is not null)
                    return new SubmitResult
                    {
                        Accepted = existing.Outcome == BatchOutcome.Accepted,
                        Batch = existing,
                        IdempotentReplay = true,
                        Projection = _projection,
                    };
            }

            var now = DateTimeOffset.UtcNow;
            var candidateErrors = _validator.Validate(submission, _projection, now);

            var batchId = $"B{now:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}";
            var batch = new BatchRecord
            {
                BatchId = batchId,
                IdempotencyKey = key ?? "",
                Note = submission.Note,
                CommittedAt = now,
                RuleVersion = RuleVersion.Current,
            };

            var events = new List<LabEvent>();
            var baseSeq = _projection.LastSeq;

            for (var i = 0; i < submission.Events.Count; i++)
            {
                var draft = submission.Events[i];
                var lab = MapDraft(draft, i, batchId, baseSeq, now);
                events.Add(lab);
            }
            batch.Events = events;

            if (candidateErrors.Count == 0)
            {
                // materialize on a candidate projection so volume/tip/freeze violations
                // reject the whole batch without touching the committed log
                var candidateBatches = _batches.Append(batch).ToList();
                var candidate = _builder.Build(candidateBatches);
                var blocking = candidate.Errors
                    .Where(e => IsBlockingAfterSubmit(e, events))
                    .ToList();
                if (blocking.Count > 0)
                {
                    batch.Outcome = BatchOutcome.Rejected;
                    batch.Errors = blocking;
                    await _store.AppendBatchAsync(batch, ct).ConfigureAwait(false);
                    return new SubmitResult { Accepted = false, Batch = batch, Projection = _projection };
                }

                batch.Outcome = BatchOutcome.Accepted;
                await _store.AppendBatchAsync(batch, ct).ConfigureAwait(false);
                _batches = candidateBatches;
                _projection = candidate;
                await ApplyCorrectionReviewAsync(events, now, ct).ConfigureAwait(false);
                return new SubmitResult { Accepted = true, Batch = batch, Projection = _projection };
            }

            batch.Outcome = BatchOutcome.Rejected;
            batch.Errors = candidateErrors;
            await _store.AppendBatchAsync(batch, ct).ConfigureAwait(false);
            return new SubmitResult { Accepted = false, Batch = batch, Projection = _projection };
        }
        finally { _writeGate.Release(); }
    }

    private static bool IsBlockingAfterSubmit(EvidenceError error, List<LabEvent> batchEvents)
    {
        // candidate projection re-validates the whole log: only errors introduced by
        // this batch reject it; pre-existing working errors stay visible, not blocking.
        if (error.Severity == EvidenceSeverity.Warning) return false;
        var ids = batchEvents.Select(e => e.EventId).ToHashSet(StringComparer.Ordinal);
        var correctionIds = batchEvents.Where(e => e.Kind == LabEventType.Correction)
            .Select(e => e.EventId).ToHashSet(StringComparer.Ordinal);
        if (error.EventId is not null && (ids.Contains(error.EventId) || correctionIds.Contains(error.EventId)))
            return true;
        return false;
    }

    private static LabEvent MapDraft(EventDraft d, int index, string batchId, long baseSeq, DateTimeOffset now)
    {
        var e = new LabEvent
        {
            Seq = baseSeq + index + 1,
            EventId = $"{batchId}/E{index + 1:D3}",
            BatchId = batchId,
            Kind = d.Kind,
            OccurredAt = d.OccurredAt ?? now,
            PlateId = d.PlateId, Well = d.Well?.ToUpperInvariant(), Rows = d.Rows, Cols = d.Cols,
            Alias = d.Alias, ReagentBatch = d.ReagentBatch, ReagentName = d.ReagentName,
            FrozenVersion = d.FrozenVersion, TipId = d.TipId,
            VolumeLow = d.VolumeLow, VolumeHigh = d.VolumeHigh,
            AspirateLow = d.AspirateLow, AspirateHigh = d.AspirateHigh,
            FromPlate = d.FromPlate, FromWell = d.FromWell?.ToUpperInvariant(),
            ToPlate = d.ToPlate, ToWell = d.ToWell?.ToUpperInvariant(),
            Analyte = d.Analyte, Source = d.Source, Value = d.Value, Unit = d.Unit,
            ThresholdHigh = d.ThresholdHigh,
            SupersedesEventId = d.SupersedesEventId, Reason = d.Reason,
            ReplacementKind = d.Replacement?.Kind,
            ReplacementJson = d.Replacement is null ? null :
                JsonSerializer.Serialize(new
                {
                    plateId = d.Replacement.PlateId, well = d.Replacement.Well,
                    rows = d.Replacement.Rows, cols = d.Replacement.Cols,
                    alias = d.Replacement.Alias, reagentBatch = d.Replacement.ReagentBatch,
                    reagentName = d.Replacement.ReagentName, frozenVersion = d.Replacement.FrozenVersion,
                    tipId = d.Replacement.TipId,
                    volumeLow = d.Replacement.VolumeLow, volumeHigh = d.Replacement.VolumeHigh,
                    aspirateLow = d.Replacement.AspirateLow, aspirateHigh = d.Replacement.AspirateHigh,
                    fromPlate = d.Replacement.FromPlate, fromWell = d.Replacement.FromWell,
                    toPlate = d.Replacement.ToPlate, toWell = d.Replacement.ToWell,
                    analyte = d.Replacement.Analyte, source = d.Replacement.Source,
                    value = d.Replacement.Value, unit = d.Replacement.Unit,
                    thresholdHigh = d.Replacement.ThresholdHigh,
                }, PlateTraceJson.Options),
        };
        return e;
    }

    /// <summary>
    /// Any correction touching a referenced (non-published-frozen) upstream puts
    /// draft/needs-review conclusions into NeedsReview. Published conclusions are
    /// never rewritten; their frozen snapshot stays the old conclusion.
    /// </summary>
    private async Task ApplyCorrectionReviewAsync(List<LabEvent> accepted, DateTimeOffset now, CancellationToken ct)
    {
        var corrections = accepted.Where(e => e.Kind == LabEventType.Correction).ToList();
        if (corrections.Count == 0) return;

        var affectedPlates = new HashSet<string>(StringComparer.Ordinal);
        var affectedBatches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in corrections)
        {
            if (c.SupersedesEventId is null) continue;
            var original = _projection.RawById.GetValueOrDefault(c.SupersedesEventId);
            if (original is null) continue;
            foreach (var pid in new[] { original.PlateId, original.FromPlate, original.ToPlate }.Where(p => p is not null))
                affectedPlates.Add(pid!);
            if (original.ReagentBatch is not null) affectedBatches.Add(original.ReagentBatch);
        }

        var conclusions = (await _store.ListConclusionsAsync(ct).ConfigureAwait(false))
            .GroupBy(c => c.ConclusionId)
            .Select(g => g.OrderByDescending(c => c.CreatedAt).First())
            .ToList();
        foreach (var conclusion in conclusions)
        {
            // published conclusions are never rewritten: instead an immutable new
            // NeedsReview revision is appended; the old Published revision keeps its snapshot
            if (conclusion.State == ConclusionState.NeedsReview &&
                corrections.All(c => conclusion.TriggeredByCorrectionEventIds.Contains(c.EventId)))
                continue;
            var hitsPlates = conclusion.ReferencedPlates.Any(affectedPlates.Contains);
            var hitsBatches = conclusion.ReferencedReagentBatches.Any(affectedBatches.Contains);
            if (!hitsPlates && !hitsBatches) continue;

            var reviewed = CloneForReview(conclusion, corrections, now);
            await _store.AppendConclusionAsync(reviewed, ct).ConfigureAwait(false);
        }
    }

    private static ConclusionRecord CloneForReview(ConclusionRecord c, List<LabEvent> corrections, DateTimeOffset now)
    {
        var json = JsonSerializer.Serialize(c, PlateTraceJson.Options);
        var copy = JsonSerializer.Deserialize<ConclusionRecord>(json, PlateTraceJson.Options)!;
        copy.State = ConclusionState.NeedsReview;
        foreach (var corr in corrections)
        {
            copy.ReviewReasons.Add($"upstream correction {corr.EventId} ({corr.Reason})");
            copy.TriggeredByCorrectionEventIds.Add(corr.EventId);
        }
        copy.Snapshot = null; // the working copy no longer claims a current snapshot
        copy.CreatedAt = now;
        return copy;
    }
}
