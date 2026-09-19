namespace PairwiseGsb.Core;

public enum EvidenceErrorKind { OverAspiration, TimeReversal, TipReuse, IncomparableUnits }

public sealed record EvidenceError(
    EvidenceErrorKind Kind,
    string Message,
    List<string> EventIds,
    List<string> Wells);

public sealed record TransferEdge(
    string EventId,
    WellRef Source,
    WellRef Dest,
    Interval Volume,
    string TipId,
    string? DiluentBatchId,
    DateTimeOffset Timestamp,
    int OrderIndex,
    Interval DestVolumeAfter);

public sealed record Reading(string EventId, double Value, string Unit, string Source, DateTimeOffset Timestamp);

public sealed class WellState
{
    public required WellRef Ref { get; init; }
    public Interval InitialVolume { get; set; } = Interval.Zero;
    public bool InitialVolumeKnown { get; set; }
    public Interval CurrentVolume { get; set; } = Interval.Zero;
    public string? Alias { get; set; }
    public List<Reading> Readings { get; } = new();
    public List<TransferEdge> Incoming { get; } = new();
    public List<TransferEdge> Outgoing { get; } = new();
}

public sealed record PlateInfo(string PlateId, int Rows, int Cols);
public sealed record ReagentBatchInfo(string BatchId, string Kind, string Version);

/// <summary>
/// Deterministic projection of the effective event log into a transfer graph
/// with a per-well volume ledger. Evidence errors are detected here, at build
/// time, as soon as the offending events are in the log.
/// </summary>
public sealed class LabState
{
    public Dictionary<string, PlateInfo> Plates { get; } = new();
    public Dictionary<WellRef, WellState> Wells { get; } = new();
    public List<TransferEdge> Edges { get; } = new();
    public List<EvidenceError> Errors { get; } = new();
    public Dictionary<string, ReagentBatchInfo> ReagentBatches { get; } = new();
    public HashSet<string> FrozenEntities { get; } = new(); // "plate:P1", "reagentBatch:D7"
    public List<(string FromUnit, string ToUnit, double Factor)> Conversions { get; } = new();
    public List<LabEvent> EventOrder { get; } = new();
    public string RuleVersion => RuleSet.RuleVersion;

    public static LabState Build(IEnumerable<LabEvent> effectiveEvents)
    {
        var state = new LabState();
        var lastTouch = new Dictionary<WellRef, (DateTimeOffset Ts, string EventId)>();
        var tipUse = new Dictionary<string, string>(); // tipId -> first eventId
        var order = 0;

        foreach (var e in effectiveEvents)
        {
            state.EventOrder.Add(e);
            switch (e)
            {
                case PlateRegistered p:
                    state.Plates[p.PlateId] = new PlateInfo(p.PlateId, p.Rows, p.Cols);
                    break;

                case VolumeDeclared v:
                {
                    var well = state.Well(new WellRef(v.PlateId, v.Well));
                    well.InitialVolume = Interval.FromNullable(v.VolumeUl);
                    well.InitialVolumeKnown = v.VolumeUl is not null;
                    well.CurrentVolume = well.InitialVolume;
                    state.CheckTime(v, new WellRef(v.PlateId, v.Well), lastTouch);
                    break;
                }

                case SampleAliased a:
                    state.Well(new WellRef(a.PlateId, a.Well)).Alias = a.Alias;
                    break;

                case TransferRecorded t:
                {
                    var volume = Interval.FromNullable(t.VolumeUl);
                    var source = state.Well(t.Source);
                    var dest = state.Well(t.Dest);

                    if (tipUse.TryGetValue(t.TipId, out var firstUse))
                        state.Errors.Add(new EvidenceError(
                            EvidenceErrorKind.TipReuse,
                            $"Disposable tip '{t.TipId}' reused by event '{t.EventId}' (first used by '{firstUse}')",
                            new List<string> { firstUse, t.EventId },
                            new List<string> { t.Source.ToString(), t.Dest.ToString() }));
                    else
                        tipUse[t.TipId] = t.EventId;

                    state.CheckTime(t, t.Source, lastTouch);
                    state.CheckTime(t, t.Dest, lastTouch);

                    if (volume.DefinitelyExceeds(source.CurrentVolume))
                        state.Errors.Add(new EvidenceError(
                            EvidenceErrorKind.OverAspiration,
                            $"Event '{t.EventId}' aspirates {volume} from {t.Source} which holds at most {source.CurrentVolume.Hi:0.###} uL",
                            new List<string> { t.EventId },
                            new List<string> { t.Source.ToString() }));

                    source.CurrentVolume -= volume;
                    dest.CurrentVolume += volume;

                    var edge = new TransferEdge(
                        t.EventId, t.Source, t.Dest, volume, t.TipId, t.DiluentBatchId,
                        t.Timestamp, order++, dest.CurrentVolume);
                    state.Edges.Add(edge);
                    source.Outgoing.Add(edge);
                    dest.Incoming.Add(edge);
                    break;
                }

                case MixRecorded m:
                    state.CheckTime(m, new WellRef(m.PlateId, m.Well), lastTouch);
                    break;

                case ReadingRecorded r:
                    state.Well(new WellRef(r.PlateId, r.Well)).Readings.Add(
                        new Reading(r.EventId, r.Value, r.Unit, r.Source, r.Timestamp));
                    break;

                case UnitConversionDeclared u:
                    state.Conversions.Add((u.FromUnit, u.ToUnit, u.Factor));
                    break;

                case ReagentBatchRegistered b:
                    state.ReagentBatches[b.BatchId] = new ReagentBatchInfo(b.BatchId, b.Kind, b.Version);
                    break;

                case EntityFrozen f:
                    state.FrozenEntities.Add($"{f.EntityKind}:{f.EntityId}");
                    break;
            }
        }
        return state;
    }

    private void CheckTime(LabEvent e, WellRef well,
        Dictionary<WellRef, (DateTimeOffset Ts, string EventId)> lastTouch)
    {
        if (lastTouch.TryGetValue(well, out var prior) && e.Timestamp < prior.Ts)
            Errors.Add(new EvidenceError(
                EvidenceErrorKind.TimeReversal,
                $"Event '{e.EventId}' touches {well} at {e.Timestamp:u} which is before '{prior.EventId}' at {prior.Ts:u}",
                new List<string> { prior.EventId, e.EventId },
                new List<string> { well.ToString() }));
        else if (!lastTouch.TryGetValue(well, out var cur) || e.Timestamp >= cur.Ts)
            lastTouch[well] = (e.Timestamp, e.EventId);
    }

    public WellState Well(WellRef r)
    {
        if (!Wells.TryGetValue(r, out var w))
            Wells[r] = w = new WellState { Ref = r };
        return w;
    }

    /// <summary>Explicit unit conversion graph; readings in different units are
    /// only comparable along declared conversions (BFS over factors).</summary>
    public double? Convert(double value, string fromUnit, string toUnit)
    {
        if (fromUnit == toUnit) return value;
        var visited = new HashSet<string> { fromUnit };
        var frontier = new Queue<(string Unit, double Factor)>();
        frontier.Enqueue((fromUnit, 1.0));
        while (frontier.Count > 0)
        {
            var (unit, factor) = frontier.Dequeue();
            foreach (var (from, to, f) in Conversions)
            {
                string? next = null;
                double nextFactor = factor;
                if (from == unit) { next = to; nextFactor = factor * f; }
                else if (to == unit) { next = from; nextFactor = factor / f; }
                if (next is null || !visited.Add(next)) continue;
                if (next == toUnit) return value * nextFactor;
                frontier.Enqueue((next, nextFactor));
            }
        }
        return null;
    }
}
