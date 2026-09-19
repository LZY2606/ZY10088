namespace PlateTrace.Core.Models;

public enum JobKind
{
    EvaluateHypothesis,
    RebuildProjection,
}

public enum JobState
{
    Pending,
    Running,
    Completed,
    Failed,
}

/// <summary>
/// Recoverable unit of background work. A job left <see cref="JobState.Running"/>
/// by a crashed process is picked up again on startup, but a recorded result is
/// never written twice (see <see cref="HasResult"/>).
/// </summary>
public sealed class JobRecord
{
    public string JobId { get; set; } = "";
    public JobKind Kind { get; set; }
    public JobState State { get; set; } = JobState.Pending;
    public int Attempts { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? PayloadJson { get; set; }
    public string? ResultJson { get; set; }
    public string? ResultFingerprint { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string RuleVersion { get; set; } = "plate-trace-rules/1.0.0";

    public bool HasResult => !string.IsNullOrEmpty(ResultJson);
}
