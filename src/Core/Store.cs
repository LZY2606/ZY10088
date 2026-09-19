using System.Text.Json;

namespace PairwiseGsb.Core;

public static class RuleSet
{
    public const string RuleVersion = "rules/1.0.0";

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

/// <summary>One atomically committed batch of events.</summary>
public sealed record CommittedBatch(
    string IdempotencyKey,
    DateTimeOffset CommittedAt,
    List<LabEvent> Events);

public sealed record BatchCommitResult(
    bool Applied,
    bool WasReplay,
    string IdempotencyKey,
    int EventCount,
    List<string> RejectionReasons);

/// <summary>
/// Append-only event store. Every accepted batch is appended as a single
/// JSON line (the commit boundary); the raw log is never rewritten.
/// Corrections are themselves events, so history is immutable.
/// </summary>
public sealed class EventStore
{
    private readonly string _dataDir;
    private readonly string _logPath;
    private readonly List<CommittedBatch> _batches = new();
    private readonly Dictionary<string, BatchCommitResult> _byKey = new();

    public EventStore(string dataDir)
    {
        _dataDir = dataDir;
        Directory.CreateDirectory(_dataDir);
        _logPath = Path.Combine(_dataDir, "events.jsonl");
        Load();
    }

    public IReadOnlyList<CommittedBatch> Batches => _batches;

    private void Load()
    {
        if (!File.Exists(_logPath)) return;
        foreach (var line in File.ReadLines(_logPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var batch = JsonSerializer.Deserialize<CommittedBatch>(line, RuleSet.Json)
                ?? throw new InvalidDataException("Corrupt batch line in events.jsonl");
            _batches.Add(batch);
            _byKey[batch.IdempotencyKey] = new BatchCommitResult(
                true, false, batch.IdempotencyKey, batch.Events.Count, new List<string>());
        }
    }

    /// <summary>All events in log order, including superseded originals and
    /// corrections. This is the immutable audit view.</summary>
    public List<LabEvent> RawLog() => _batches.SelectMany(b => b.Events).ToList();

    /// <summary>The effective view: superseded originals removed, correction
    /// replacements applied at the position of the correction.</summary>
    public List<LabEvent> EffectiveEvents()
    {
        var superseded = new HashSet<string>();
        foreach (var e in RawLog())
            if (e is CorrectionRecorded c)
                superseded.Add(c.SupersedesEventId);

        var effective = new List<LabEvent>();
        foreach (var e in RawLog())
        {
            if (e is CorrectionRecorded c)
            {
                effective.Add(c.Replacement);
                effective.Add(e); // corrections stay visible in the effective order
            }
            else if (!superseded.Contains(e.EventId))
            {
                effective.Add(e);
            }
        }
        return effective;
    }

    /// <summary>
    /// Validate-then-commit: either every event in the batch is appended as
    /// one line, or nothing changes. Retries with the same idempotency key
    /// return the original result without re-applying.
    /// </summary>
    public BatchCommitResult SubmitBatch(string idempotencyKey, List<LabEvent> events)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency key is required", nameof(idempotencyKey));

        if (_byKey.TryGetValue(idempotencyKey, out var prior))
            return prior with { WasReplay = true };

        var rejection = ValidateBatch(events);
        if (rejection.Count > 0)
        {
            var rejected = new BatchCommitResult(false, false, idempotencyKey, 0, rejection);
            _byKey[idempotencyKey] = rejected; // stable outcome for retries
            return rejected;
        }

        var batch = new CommittedBatch(idempotencyKey, DateTimeOffset.UtcNow, events);
        var line = JsonSerializer.Serialize(batch, RuleSet.Json);
        File.AppendAllText(_logPath, line + Environment.NewLine);
        _batches.Add(batch);
        var applied = new BatchCommitResult(true, false, idempotencyKey, events.Count, new List<string>());
        _byKey[idempotencyKey] = applied;
        return applied;
    }

    private List<string> ValidateBatch(List<LabEvent> events)
    {
        var problems = new List<string>();
        var knownIds = RawLog().Select(e => e.EventId).ToHashSet();
        var batchIds = new HashSet<string>();
        var knownPlates = EffectiveEvents().OfType<PlateRegistered>().Select(p => p.PlateId).ToHashSet();
        var knownCorrections = new HashSet<string>();

        foreach (var e in events)
        {
            if (string.IsNullOrWhiteSpace(e.EventId))
                problems.Add("Event is missing an eventId");
            else if (!batchIds.Add(e.EventId) || knownIds.Contains(e.EventId))
                problems.Add($"Duplicate eventId '{e.EventId}'");

            switch (e)
            {
                case TransferRecorded t when t.VolumeUl is <= 0:
                    problems.Add($"Event '{e.EventId}': transfer volume must be positive");
                    break;
                case UnitConversionDeclared u when u.Factor <= 0:
                    problems.Add($"Event '{e.EventId}': conversion factor must be positive");
                    break;
                case CorrectionRecorded c:
                    if (!knownIds.Contains(c.SupersedesEventId) && !batchIds.Contains(c.SupersedesEventId))
                        problems.Add($"Correction '{c.EventId}' supersedes unknown event '{c.SupersedesEventId}'");
                    if (string.IsNullOrWhiteSpace(c.Reason))
                        problems.Add($"Correction '{c.EventId}' must carry a reason");
                    if (c.Replacement is CorrectionRecorded)
                        problems.Add($"Correction '{c.EventId}' cannot replace with another correction");
                    knownCorrections.Add(c.SupersedesEventId);
                    break;
            }

            foreach (var plateId in ReferencedPlates(e))
                if (!knownPlates.Contains(plateId) &&
                    !events.OfType<PlateRegistered>().Any(p => p.PlateId == plateId))
                    problems.Add($"Event '{e.EventId}' references unknown plate '{plateId}'");
        }
        return problems;
    }

    private static IEnumerable<string> ReferencedPlates(LabEvent e) => e switch
    {
        VolumeDeclared v => new[] { v.PlateId },
        SampleAliased a => new[] { a.PlateId },
        TransferRecorded t => new[] { t.Source.PlateId, t.Dest.PlateId },
        MixRecorded m => new[] { m.PlateId },
        ReadingRecorded r => new[] { r.PlateId },
        CorrectionRecorded c => ReferencedPlates(c.Replacement),
        _ => Array.Empty<string>(),
    };
}
