using PlateTrace.Core.Models;

namespace PlateTrace.Core.Engine;

/// <summary>
/// Candidate-layer validation: drafts are validated as one atomic batch against
/// the current working projection. Any error rejects the whole submission; the
/// immutable log is not touched by a rejected batch.
/// </summary>
public sealed class SubmissionValidator
{
    public List<EvidenceError> Validate(BatchSubmission submission, Projection current, DateTimeOffset now)
    {
        var errors = new List<EvidenceError>();
        if (submission.Events.Count == 0)
        {
            errors.Add(new EvidenceError(ErrorCodes.EmptyBatch, "a batch must contain at least one event"));
            return errors;
        }

        DateTimeOffset? lastTime = null;
        var tipUsedInBatch = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < submission.Events.Count; i++)
        {
            var d = submission.Events[i];
            var at = d.OccurredAt ?? now;

            if (at < new DateTimeOffset(1990, 1, 1, 0, 0, 0, TimeSpan.Zero))
                errors.Add(new EvidenceError(ErrorCodes.Validation, $"event #{i}: implausible timestamp {at:O}", i));

            // time must not move backwards, neither within the batch nor vs the log
            if (lastTime is not null && at < lastTime.Value)
                errors.Add(new EvidenceError(ErrorCodes.TimeDescending,
                    $"event #{i} ({d.Kind}) at {at:O} precedes earlier event at {lastTime:O}", i));
            var timelineLast = current.Timeline.Count == 0 ? (DateTimeOffset?)null : current.Timeline.Max(e => e.OccurredAt);
            if (timelineLast is not null && at < timelineLast.Value)
                errors.Add(new EvidenceError(ErrorCodes.TimeDescending,
                    $"event #{i} ({d.Kind}) at {at:O} precedes last logged event at {timelineLast:O}", i));
            lastTime = at;

            ValidateShape(d, i, errors);

            // a disposable tip may be attached and used at most once across the whole
            // submission boundary (even if the attachment is in the same batch)
            if (d.Kind is LabEventType.Transfer && !string.IsNullOrWhiteSpace(d.TipId)
                && !tipUsedInBatch.Add(d.TipId!.Trim()))
            {
                errors.Add(new EvidenceError(ErrorCodes.TipReused,
                    $"tip {d.TipId} used more than once inside the same batch", i));
            }
            if (d.Kind is LabEventType.TipAttach && !string.IsNullOrWhiteSpace(d.TipId)
                && current.Tips.ContainsKey(d.TipId.Trim()))
            {
                errors.Add(new EvidenceError(ErrorCodes.TipReused,
                    $"tip {d.TipId} already attached earlier in the log", i));
            }

            if (d.Kind == LabEventType.Correction)
            {
                if (string.IsNullOrWhiteSpace(d.SupersedesEventId))
                    errors.Add(new EvidenceError(ErrorCodes.Validation, $"event #{i}: correction requires supersedesEventId", i));
                else if (!current.RawById.ContainsKey(d.SupersedesEventId))
                    errors.Add(new EvidenceError(ErrorCodes.CorrectionTargetMissing,
                        $"event #{i}: no event '{d.SupersedesEventId}' to supersede", i));
                if (string.IsNullOrWhiteSpace(d.Reason))
                    errors.Add(new EvidenceError(ErrorCodes.CorrectionWithoutReason,
                        $"event #{i}: correction requires a reason", i));
                var original = d.SupersedesEventId is null ? null : current.RawById.GetValueOrDefault(d.SupersedesEventId);
                if (original?.Kind == LabEventType.Correction)
                    errors.Add(new EvidenceError(ErrorCodes.CorrectionOfCorrection,
                        $"event #{i}: cannot correct another correction", i));
                if (d.Replacement is not null && d.Replacement.Kind == LabEventType.Correction)
                    errors.Add(new EvidenceError(ErrorCodes.Validation,
                        $"event #{i}: replacement must not itself be a correction", i));
            }
        }

        return errors;
    }

    private static void ValidateShape(EventDraft d, int i, List<EvidenceError> errors)
    {
        void Bad(string msg) => errors.Add(new EvidenceError(ErrorCodes.Validation, $"event #{i} ({d.Kind}): {msg}", i));

        switch (d.Kind)
        {
            case LabEventType.PlateRegister:
                if (string.IsNullOrWhiteSpace(d.PlateId)) Bad("plateId required");
                if (d.Rows is < 1 or > 100 || d.Cols is < 1 or > 100) Bad("rows/cols must be within 1..100");
                break;
            case LabEventType.SampleAlias:
                if (MissingWell(d)) Bad("plateId+well required");
                if (string.IsNullOrWhiteSpace(d.Alias)) Bad("alias required");
                break;
            case LabEventType.ReagentBatchRegister:
                if (string.IsNullOrWhiteSpace(d.ReagentBatch)) Bad("reagentBatch required");
                break;
            case LabEventType.ReagentBatchFreeze:
                if (string.IsNullOrWhiteSpace(d.ReagentBatch)) Bad("reagentBatch required");
                break;
            case LabEventType.TipAttach:
            case LabEventType.TipDiscard:
                if (string.IsNullOrWhiteSpace(d.TipId)) Bad("tipId required");
                break;
            case LabEventType.LoadSample:
                if (MissingWell(d)) Bad("plateId+well required");
                CheckVolume(d, errors, i);
                break;
            case LabEventType.AddReagent:
                if (MissingWell(d)) Bad("plateId+well required");
                if (string.IsNullOrWhiteSpace(d.ReagentBatch)) Bad("reagentBatch required");
                CheckVolume(d, errors, i);
                break;
            case LabEventType.Transfer:
                if (d.FromPlate is null || d.FromWell is null || d.ToPlate is null || d.ToWell is null)
                    Bad("fromPlate/fromWell/toPlate/toWell required");
                else if (d.FromPlate == d.ToPlate &&
                         string.Equals(d.FromWell, d.ToWell, StringComparison.OrdinalIgnoreCase))
                    Bad("source and destination must differ");
                if (d.AspirateLow is < 0 || d.AspirateHigh is < 0)
                    errors.Add(new EvidenceError(ErrorCodes.VolumeInvalid, $"event #{i}: aspirate must be non-negative", i));
                if (d.AspirateLow.HasValue && d.AspirateHigh.HasValue && d.AspirateLow > d.AspirateHigh)
                    errors.Add(new EvidenceError(ErrorCodes.VolumeInvalid, $"event #{i}: aspirate interval inverted", i));
                if (d.AspirateLow is null && d.AspirateHigh is null)
                    errors.Add(new EvidenceError(ErrorCodes.VolumeInvalid,
                        $"event #{i}: aspirate volume fully unknown; record an estimate interval", i));
                if (!string.IsNullOrWhiteSpace(d.Unit)) { /* tip/transfer has no reading unit */ }
                break;
            case LabEventType.Mix:
                if (MissingWell(d)) Bad("plateId+well required");
                break;
            case LabEventType.Reading:
                if (MissingWell(d)) Bad("plateId+well required");
                if (d.Value is null) Bad("value required");
                if (string.IsNullOrWhiteSpace(d.Unit))
                    errors.Add(new EvidenceError(ErrorCodes.UnitUnknown, $"event #{i}: reading unit required", i));
                else if (!Units.IsKnown(d.Unit!))
                    errors.Add(new EvidenceError(ErrorCodes.UnitUnknown, $"event #{i}: unit '{d.Unit}' not in registry", i));
                if (d.ThresholdHigh is < 0) Bad("threshold must be non-negative");
                break;
            case LabEventType.PlateFreeze:
                if (string.IsNullOrWhiteSpace(d.PlateId)) Bad("plateId required");
                break;
            case LabEventType.Correction:
                // validated by caller
                break;
            default:
                Bad($"unsupported event kind {d.Kind}");
                break;
        }
    }

    private static bool MissingWell(EventDraft d) =>
        string.IsNullOrWhiteSpace(d.PlateId) || string.IsNullOrWhiteSpace(d.Well);

    private static void CheckVolume(EventDraft d, List<EvidenceError> errors, int i)
    {
        if (d.VolumeLow is < 0 || d.VolumeHigh is < 0)
            errors.Add(new EvidenceError(ErrorCodes.VolumeInvalid, $"event #{i}: volume must be non-negative", i));
        if (d.VolumeLow.HasValue && d.VolumeHigh.HasValue && d.VolumeLow > d.VolumeHigh)
            errors.Add(new EvidenceError(ErrorCodes.VolumeInvalid, $"event #{i}: volume interval inverted", i));
        if (d.VolumeLow is null && d.VolumeHigh is null)
            errors.Add(new EvidenceError(ErrorCodes.VolumeInvalid,
                $"event #{i}: volume fully unknown; record an estimate interval", i));
    }
}
