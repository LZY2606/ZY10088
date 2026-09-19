using System.Text.Json;
using PlateTrace.Core.Models;
using PlateTrace.Core.Store;

namespace PlateTrace.Core.Engine;

public sealed partial class TraceService
{
    public async Task<IReadOnlyList<HypothesisRecord>> ListHypothesesAsync(CancellationToken ct = default)
        => await _store.ListHypothesesAsync(ct).ConfigureAwait(false);

    public async Task<HypothesisRecord> SaveHypothesisAsync(HypothesisRequest req, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = (await _store.ListHypothesesAsync(ct).ConfigureAwait(false)).ToList();
            var id = string.IsNullOrWhiteSpace(req.HypothesisId)
                ? $"H{Guid.NewGuid().ToString("N")[..10]}"
                : req.HypothesisId.Trim()!;
            var version = existing.Where(h => h.HypothesisId == id).Select(h => h.Version).DefaultIfEmpty(0).Max() + 1;

            ValidateHypothesis(req);
            var record = new HypothesisRecord
            {
                HypothesisId = id,
                Version = version,
                Kind = req.Kind,
                Description = req.Description,
                CreatedAt = DateTimeOffset.UtcNow,
                TargetEventId = req.TargetEventId,
                SourcePlate = req.SourcePlate,
                SourceWell = req.SourceWell,
                InjectionPlate = req.InjectionPlate,
                InjectionWell = req.InjectionWell,
                ReagentBatch = req.ReagentBatch,
                TipId = req.TipId,
                Analyte = string.IsNullOrWhiteSpace(req.Analyte) ? "default" : req.Analyte,
                ConcentrationLow = req.ConcentrationLow,
                ConcentrationHigh = req.ConcentrationHigh ?? req.ConcentrationLow,
                CarryFractionLow = req.CarryFractionLow,
                CarryFractionHigh = req.CarryFractionHigh ?? req.CarryFractionLow,
                MinPathFraction = req.MinPathFraction <= 0 ? 1e-6 : req.MinPathFraction,
            };
            await _store.AppendHypothesisAsync(record, ct).ConfigureAwait(false);
            return record;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<HypothesisRecord> RetireHypothesisAsync(string id, string reason, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = (await _store.ListHypothesesAsync(ct).ConfigureAwait(false)).ToList();
            var latest = all.Where(h => h.HypothesisId == id).OrderByDescending(h => h.Version).FirstOrDefault()
                ?? throw new ArgumentException($"unknown hypothesis {id}");
            var json = JsonSerializer.Serialize(latest, PlateTraceJson.Options);
            var copy = JsonSerializer.Deserialize<HypothesisRecord>(json, PlateTraceJson.Options)!;
            copy.Version = latest.Version + 1;
            copy.Retired = true;
            copy.RetireReason = reason;
            copy.CreatedAt = DateTimeOffset.UtcNow;
            await _store.AppendHypothesisAsync(copy, ct).ConfigureAwait(false);
            return copy;
        }
        finally { _writeGate.Release(); }
    }

    private void ValidateHypothesis(HypothesisRequest req)
    {
        switch (req.Kind)
        {
            case HypothesisKind.CarryOver when string.IsNullOrWhiteSpace(req.TargetEventId):
                throw new ArgumentException("carry-over hypothesis requires targetEventId");
            case HypothesisKind.ContaminatedReagent when string.IsNullOrWhiteSpace(req.ReagentBatch):
                throw new ArgumentException("contaminated reagent hypothesis requires reagentBatch");
            case HypothesisKind.ContaminatedTip when string.IsNullOrWhiteSpace(req.TipId):
                throw new ArgumentException("contaminated tip hypothesis requires tipId");
        }
        if (req.ConcentrationLow is < 0)
            throw new ArgumentException("concentrationLow must be non-negative");
        if (req.ConcentrationHigh is < 0)
            throw new ArgumentException("concentrationHigh must be non-negative");
        if (req.Kind == HypothesisKind.CarryOver &&
            (req.CarryFractionLow is < 0 or > 1 || req.CarryFractionHigh is < 0 or > 1))
            throw new ArgumentException("carry fractions must be within [0,1]");
    }

    public async Task<PublishResult> PublishConclusionAsync(PublishRequest req, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var errors = new List<EvidenceError>();
            var plates = req.ReferencedPlates.Distinct(StringComparer.Ordinal).ToList();
            var batches = req.ReferencedReagentBatches.Distinct(StringComparer.Ordinal).ToList();

            foreach (var pid in plates)
            {
                if (!_projection.Plates.TryGetValue(pid, out var plate))
                { errors.Add(new EvidenceError(ErrorCodes.UnknownPlate, $"referenced plate {pid} does not exist")); continue; }
                if (plate.FrozenAt is null)
                    errors.Add(new EvidenceError(ErrorCodes.FrozenPlateMutation,
                        $"referenced plate {pid} is not frozen; freeze it before publishing"));
            }
            foreach (var b in batches)
            {
                if (!_projection.ReagentBatches.TryGetValue(b, out var info))
                { errors.Add(new EvidenceError(ErrorCodes.ReagentUnknown, $"referenced reagent batch {b} does not exist")); continue; }
                if (!info.IsFrozen)
                    errors.Add(new EvidenceError(ErrorCodes.ReagentAlreadyFrozen,
                        $"referenced reagent batch {b} has no frozen version"));
            }
            if (string.IsNullOrWhiteSpace(req.Title))
                errors.Add(new EvidenceError(ErrorCodes.Validation, "title is required"));

            if (errors.Count > 0)
                return new PublishResult { Published = false, Errors = errors };

            var hypotheses = (await _store.ListHypothesesAsync(ct).ConfigureAwait(false)).ToList();
            var picked = hypotheses
                .Where(h => req.ReferencedHypothesisIds.Contains(h.HypothesisId))
                .GroupBy(h => h.HypothesisId)
                .Select(g => g.OrderByDescending(h => h.Version).First())
                .ToList();

            var snapshot = new Dictionary<string, string>();
            foreach (var pid in plates)
            {
                var p = _projection.Plates[pid];
                snapshot[$"plate:{pid}"] = $"frozen@{p.FrozenAt:O}";
            }
            foreach (var b in batches)
            {
                var info = _projection.ReagentBatches[b];
                snapshot[$"reagent:{b}"] = info.FrozenVersion ?? "unfrozen";
            }

            var id = string.IsNullOrWhiteSpace(req.ConclusionId)
                ? $"C{Guid.NewGuid().ToString("N")[..10]}"
                : req.ConclusionId.Trim()!;
            var bundle = await BuildExportBundleAsync(picked, ct).ConfigureAwait(false);

            var conclusion = new ConclusionRecord
            {
                ConclusionId = id,
                Title = req.Title.Trim(),
                State = ConclusionState.Published,
                CreatedAt = DateTimeOffset.UtcNow,
                RuleVersion = RuleVersion.Current,
                ReferencedPlates = plates,
                ReferencedReagentBatches = batches,
                ReferencedHypothesisRevisions = picked.Select(h => $"{h.HypothesisId}#v{h.Version}").ToList(),
                FreezeSnapshot = snapshot,
                Snapshot = bundle,
            };
            await _store.AppendConclusionAsync(conclusion, ct).ConfigureAwait(false);
            return new PublishResult { Published = true, Conclusion = conclusion };
        }
        finally { _writeGate.Release(); }
    }

    public async Task<IReadOnlyList<ConclusionRecord>> ListConclusionsAsync(CancellationToken ct = default)
        => await _store.ListConclusionsAsync(ct).ConfigureAwait(false);

    public async Task<ExportBundle> BuildExportBundleAsync(List<HypothesisRecord>? onlyHypotheses = null,
        CancellationToken ct = default)
    {
        var batches = await ListBatchesAsync(ct).ConfigureAwait(false);
        var hypotheses = (await _store.ListHypothesesAsync(ct).ConfigureAwait(false)).ToList();
        if (onlyHypotheses is not null)
        {
            var keep = onlyHypotheses.Select(h => (h.HypothesisId, h.Version)).ToHashSet();
            hypotheses = hypotheses.Where(h => keep.Contains((h.HypothesisId, h.Version))).ToList();
        }

        var tracer = new PathTracer(_projection, _pathMinFraction);
        var paths = tracer.AllPaths();

        var comparisons = BuildReadingComparisons();

        return new ExportBundle
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            RuleVersion = RuleVersion.Current,
            PathMinFraction = _pathMinFraction,
            Batches = batches.ToList(),
            Plates = _projection.Plates.Values.ToList(),
            ReagentBatches = _projection.ReagentBatches.Values.ToList(),
            Wells = _projection.Wells.Values.Select(w => new WellExport
            {
                WellId = w.Ref.Id, PlateId = w.Ref.PlateId, Well = w.Ref.Well,
                SampleAlias = w.SampleAlias, Volume = w.Volume,
                ReagentBatches = w.ReagentBatches.ToList(), LastMixAt = w.LastMixAt,
                Readings = w.Readings.ToList(),
            }).ToList(),
            Tips = _projection.Tips.Values.Select(t => new TipLifeExport
            {
                TipId = t.TipId, Discarded = t.Discarded, UseSeqs = t.UseSeqs.ToList(),
            }).ToList(),
            TransferEdges = _projection.TransferEdges.ToList(),
            ReagentEdges = _projection.ReagentEdges.ToList(),
            Paths = paths.Select(p => new TracePathExport
            {
                Origin = p.Origin.Id, Target = p.Target.Id, Cumulative = p.Cumulative,
                EdgeEventIds = p.Edges.Select(e => e.EventId).ToList(),
                Route = p.Edges.Select(e => e.Label).ToList(),
                TipIds = string.Join(",", p.Edges.Where(e => e.TipId is not null).Select(e => e.TipId).Distinct()),
            }).ToList(),
            ReadingComparisons = comparisons,
            Hypotheses = hypotheses,
            Evaluations = new List<HypothesisEvaluation>(),
            Conclusions = (await _store.ListConclusionsAsync(ct).ConfigureAwait(false)).ToList(),
        };
    }

    public List<ReadingComparison> BuildReadingComparisons(double tolerance = 0.15)
    {
        var result = new List<ReadingComparison>();
        foreach (var well in _projection.Wells.Values)
        {
            foreach (var g in well.Readings.GroupBy(r => r.Analyte).Where(g => g.Count() > 1))
            {
                var readings = g.OrderBy(r => r.OccurredAt).ToList();
                var (min, max) = (readings.Min(r => r.ValueBase), readings.Max(r => r.ValueBase));
                var rel = max <= 0 ? 0 : (max - min) / max;
                result.Add(new ReadingComparison
                {
                    WellId = well.Ref.Id, Analyte = g.Key, Readings = readings,
                    RelativeTolerance = tolerance,
                    Consistency = rel <= tolerance ? ReadingConsistency.Consistent : ReadingConsistency.Conflicting,
                    Detail = $"compared in canonical unit {readings[0].BaseUnit} after explicit per-source conversion; spread {rel:P0}",
                });
            }
        }
        return result;
    }
}
