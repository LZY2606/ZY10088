using System.Text.Json.Serialization;

namespace PlateTrace.Core.Models;

public enum LabEventType
{
    PlateRegister,
    SampleAlias,
    ReagentBatchRegister,
    ReagentBatchFreeze,
    TipAttach,
    TipDiscard,
    LoadSample,
    AddReagent,
    Transfer,
    Mix,
    Reading,
    PlateFreeze,
    // A correction never mutates the original event; it supersedes it for the
    // working projection and carries a mandatory human reason.
    Correction,
}

/// <summary>
/// One immutable laboratory log entry. Only <see cref="LabEventType.Correction"/>
/// refers to another event; it never rewrites it.
/// </summary>
public sealed class LabEvent
{
    public long Seq { get; set; }
    public string EventId { get; set; } = "";
    public string BatchId { get; set; } = "";
    public LabEventType Kind { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    // plate / well
    public string? PlateId { get; set; }
    public string? Well { get; set; }
    public int? Rows { get; set; }
    public int? Cols { get; set; }

    // samples / reagents / tips
    public string? Alias { get; set; }
    public string? ReagentBatch { get; set; }
    public string? ReagentName { get; set; }
    public string? FrozenVersion { get; set; }
    public string? TipId { get; set; }

    // liquid movement (uL); null bounds keep the volume unknown rather than zero
    public double? VolumeLow { get; set; }
    public double? VolumeHigh { get; set; }
    public double? AspirateLow { get; set; }
    public double? AspirateHigh { get; set; }

    // transfer endpoints
    public string? FromPlate { get; set; }
    public string? FromWell { get; set; }
    public string? ToPlate { get; set; }
    public string? ToWell { get; set; }

    // readings
    public string? Analyte { get; set; }
    public string? Source { get; set; }
    public double? Value { get; set; }
    public string? Unit { get; set; }
    public double? ThresholdHigh { get; set; }

    // correction envelope
    public string? SupersedesEventId { get; set; }
    public string? Reason { get; set; }
    public LabEventType? ReplacementKind { get; set; }
    public string? ReplacementJson { get; set; }

    public bool IsMutation() => Kind is LabEventType.LoadSample or LabEventType.AddReagent
        or LabEventType.Transfer or LabEventType.Mix;

    /// <summary>True when this entry is the working-projection replacement from a Correction.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsReplacement { get; set; }
}

public enum BatchOutcome
{
    Accepted,
    Rejected,
}

/// <summary>
/// Submission boundary: a batch is validated as a whole and either fully applied
/// (events appended) or leaves the log exactly as it was. A rejected batch is
/// still recorded with its evidence errors, keyed for idempotent retries.
/// </summary>
public sealed class BatchRecord
{
    public string BatchId { get; set; } = "";
    public string IdempotencyKey { get; set; } = "";
    public string? Note { get; set; }
    public BatchOutcome Outcome { get; set; }
    public DateTimeOffset CommittedAt { get; set; }
    public string RuleVersion { get; set; } = "plate-trace-rules/1.0.0";
    public List<LabEvent> Events { get; set; } = new();
    public List<EvidenceError> Errors { get; set; } = new();
}

public enum EvidenceSeverity
{
    Error,
    Warning,
}

/// <summary>An individual rule violation attached to an input event/batch.</summary>
public sealed class EvidenceError
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public EvidenceSeverity Severity { get; set; } = EvidenceSeverity.Error;
    public string? EventId { get; set; }
    public int? EventIndex { get; set; }
    public string? WellId { get; set; }
    public string? TipId { get; set; }

    public EvidenceError() { }
    public EvidenceError(string code, string message, int? eventIndex = null, string? eventId = null)
    {
        Code = code; Message = message; EventIndex = eventIndex; EventId = eventId;
    }
}

/// <summary>User-supplied event draft inside a batch submission (transport DTO).</summary>
public sealed class EventDraft
{
    public LabEventType Kind { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }

    public string? PlateId { get; set; }
    public string? Well { get; set; }
    public int? Rows { get; set; }
    public int? Cols { get; set; }

    public string? Alias { get; set; }
    public string? ReagentBatch { get; set; }
    public string? ReagentName { get; set; }
    public string? FrozenVersion { get; set; }
    public string? TipId { get; set; }

    public double? VolumeLow { get; set; }
    public double? VolumeHigh { get; set; }
    public double? AspirateLow { get; set; }
    public double? AspirateHigh { get; set; }

    public string? FromPlate { get; set; }
    public string? FromWell { get; set; }
    public string? ToPlate { get; set; }
    public string? ToWell { get; set; }

    public string? Analyte { get; set; }
    public string? Source { get; set; }
    public double? Value { get; set; }
    public string? Unit { get; set; }
    public double? ThresholdHigh { get; set; }

    public string? SupersedesEventId { get; set; }
    public string? Reason { get; set; }
    public EventDraft? Replacement { get; set; }
}

/// <summary>Transport DTO for an atomic batch submission.</summary>
public sealed class BatchSubmission
{
    public string? IdempotencyKey { get; set; }
    public string? Note { get; set; }
    public List<EventDraft> Events { get; set; } = new();
}
