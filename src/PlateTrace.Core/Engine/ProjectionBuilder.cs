using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlateTrace.Core.Models;
using PlateTrace.Core.Store;

namespace PlateTrace.Core.Engine;

public sealed class BuildOptions
{
    /// <summary>Tolerance for "same reading, different units" comparisons (relative).</summary>
    public double ReadingConflictTolerance { get; init; } = 0.15;
}

/// <summary>
/// Applies the immutable log to a fresh <see cref="Projection"/>. Corrections are
/// materialized into replacement events for the *working* projection; the raw
/// timeline is kept verbatim alongside it.
/// </summary>
public sealed class ProjectionBuilder
{
    private readonly BuildOptions _options;

    public ProjectionBuilder(BuildOptions? options = null) => _options = options ?? new BuildOptions();

    public Projection Build(IReadOnlyList<BatchRecord> batches)
    {
        var p = new Projection();
        var raw = batches.Where(b => b.Outcome == BatchOutcome.Accepted)
            .SelectMany(b => b.Events)
            .OrderBy(e => e.Seq)
            .ToList();

        foreach (var e in raw) p.RawById[e.EventId] = e;

        var effective = new List<LabEvent>(raw.Count);
        LabEvent? last = null;
        foreach (var e in raw)
        {
            if (last is not null && e.OccurredAt < last.OccurredAt)
            {
                p.Errors.Add(new EvidenceError(ErrorCodes.TimeDescending,
                    $"event {e.EventId} at {e.OccurredAt:O} precedes prior event {last.EventId} at {last.OccurredAt:O}",
                    null, e.EventId));
            }

            if (e.Kind == LabEventType.Correction)
            {
                p.Corrections.Add(e);
                ApplyCorrection(p, e, raw);
                last = e;
                continue;
            }
            effective.Add(e);
            last = e;
        }

        // build the effective timeline with replacements standing in for corrected originals
        var timeline = new List<LabEvent>(effective.Count);
        foreach (var e in effective)
        {
            // a corrected original is replaced in-place by its newest correction;
            // without a matching correction the original event is applied unchanged
            var replacement = p.Corrections
                .Where(c => c.SupersedesEventId == e.EventId)
                .OrderByDescending(c => c.OccurredAt)
                .FirstOrDefault();
            timeline.Add(replacement is null ? e : MaterializeReplacement(replacement, e, p.Corrections));
        }
        timeline.Sort((a, b) => a.OccurredAt.CompareTo(b.OccurredAt) == 0
            ? a.Seq.CompareTo(b.Seq)
            : a.OccurredAt.CompareTo(b.OccurredAt));
        p.Timeline.AddRange(timeline);

        var applier = new EventApplier(p, _options);
        LabEvent? prev = null;
        foreach (var e in timeline)
        {
            if (prev is not null && e.OccurredAt < prev.OccurredAt)
                applier.Add(ErrorCodes.TimeDescending, e,
                    $"event {e.EventId} at {e.OccurredAt:O} precedes prior event {prev.EventId}");
            applier.Apply(e);
            prev = e;
        }

        CheckReadingConsistency(p);
        p.LastSeq = timeline.Count == 0 ? 0 : timeline.Max(e => e.Seq);
        p.Fingerprint = ComputeFingerprint(timeline);
        return p;
    }

    private static void ApplyCorrection(Projection p, LabEvent correction, List<LabEvent> raw)
    {
        var targetId = correction.SupersedesEventId;
        if (string.IsNullOrWhiteSpace(targetId) || !p.RawById.TryGetValue(targetId, out var original))
        {
            p.Errors.Add(new EvidenceError(ErrorCodes.CorrectionTargetMissing,
                $"correction {correction.EventId} references unknown event '{targetId}'", null, correction.EventId));
            return;
        }
        if (original.Kind == LabEventType.Correction)
        {
            p.Errors.Add(new EvidenceError(ErrorCodes.CorrectionOfCorrection,
                $"correction {correction.EventId} targets another correction {targetId}", null, correction.EventId));
            return;
        }
        if (p.SupersededEventIds.Contains(targetId)) return; // latest correction wins
        if (string.IsNullOrWhiteSpace(correction.Reason))
        {
            p.Errors.Add(new EvidenceError(ErrorCodes.CorrectionWithoutReason,
                $"correction {correction.EventId} is missing a reason", null, correction.EventId));
        }
        p.SupersededEventIds.Add(targetId);
    }

    private static LabEvent MaterializeReplacement(LabEvent correction, LabEvent original,
        IReadOnlyList<LabEvent> allCorrections)
    {
        var kind = correction.ReplacementKind ?? original.Kind;
        var replacement = new LabEvent
        {
            Seq = original.Seq,
            EventId = original.EventId,
            BatchId = original.BatchId,
            Kind = kind,
            OccurredAt = original.OccurredAt,
            IsReplacement = true,
            SupersedesEventId = correction.EventId,
            Reason = correction.Reason,
        };

        // copy the original fields, then overlay the replacement payload
        CopyFields(original, replacement);
        if (!string.IsNullOrWhiteSpace(correction.ReplacementJson))
        {
            using var doc = JsonDocument.Parse(correction.ReplacementJson);
            Overlay(replacement, doc.RootElement);
        }
        replacement.Kind = kind;
        replacement.IsReplacement = true;
        replacement.SupersedesEventId = correction.EventId;
        replacement.Reason = correction.Reason;

        // if an earlier correction is itself later corrected, chain to the newest
        var chained = allCorrections
            .Where(c => c.SupersedesEventId == correction.EventId)
            .OrderByDescending(c => c.OccurredAt)
            .FirstOrDefault();
        if (chained is not null)
        {
            replacement.SupersedesEventId = chained.EventId;
            if (chained.ReplacementKind is not null) replacement.Kind = chained.ReplacementKind.Value;
            if (!string.IsNullOrWhiteSpace(chained.ReplacementJson))
            {
                using var doc2 = JsonDocument.Parse(chained.ReplacementJson);
                Overlay(replacement, doc2.RootElement);
                replacement.Kind = chained.ReplacementKind ?? replacement.Kind;
            }
        }
        return replacement;
    }

    private static void CopyFields(LabEvent from, LabEvent to)
    {
        to.PlateId = from.PlateId; to.Well = from.Well; to.Rows = from.Rows; to.Cols = from.Cols;
        to.Alias = from.Alias; to.ReagentBatch = from.ReagentBatch; to.ReagentName = from.ReagentName;
        to.FrozenVersion = from.FrozenVersion; to.TipId = from.TipId;
        to.VolumeLow = from.VolumeLow; to.VolumeHigh = from.VolumeHigh;
        to.AspirateLow = from.AspirateLow; to.AspirateHigh = from.AspirateHigh;
        to.FromPlate = from.FromPlate; to.FromWell = from.FromWell;
        to.ToPlate = from.ToPlate; to.ToWell = from.ToWell;
        to.Analyte = from.Analyte; to.Source = from.Source; to.Value = from.Value;
        to.Unit = from.Unit; to.ThresholdHigh = from.ThresholdHigh;
    }

    private static void Overlay(LabEvent e, JsonElement el)
    {
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            switch (prop.Name)
            {
                case "plateId": e.PlateId = prop.Value.GetString(); break;
                case "well": e.Well = prop.Value.GetString(); break;
                case "rows": e.Rows = prop.Value.GetInt32(); break;
                case "cols": e.Cols = prop.Value.GetInt32(); break;
                case "alias": e.Alias = prop.Value.GetString(); break;
                case "reagentBatch": e.ReagentBatch = prop.Value.GetString(); break;
                case "reagentName": e.ReagentName = prop.Value.GetString(); break;
                case "frozenVersion": e.FrozenVersion = prop.Value.GetString(); break;
                case "tipId": e.TipId = prop.Value.GetString(); break;
                case "volumeLow": e.VolumeLow = prop.Value.GetDouble(); break;
                case "volumeHigh": e.VolumeHigh = prop.Value.GetDouble(); break;
                case "aspirateLow": e.AspirateLow = prop.Value.GetDouble(); break;
                case "aspirateHigh": e.AspirateHigh = prop.Value.GetDouble(); break;
                case "fromPlate": e.FromPlate = prop.Value.GetString(); break;
                case "fromWell": e.FromWell = prop.Value.GetString(); break;
                case "toPlate": e.ToPlate = prop.Value.GetString(); break;
                case "toWell": e.ToWell = prop.Value.GetString(); break;
                case "analyte": e.Analyte = prop.Value.GetString(); break;
                case "source": e.Source = prop.Value.GetString(); break;
                case "value": e.Value = prop.Value.GetDouble(); break;
                case "unit": e.Unit = prop.Value.GetString(); break;
                case "thresholdHigh": e.ThresholdHigh = prop.Value.GetDouble(); break;
            }
        }
    }

    private void CheckReadingConsistency(Projection p)
    {
        foreach (var group in p.Wells.Values
                     .SelectMany(w => w.Readings.Select(r => (w: w.Ref.Id, r)))
                     .GroupBy(t => (t.w, t.r.Analyte))
                     .Where(g => g.Count() > 1))
        {
            var readings = group.Select(t => t.r).OrderBy(r => r.OccurredAt).ToList();
            var sources = string.Join(" vs ", readings.Select(r => $"{r.Source}({r.OriginalValue:G4} {r.OriginalUnit})"));
            var (min, max) = (readings.Min(r => r.ValueBase), readings.Max(r => r.ValueBase));
            var rel = min <= 0 ? 0 : (max - min) / max;
            if (rel > _options.ReadingConflictTolerance)
            {
                p.Errors.Add(new EvidenceError(ErrorCodes.ReadingConflict,
                    $"well {group.Key.w} analyte {group.Key.Analyte}: sources disagree after explicit conversion ({sources}; spread {rel:P0})")
                { WellId = group.Key.w });
            }
        }
    }

    private static string ComputeFingerprint(List<LabEvent> timeline)
    {
        using var sha = SHA256.Create();
        var sb = new StringBuilder();
        foreach (var e in timeline)
            sb.Append(e.Seq).Append('|').Append(e.EventId).Append('|').Append(e.IsReplacement ? e.SupersedesEventId : "")
              .Append('|').Append(e.OccurredAt.ToUnixTimeMilliseconds()).Append('\n');
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }
}
