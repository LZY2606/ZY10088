using System.Text.Json;

namespace PairwiseGsb.Core;

public enum ConclusionStatus { Draft, Published, NeedsReview }

public sealed record PublishedConclusion(
    DateTimeOffset PublishedAt,
    string RuleVersion,
    List<string> FrozenEntities,
    List<string> ReferencedPlates,
    List<string> ReferencedReagentBatches,
    List<string> EventOrder,
    List<AnomalousWell> Anomalies,
    List<TracePath> Paths,
    List<string> HypothesisVersions,
    List<EvidenceError> EvidenceErrors);

public sealed class ConclusionState
{
    public ConclusionStatus Status { get; set; } = ConclusionStatus.Draft;
    public PublishedConclusion? Published { get; set; }
}

/// <summary>
/// Batch conclusion lifecycle. Publish is gated on every referenced plate and
/// reagent batch being frozen. An upstream correction flips the working copy
/// to NeedsReview without touching the already published snapshot.
/// </summary>
public sealed class ConclusionStore
{
    private readonly string _path;
    private readonly ConclusionState _state;

    public ConclusionStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "conclusion.json");
        _state = File.Exists(_path)
            ? JsonSerializer.Deserialize<ConclusionState>(File.ReadAllText(_path), RuleSet.Json) ?? new ConclusionState()
            : new ConclusionState();
    }

    public ConclusionStatus Status => _state.Status;
    public PublishedConclusion? Published => _state.Published;

    public static List<string> ReferencedPlates(LabState state) =>
        state.Plates.Keys.OrderBy(x => x).ToList();

    public static List<string> ReferencedReagentBatches(LabState state) =>
        state.Edges.Where(e => e.DiluentBatchId is not null)
            .Select(e => e.DiluentBatchId!)
            .Concat(state.ReagentBatches.Keys)
            .Distinct().OrderBy(x => x).ToList();

    public List<string> MissingFreezes(LabState state)
    {
        var missing = new List<string>();
        foreach (var p in ReferencedPlates(state))
            if (!state.FrozenEntities.Contains($"plate:{p}")) missing.Add($"plate:{p}");
        foreach (var b in ReferencedReagentBatches(state))
            if (!state.FrozenEntities.Contains($"reagentBatch:{b}")) missing.Add($"reagentBatch:{b}");
        return missing;
    }

    public PublishedConclusion Publish(LabState state, TraceResult trace, IReadOnlyList<Hypothesis> hypotheses)
    {
        var missing = MissingFreezes(state);
        if (missing.Count > 0)
            throw new InvalidOperationException(
                "Cannot publish: unfrozen references: " + string.Join(", ", missing));

        var published = new PublishedConclusion(
            DateTimeOffset.UtcNow,
            state.RuleVersion,
            state.FrozenEntities.OrderBy(x => x).ToList(),
            ReferencedPlates(state),
            ReferencedReagentBatches(state),
            state.EventOrder.Select(e => e.EventId).ToList(),
            trace.Anomalies,
            trace.Paths,
            hypotheses.Select(h => $"{h.Id}@v{h.Version}").ToList(),
            state.Errors);
        _state.Published = published;
        _state.Status = ConclusionStatus.Published;
        Save();
        return published;
    }

    /// <summary>Called when a correction lands upstream of the conclusion.</summary>
    public void OnUpstreamCorrection()
    {
        if (_state.Status == ConclusionStatus.Published)
        {
            _state.Status = ConclusionStatus.NeedsReview; // published snapshot untouched
            Save();
        }
    }

    private void Save() =>
        File.WriteAllText(_path, JsonSerializer.Serialize(_state, RuleSet.Json));
}
