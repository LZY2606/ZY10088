using PlateTrace.Core.Models;

namespace PlateTrace.Core.Engine;

/// <summary>
/// Evaluates a contamination hypothesis against the working projection:
///   1. locate injection point(s) (one carry-over transfer, every use of a
///      reagent batch, or the first post-contact transfer of a dirty tip);
///   2. propagate the assumed contaminant interval along every eligible
///      time-respecting path;
///   3. compare predicted ranges at each well with readings and threshold.
///
/// Hypotheses live in the candidate layer only; no operational event changes.
/// </summary>
public sealed class HypothesisEngine
{
    private readonly Projection _projection;

    public HypothesisEngine(Projection projection) => _projection = projection;

    public HypothesisEvaluation Evaluate(HypothesisRecord h, string jobId, DateTimeOffset now)
    {
        var ev = new HypothesisEvaluation
        {
            JobId = jobId,
            HypothesisId = h.HypothesisId,
            HypothesisVersion = h.Version,
            RuleVersion = RuleVersion.Current,
            ComputedAt = now,
            ProjectionFingerprint = _projection.Fingerprint,
        };

        var injections = BuildInjections(h);
        var tracer = new PathTracer(_projection, h.MinPathFraction);
        var perWell = new Dictionary<string, WellExplanation>(StringComparer.Ordinal);

        foreach (var injection in injections)
        {
            List<TracePath> paths;
            switch (injection.Kind)
            {
                case InjectionKind.TransferEdge:
                    var edge = _projection.TransferEdges.FirstOrDefault(e => e.EventId == injection.EventId);
                    if (edge is null) continue;
                    paths = tracer.PathsFromEdge(edge);
                    // the carry is delivered INTO the first edge's destination, so the
                    // destination itself carries the injected level (zero-edge path)
                    AccumulateInjectionWell(perWell, edge.To, injection, h, edge.EventId);
                    break;
                case InjectionKind.Well:
                    paths = tracer.PathsFromWell(injection.Well!);
                    AccumulateInjectionWell(perWell, injection.Well!, injection, h, injection.EventId);
                    break;
                default:
                    paths = new List<TracePath>();
                    break;
            }

            foreach (var path in paths)
                Accumulate(perWell, path, injection, h);
        }

        Classify(ev, perWell, h);
        return ev;
    }

    private enum InjectionKind { TransferEdge, Well }

    private sealed record Injection(
        InjectionKind Kind,
        Interval Level,
        string? EventId = null,
        WellRef? Well = null,
        bool AtEdgeStart = false);

    private List<Injection> BuildInjections(HypothesisRecord h)
    {
        var level = new Interval(h.ConcentrationLow, h.ConcentrationHigh);
        var list = new List<Injection>();

        switch (h.Kind)
        {
            case HypothesisKind.CarryOver:
                if (h.TargetEventId is not null)
                    list.Add(new Injection(InjectionKind.TransferEdge, level, h.TargetEventId, AtEdgeStart: false));
                break;

            case HypothesisKind.ContaminatedReagent:
                if (h.ReagentBatch is null) break;
                // every AddReagent from the batch is an independent injection into its well;
                // downstream propagation starts from that well via later transfers.
                foreach (var re in _projection.ReagentEdges.Where(e => e.ReagentBatch == h.ReagentBatch))
                    list.Add(new Injection(InjectionKind.Well, level, re.EventId, re.To));
                break;

            case HypothesisKind.ContaminatedTip:
                if (h.TipId is null) break;
                // a contaminated tip delivers on each later transfer after the contact;
                // the earliest use is the primary carry edge, later uses add more.
                foreach (var edge in _projection.TransferEdges
                             .Where(e => e.TipId == h.TipId)
                             .OrderBy(e => e.OccurredAt).ThenBy(e => e.Seq))
                {
                    var carry = new Interval(
                        (h.CarryFractionLow ?? 1e-4) * (level.Low ?? 0),
                        (h.CarryFractionHigh ?? 0.05) * level.High);
                    list.Add(new Injection(InjectionKind.TransferEdge, carry, edge.EventId));
                }
                break;
        }

        if (h.Kind == HypothesisKind.CarryOver && h.CarryFractionLow is not null)
        {
            // carry-over level is expressed as fraction x contaminant concentration
            list = list.Select(i => i with
            {
                Level = new Interval(
                    i.Level.Low * (h.CarryFractionLow ?? 0),
                    i.Level.High * (h.CarryFractionHigh ?? 1)),
            }).ToList();
        }

        return list;
    }

    private static void Accumulate(Dictionary<string, WellExplanation> perWell, TracePath path,
        Injection injection, HypothesisRecord h)
    {
        var target = path.Target;
        var lastEdge = path.Edges[^1];
        var factor = path.Cumulative;

        // TransferEdge injections place contaminant at the first edge's destination:
        // that first edge must not dilute again, only subsequent edges do.
        // Well injections (reagent) dilute already on the first outgoing edge.
        var dilution = injection.Kind == InjectionKind.TransferEdge && path.Edges.Count > 1
            ? ProductWithoutFirst(path)
            : factor;
        var predicted = new Interval(
            injection.Level.Low * (dilution.Low ?? 0),
            injection.Level.High is null ? null : injection.Level.High * dilution.High);

        if (!perWell.TryGetValue(target.Id, out var exp))
        {
            exp = new WellExplanation
            {
                WellId = target.Id,
                Analyte = h.Analyte,
                PredictedRange = new Interval(0, 0),
            };
            perWell[target.Id] = exp;
        }

        exp.PredictedRange = Union(exp.PredictedRange, predicted);
        exp.Contributions.Add(new PathContribution
        {
            EdgeEventIds = path.Edges.Select(e => e.EventId).ToList(),
            Route = path.Edges.Select(e => e.Label).ToList(),
            Dilution = factor,
            PredictedLevel = predicted,
        });
    }

    private static Interval ProductWithoutFirst(TracePath path)
    {
        var low = 1.0;
        double? high = 1.0;
        foreach (var e in path.Edges.Skip(1))
        {
            if (e.Fraction.Low is { } fl) low *= fl; else low = 0;
            high = e.Fraction.High is { } fh ? high * fh : null;
        }
        return new Interval(low, high);
    }

    private static void AccumulateInjectionWell(Dictionary<string, WellExplanation> perWell,
        WellRef well, Injection injection, HypothesisRecord h, string? eventId)
    {
        if (!perWell.TryGetValue(well.Id, out var exp))
        {
            exp = new WellExplanation { WellId = well.Id, Analyte = h.Analyte, PredictedRange = new Interval(0, 0) };
            perWell[well.Id] = exp;
        }
        exp.PredictedRange = Union(exp.PredictedRange, injection.Level);
        exp.Contributions.Add(new PathContribution
        {
            EdgeEventIds = eventId is null ? new() : new() { eventId },
            Route = new() { $"injection → {well.Id}" },
            Dilution = Interval.Exact(1),
            PredictedLevel = injection.Level,
        });
    }

    /// <summary>
    /// Combine independent propagation routes into one well: the contaminant is
    /// present if ANY route delivers it. The guaranteed level is the largest lower
    /// bound (most certain route); the possible level is the largest upper bound.
    /// </summary>
    private static Interval Union(Interval a, Interval b) => new(
        Math.Max(a.Low ?? 0, b.Low ?? 0),
        a.High is null || b.High is null ? null : Math.Max(a.High.Value, b.High.Value));

    private void Classify(HypothesisEvaluation ev, Dictionary<string, WellExplanation> perWell, HypothesisRecord h)
    {
        var anomalies = _projection.Wells.Values
            .SelectMany(w => w.Readings.Where(r => r.IsAbnormal && r.Analyte == h.Analyte)
                .Select(r => (w, r)))
            .ToList();

        var explainedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (well, reading) in anomalies)
        {
            perWell.TryGetValue(well.Ref.Id, out var exp);
            var range = exp?.PredictedRange ?? new Interval(0, 0);
            var status = range.DefinitelyAbove(reading.ThresholdHigh!.Value)
                ? ExplanationStatus.Definite
                : range.OverlapsPositive(reading.ThresholdHigh.Value)
                    ? ExplanationStatus.Possible
                    : ExplanationStatus.NotExplained;

            var detail = exp ?? new WellExplanation
            {
                WellId = well.Ref.Id, Analyte = h.Analyte, PredictedRange = range,
            };
            detail.ObservedValue = reading.ValueBase;
            detail.Unit = reading.BaseUnit;
            detail.Threshold = reading.ThresholdHigh.Value;
            detail.Status = status;

            if (status == ExplanationStatus.NotExplained) ev.UnexplainedAnomalies.Add(detail);
            else { ev.ExplainedAnomalies.Add(detail); explainedIds.Add(well.Ref.Id); }
        }

        foreach (var exp in perWell.Values)
        {
            if (explainedIds.Contains(exp.WellId)) continue;
            var well = _projection.Wells.GetValueOrDefault(exp.WellId);
            if (well is null) continue;
            var readings = well.Readings.Where(r => r.Analyte == h.Analyte).ToList();
            if (readings.Count == 0)
            {
                exp.Status = exp.PredictedRange.Low is > 0
                    ? ExplanationStatus.Definite : ExplanationStatus.Possible;
                ev.ExtraPredictedUnread.Add(exp);
                continue;
            }

            var threshold = readings.Where(r => r.ThresholdHigh is not null)
                .Select(r => r.ThresholdHigh!.Value)
                .DefaultIfEmpty(double.NaN).First();
            if (double.IsNaN(threshold))
            {
                // no threshold on record: still report the prediction for the unread-status check
                exp.ObservedValue = readings.Max(r => r.ValueBase);
                exp.Unit = readings[0].BaseUnit;
                exp.Status = ExplanationStatus.Possible;
                ev.ExtraPredictedUnread.Add(exp);
                continue;
            }

            exp.ObservedValue = readings.Max(r => r.ValueBase);
            exp.Unit = readings[0].BaseUnit;
            exp.Threshold = threshold;
            if (exp.ObservedValue < threshold && exp.PredictedRange.DefinitelyAbove(threshold))
            {
                exp.Status = ExplanationStatus.Definite;
                ev.ExtraPredictedNormal.Add(exp);
            }
            else if (exp.ObservedValue < threshold && exp.PredictedRange.OverlapsPositive(threshold))
            {
                exp.Status = ExplanationStatus.Possible;
                ev.ExtraPredictedNormal.Add(exp);
            }
        }
    }
}
