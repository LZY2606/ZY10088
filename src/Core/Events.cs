using System.Text.Json.Serialization;

namespace PairwiseGsb.Core;

/// <summary>A well is always scoped to a plate; identical coordinates on
/// different plates are distinct nodes in the transfer graph.</summary>
public readonly record struct WellRef(string PlateId, string Well)
{
    public override string ToString() => $"{PlateId}!{Well}";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PlateRegistered), "plateRegistered")]
[JsonDerivedType(typeof(VolumeDeclared), "volumeDeclared")]
[JsonDerivedType(typeof(SampleAliased), "sampleAliased")]
[JsonDerivedType(typeof(TransferRecorded), "transferRecorded")]
[JsonDerivedType(typeof(MixRecorded), "mixRecorded")]
[JsonDerivedType(typeof(ReadingRecorded), "readingRecorded")]
[JsonDerivedType(typeof(UnitConversionDeclared), "unitConversionDeclared")]
[JsonDerivedType(typeof(CorrectionRecorded), "correctionRecorded")]
[JsonDerivedType(typeof(ReagentBatchRegistered), "reagentBatchRegistered")]
[JsonDerivedType(typeof(EntityFrozen), "entityFrozen")]
public abstract record LabEvent
{
    public string EventId { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
}

public sealed record PlateRegistered : LabEvent
{
    public required string PlateId { get; init; }
    public required int Rows { get; init; }
    public required int Cols { get; init; }
}

/// <summary>Declares the initial volume of a well. Null means unknown;
/// unknown volumes are tracked as intervals, never as zero.</summary>
public sealed record VolumeDeclared : LabEvent
{
    public required string PlateId { get; init; }
    public required string Well { get; init; }
    public double? VolumeUl { get; init; }
}

public sealed record SampleAliased : LabEvent
{
    public required string PlateId { get; init; }
    public required string Well { get; init; }
    public required string Alias { get; init; }
}

/// <summary>A single aspirate+dispense with one tip. Volume null = unknown.</summary>
public sealed record TransferRecorded : LabEvent
{
    public required WellRef Source { get; init; }
    public required WellRef Dest { get; init; }
    public double? VolumeUl { get; init; }
    public required string TipId { get; init; }
    public string? DiluentBatchId { get; init; }
}

public sealed record MixRecorded : LabEvent
{
    public required string PlateId { get; init; }
    public required string Well { get; init; }
}

public sealed record ReadingRecorded : LabEvent
{
    public required string PlateId { get; init; }
    public required string Well { get; init; }
    public required double Value { get; init; }
    public required string Unit { get; init; }
    public required string Source { get; init; }
}

/// <summary>Readings in different units may only be compared after an
/// explicit conversion has been declared through this event.</summary>
public sealed record UnitConversionDeclared : LabEvent
{
    public required string FromUnit { get; init; }
    public required string ToUnit { get; init; }
    public required double Factor { get; init; }
}

/// <summary>Corrections never mutate the log: the original event stays and
/// the replacement takes effect from this point in the effective view.</summary>
public sealed record CorrectionRecorded : LabEvent
{
    public required string SupersedesEventId { get; init; }
    public required string Reason { get; init; }
    public required LabEvent Replacement { get; init; }
}

public sealed record ReagentBatchRegistered : LabEvent
{
    public required string BatchId { get; init; }
    public required string Kind { get; init; }
    public required string Version { get; init; }
}

public sealed record EntityFrozen : LabEvent
{
    public required string EntityKind { get; init; } // "plate" | "reagentBatch"
    public required string EntityId { get; init; }
}
