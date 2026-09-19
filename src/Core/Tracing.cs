namespace PairwiseGsb.Core;

public sealed record AnomalousWell(
    WellRef Well,
    string? Alias,
    Reading Reading,
    double NormalizedValue,
    string NormalizedUnit);

public sealed record TraceStep(
    string EventId,
    WellRef From,
    WellRef To,
    Interval Volume,
    Interval DilutionFactor);

public sealed record TracePath(
    WellRef Origin,
    WellRef Terminus,
    List<TraceStep> Steps,
    Interval CumulativeConcentration)
{
    /// <summary>Dilution range is the reciprocal of the concentration interval.</summary>
    public Interval CumulativeDilution => Interval.Exact(1.0) / CumulativeConcentration;
}

public sealed record TraceResult(
    double ReadingThreshold,
    string Unit,
    double MinContributionFraction,
    List<AnomalousWell> Anomalies,
    List<TracePath> Paths,
    List<EvidenceError> UnitProblems);

/// <summary>
/// Upstream anomaly tracing over the transfer graph. A path qualifies when
/// its cumulative concentration factor interval can still reach the minimum
/// contribution fraction (Hi >= fraction); unknown volumes widen the interval
/// instead of zeroing it.
/// </summary>
public static class Tracer
{
    public static TraceResult Trace(
        LabState state, double readingThreshold, string unit,
        double minContributionFraction = 0.0)
    {
        var anomalies = new List<AnomalousWell>();
        var unitProblems = new List<EvidenceError>();

        foreach (var well in state.Wells.Values)
        {
            foreach (var reading in well.Readings)
            {
                var normalized = state.Convert(reading.Value, reading.Unit, unit);
                if (normalized is null)
                {
                    unitProblems.Add(new EvidenceError(
                        EvidenceErrorKind.IncomparableUnits,
                        $"Reading '{reading.EventId}' on {well.Ref} is in '{reading.Unit}' and no explicit conversion to '{unit}' is declared",
                        new List<string> { reading.EventId },
                        new List<string> { well.Ref.ToString() }));
                    continue;
                }
                if (normalized > readingThreshold)
                    anomalies.Add(new AnomalousWell(well.Ref, well.Alias, reading, normalized.Value, unit));
            }
        }

        var paths = new List<TracePath>();
        foreach (var anomaly in anomalies)
            foreach (var path in TraceWell(state, anomaly.Well, minContributionFraction))
                paths.Add(path);

        return new TraceResult(readingThreshold, unit, minContributionFraction, anomalies, paths, unitProblems);
    }

    public static List<TracePath> TraceWell(LabState state, WellRef target, double minContributionFraction)
    {
        var results = new List<TracePath>();
        var stack = new List<TransferEdge>();
        var visited = new HashSet<WellRef> { target };

        void Dfs(WellRef current, Interval factor)
        {
            var incoming = state.Well(current).Incoming;
            if (incoming.Count == 0)
            {
                results.Add(new TracePath(current, target, ToSteps(stack), factor));
                return;
            }
            foreach (var edge in incoming)
            {
                if (!visited.Add(edge.Source)) continue; // simple paths only
                // Fraction of the destination well contributed by this transfer.
                var edgeFactor = edge.Volume / edge.DestVolumeAfter;
                var next = factor * edgeFactor;
                stack.Add(edge);
                Dfs(edge.Source, next);
                stack.RemoveAt(stack.Count - 1);
                visited.Remove(edge.Source);
            }
        }

        Dfs(target, Interval.Exact(1.0));
        return results
            .Where(p => p.Steps.Count == 0 || p.CumulativeConcentration.Hi >= minContributionFraction)
            .ToList();
    }

    private static List<TraceStep> ToSteps(List<TransferEdge> stack) => stack
        .Select(e => new TraceStep(e.EventId, e.Source, e.Dest, e.Volume, e.Volume / e.DestVolumeAfter))
        .ToList();
}
