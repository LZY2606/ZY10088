namespace PlateTrace.Core.Models;

public sealed class PlateInfo
{
    public required string PlateId { get; init; }
    public int Rows { get; init; }
    public int Cols { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? FrozenAt { get; init; }
}

public sealed class ReagentBatchInfo
{
    public required string Batch { get; init; }
    public string? Name { get; init; }
    public DateTimeOffset RegisteredAt { get; init; }
    public string? FrozenVersion { get; set; }
    public DateTimeOffset? FrozenAt { get; set; }
    public bool IsFrozen => FrozenAt is not null;
}

public sealed class ReadingRecord
{
    public required long Seq { get; init; }
    public required string EventId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required string Analyte { get; init; }
    public required string Source { get; init; }
    public required double ValueBase { get; init; }
    public required string BaseUnit { get; init; }
    public required string OriginalUnit { get; init; }
    public required double OriginalValue { get; init; }
    public double? ThresholdHigh { get; init; }
    public bool IsAbnormal => ThresholdHigh is not null && ValueBase >= ThresholdHigh.Value;
}

public sealed class WellState
{
    public required WellRef Ref { get; init; }
    public Interval Volume { get; set; } = Interval.Unknown;
    public string? SampleAlias { get; set; }
    public List<string> ReagentBatches { get; } = new();
    public List<ReadingRecord> Readings { get; } = new();
    public DateTimeOffset? LastMixAt { get; set; }
}

public sealed class TipLife
{
    public required string TipId { get; init; }
    public bool Discarded { get; set; }
    public List<long> UseSeqs { get; } = new();
}

/// <summary>A directed edge of the time-respecting transfer graph.</summary>
public sealed record TransferEdge(
    long Seq,
    string EventId,
    WellRef From,
    WellRef To,
    Interval Fraction,
    Interval Aspirate,
    string? TipId,
    DateTimeOffset OccurredAt,
    bool Corrected)
{
    public string Label => $"{From.Id}→{To.Id} {Fraction}";
}

/// <summary>A reagent/diluent feeds a well, e.g. "diluent batch D-7 → P2/A1".</summary>
public sealed record ReagentEdge(
    long Seq,
    string EventId,
    string ReagentBatch,
    string? ReagentName,
    WellRef To,
    Interval AddedVolume,
    DateTimeOffset OccurredAt,
    bool Corrected);

/// <summary>One origin→target path with its cumulative dilution interval.</summary>
public sealed class TracePath
{
    public required List<TransferEdge> Edges { get; init; }
    public Interval Cumulative { get; set; }
    public WellRef Origin => Edges[0].From;
    public WellRef Target => Edges[^1].To;
    public string? TipId => string.Join(",", Edges.Where(e => e.TipId is not null).Select(e => e.TipId).Distinct());
}

public enum ReadingConsistency
{
    Consistent,
    Conflicting,
}

/// <summary>Comparable readings of the same well/analyte after explicit conversion.</summary>
public sealed class ReadingComparison
{
    public required string WellId { get; init; }
    public required string Analyte { get; init; }
    public required List<ReadingRecord> Readings { get; init; }
    public ReadingConsistency Consistency { get; set; }
    public double RelativeTolerance { get; init; }
    public string Detail { get; set; } = "";
}
