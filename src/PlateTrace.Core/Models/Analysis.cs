using PlateTrace.Core.Store;

namespace PlateTrace.Core.Models;

public enum HypothesisKind
{
    /// <summary>An aspiration/transfer event carries extra material (tip carry-over).</summary>
    CarryOver,
    /// <summary>A whole reagent/diluent batch is contaminated at registration/freeze version.</summary>
    ContaminatedReagent,
    /// <summary>A disposable tip contacts extra wells and delivers contaminant on later uses.</summary>
    ContaminatedTip,
}

/// <summary>Immutable hypothesis revision. Every edit creates a new version; old versions stay.</summary>
public sealed class HypothesisRecord
{
    public string HypothesisId { get; set; } = "";
    public int Version { get; set; } = 1;
    public HypothesisKind Kind { get; set; }
    public string Description { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }

    // carry-over: the transfer event that performs the contaminated delivery
    public string? TargetEventId { get; set; }
    // optional explicit carry origin (defaults to the event's source well)
    public string? SourcePlate { get; set; }
    public string? SourceWell { get; set; }
    public string? InjectionPlate { get; set; }
    public string? InjectionWell { get; set; }

    // reagent batch / tip selectors
    public string? ReagentBatch { get; set; }
    public string? TipId { get; set; }

    // assumed contaminant level for one analyte, expressed in the analyte base unit
    public string Analyte { get; set; } = "";
    public double? ConcentrationLow { get; set; }
    public double? ConcentrationHigh { get; set; }
    public double? CarryFractionLow { get; set; } = 0.0001;
    public double? CarryFractionHigh { get; set; } = 0.05;
    public double MinPathFraction { get; set; } = 1e-6;

    public bool Retired { get; set; }
    public string? RetireReason { get; set; }
}

public enum ExplanationStatus
{
    /// <summary>Every path bound predicts the threshold is exceeded.</summary>
    Definite,
    /// <summary>The dilution interval crosses the threshold; the anomaly is compatible.</summary>
    Possible,
    /// <summary>No eligible propagation path reaches the well above the threshold.</summary>
    NotExplained,
}

public sealed class PathContribution
{
    public List<string> EdgeEventIds { get; set; } = new();
    public List<string> Route { get; set; } = new();
    public Interval Dilution { get; set; }
    public Interval PredictedLevel { get; set; }
}

public sealed class WellExplanation
{
    public string WellId { get; set; } = "";
    public string Analyte { get; set; } = "";
    public double ObservedValue { get; set; }
    public string Unit { get; set; } = "";
    public double Threshold { get; set; }
    public ExplanationStatus Status { get; set; }
    public Interval PredictedRange { get; set; }
    public List<PathContribution> Contributions { get; set; } = new();
}

/// <summary>Result of evaluating one hypothesis revision against the current projection.</summary>
public sealed class HypothesisEvaluation
{
    public string JobId { get; set; } = "";
    public string HypothesisId { get; set; } = "";
    public int HypothesisVersion { get; set; }
    public string RuleVersion { get; set; } = "plate-trace-rules/1.0.0";
    public DateTimeOffset ComputedAt { get; set; }
    public string ProjectionFingerprint { get; set; } = "";

    public List<WellExplanation> ExplainedAnomalies { get; set; } = new();
    public List<WellExplanation> UnexplainedAnomalies { get; set; } = new();
    /// <summary>Wells that read normal but the hypothesis predicts abnormal.</summary>
    public List<WellExplanation> ExtraPredictedNormal { get; set; } = new();
    /// <summary>Predicted-positive wells that were never read.</summary>
    public List<WellExplanation> ExtraPredictedUnread { get; set; } = new();

    public bool ExplainsAllAnomalies => UnexplainedAnomalies.Count == 0 && ExplainedAnomalies.Count > 0;
}

public enum ConclusionState
{
    Published,
    NeedsReview,
}

/// <summary>Append-only conclusion revision. The latest revision per id is current.</summary>
public sealed class ConclusionRecord
{
    public string ConclusionId { get; set; } = "";
    public string Title { get; set; } = "";
    public ConclusionState State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string RuleVersion { get; set; } = "plate-trace-rules/1.0.0";

    public List<string> ReferencedPlates { get; set; } = new();
    public List<string> ReferencedReagentBatches { get; set; } = new();
    public List<string> ReferencedHypothesisRevisions { get; set; } = new();

    /// <summary>Frozen plate timestamps and reagent versions captured at publish time.</summary>
    public Dictionary<string, string> FreezeSnapshot { get; set; } = new();

    /// <summary>Embedded export bundle; old conclusions stay viewable after upstream corrections.</summary>
    public ExportBundle? Snapshot { get; set; }

    public List<string> ReviewReasons { get; set; } = new();
    public List<string> TriggeredByCorrectionEventIds { get; set; } = new();
}

/// <summary>Everything needed to reproduce a review: inputs, rules, ordering, paths, intervals.</summary>
public sealed class ExportBundle
{
    public string FormatVersion { get; set; } = "plate-trace-store/1.0";
    public string RuleVersion { get; set; } = "plate-trace-rules/1.0.0";
    public DateTimeOffset GeneratedAt { get; set; }
    public double PathMinFraction { get; set; } = 1e-6;

    public List<BatchRecord> Batches { get; set; } = new();
    public List<PlateInfo> Plates { get; set; } = new();
    public List<ReagentBatchInfo> ReagentBatches { get; set; } = new();
    public List<WellExport> Wells { get; set; } = new();
    public List<TipLifeExport> Tips { get; set; } = new();
    public List<TransferEdge> TransferEdges { get; set; } = new();
    public List<ReagentEdge> ReagentEdges { get; set; } = new();
    public List<TracePathExport> Paths { get; set; } = new();
    public List<ReadingComparison> ReadingComparisons { get; set; } = new();
    public List<HypothesisRecord> Hypotheses { get; set; } = new();
    public List<HypothesisEvaluation> Evaluations { get; set; } = new();
    public List<ConclusionRecord> Conclusions { get; set; } = new();
}

public sealed class WellExport
{
    public required string WellId { get; init; }
    public string PlateId { get; init; } = "";
    public string Well { get; init; } = "";
    public string? SampleAlias { get; init; }
    public Interval Volume { get; init; }
    public List<string> ReagentBatches { get; init; } = new();
    public DateTimeOffset? LastMixAt { get; init; }
    public List<ReadingRecord> Readings { get; init; } = new();
}

public sealed class TipLifeExport
{
    public required string TipId { get; init; }
    public bool Discarded { get; init; }
    public List<long> UseSeqs { get; init; } = new();
}

public sealed class TracePathExport
{
    public required string Origin { get; init; }
    public required string Target { get; init; }
    public Interval Cumulative { get; init; }
    public List<string> EdgeEventIds { get; init; } = new();
    public List<string> Route { get; init; } = new();
    public string? TipIds { get; init; }
}
