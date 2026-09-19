using PlateTrace.Core.Models;

namespace PlateTrace.Core.Engine;

/// <summary>
/// Rebuilt working state for one point in the immutable log. The projection
/// applies corrections (the raw log stays untouched) and records every rule
/// violation encountered as evidence errors.
/// </summary>
public sealed class Projection
{
    public string RuleVersion { get; init; } = "plate-trace-rules/1.0.0";
    public long LastSeq { get; set; }

    /// <summary>Effective timeline: replacement events stand in for corrected originals.</summary>
    public List<LabEvent> Timeline { get; } = new();
    public Dictionary<string, LabEvent> RawById { get; } = new(StringComparer.Ordinal);
    public HashSet<string> SupersededEventIds { get; } = new(StringComparer.Ordinal);
    public List<LabEvent> Corrections { get; } = new();

    public Dictionary<string, PlateInfo> Plates { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, WellState> Wells { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ReagentBatchInfo> ReagentBatches { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, TipLife> Tips { get; } = new(StringComparer.Ordinal);

    public List<TransferEdge> TransferEdges { get; } = new();
    public List<ReagentEdge> ReagentEdges { get; } = new();
    public List<EvidenceError> Errors { get; } = new();

    public string Fingerprint { get; set; } = "";

    public WellState? TryWell(WellRef r) => Wells.GetValueOrDefault(r.Id);
    public WellState RequireWell(WellRef r) =>
        Wells.TryGetValue(r.Id, out var w) ? w : throw new InvalidOperationException($"unknown well {r.Id}");
    public bool IsFrozenPlate(string plateId) =>
        Plates.TryGetValue(plateId, out var p) && p.FrozenAt is not null;
}
