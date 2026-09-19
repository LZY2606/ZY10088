using System.Text.Json;
using System.Text.Json.Serialization;

namespace PairwiseGsb.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CarryoverHypothesis), "carryover")]
[JsonDerivedType(typeof(DiluentBatchHypothesis), "diluentBatch")]
public abstract record Hypothesis
{
    public required string Id { get; init; }
    public int Version { get; init; } = 1;
    public string? Note { get; init; }
}

/// <summary>Hypothesis: the aspiration in this event picked up contaminant
/// that the same tip then carried into wells touched afterwards.</summary>
public sealed record CarryoverHypothesis : Hypothesis
{
    public required string EventId { get; init; }
}

/// <summary>Hypothesis: an entire diluent batch was affected, so every well
/// that received it is a candidate contamination source.</summary>
public sealed record DiluentBatchHypothesis : Hypothesis
{
    public required string BatchId { get; init; }
}

public sealed record AnomalyExplanation(
    WellRef Well,
    string? Alias,
    bool Explained,
    List<string> ExplainedByHypothesisIds);

public sealed record HypothesisAnalysis(
    IReadOnlyList<Hypothesis> HypothesisVersions,
    List<AnomalyExplanation> Explanations,
    List<WellRef> ExtraPredictedNormalWells,
    HashSet<string> PredictedWells);

/// <summary>
/// Candidate layer: hypotheses live outside the immutable raw log, are
/// versioned on every edit, and never rewrite recorded operations.
/// </summary>
public sealed class HypothesisStore
{
    private readonly string _path;
    private readonly List<Hypothesis> _history = new();

    public HypothesisStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "hypotheses.json");
        if (File.Exists(_path))
            _history = JsonSerializer.Deserialize<List<Hypothesis>>(
                File.ReadAllText(_path), RuleSet.Json) ?? new List<Hypothesis>();
    }

    public IReadOnlyList<Hypothesis> History => _history;

    public List<Hypothesis> Current() => _history
        .GroupBy(h => h.Id)
        .Select(g => g.OrderByDescending(h => h.Version).First())
        .ToList();

    public Hypothesis Upsert(Hypothesis h)
    {
        var next = h with { Version = _history.Where(x => x.Id == h.Id).Select(x => x.Version).DefaultIfEmpty(0).Max() + 1 };
        _history.Add(next);
        Save();
        return next;
    }

    public void Save() =>
        File.WriteAllText(_path, JsonSerializer.Serialize(_history, RuleSet.Json));

    public static HypothesisAnalysis Analyze(
        LabState state, TraceResult trace, IReadOnlyList<Hypothesis> hypotheses)
    {
        var predicted = new Dictionary<WellRef, List<string>>();

        foreach (var h in hypotheses)
        {
            foreach (var source in SourcesOf(state, h))
            {
                foreach (var well in DownstreamClosure(state, source))
                {
                    if (!predicted.TryGetValue(well, out var list))
                        predicted[well] = list = new List<string>();
                    if (!list.Contains(h.Id)) list.Add(h.Id);
                }
            }
        }

        var anomalousWells = trace.Anomalies.Select(a => a.Well).ToHashSet();
        var explanations = trace.Anomalies
            .GroupBy(a => a.Well)
            .Select(g => new AnomalyExplanation(
                g.Key,
                g.First().Alias,
                predicted.ContainsKey(g.Key),
                predicted.TryGetValue(g.Key, out var ids) ? ids : new List<string>()))
            .ToList();

        var extra = predicted.Keys
            .Where(w => !anomalousWells.Contains(w))
            .Where(w => state.Wells.TryGetValue(w, out var ws) && ws.Readings.Count > 0)
            .OrderBy(w => w.PlateId).ThenBy(w => w.Well)
            .ToList();

        return new HypothesisAnalysis(
            hypotheses,
            explanations,
            extra,
            predicted.Keys.Select(w => w.ToString()).ToHashSet());
    }

    private static IEnumerable<WellRef> SourcesOf(LabState state, Hypothesis h) => h switch
    {
        CarryoverHypothesis c => CarryoverSources(state, c.EventId),
        DiluentBatchHypothesis d => state.Edges
            .Where(e => e.DiluentBatchId == d.BatchId)
            .Select(e => e.Dest),
        _ => Array.Empty<WellRef>(),
    };

    private static IEnumerable<WellRef> CarryoverSources(LabState state, string eventId)
    {
        var trigger = state.Edges.FirstOrDefault(e => e.EventId == eventId);
        if (trigger is null) yield break;
        yield return trigger.Source;
        yield return trigger.Dest;
        // Same tip, later events: the classic carry-over route.
        foreach (var edge in state.Edges
            .Where(e => e.TipId == trigger.TipId && e.OrderIndex > trigger.OrderIndex))
            yield return edge.Dest;
    }

    private static IEnumerable<WellRef> DownstreamClosure(LabState state, WellRef start)
    {
        var seen = new HashSet<WellRef>();
        var queue = new Queue<WellRef>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current)) continue;
            yield return current;
            foreach (var edge in state.Well(current).Outgoing)
                queue.Enqueue(edge.Dest);
        }
    }
}
