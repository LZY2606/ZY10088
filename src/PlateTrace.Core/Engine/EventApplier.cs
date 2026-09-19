using PlateTrace.Core.Models;

namespace PlateTrace.Core.Engine;

/// <summary>
/// Applies one effective-timeline event to the projection. Volumes are
/// <see cref="Interval"/>s: a missing bound means "unknown" and is never treated
/// as zero. A transfer is only accepted when aspiration cannot exceed the
/// guaranteed available volume; otherwise an evidence error is raised and the
/// liquids are not moved.
/// </summary>
internal sealed class EventApplier
{
    private readonly Projection _p;
    private readonly BuildOptions _options;

    public EventApplier(Projection p, BuildOptions options)
    {
        _p = p;
        _options = options;
    }

    public void Add(string code, LabEvent e, string message, EvidenceSeverity severity = EvidenceSeverity.Error)
        => _p.Errors.Add(new EvidenceError(code, message, null, e.EventId) { Severity = severity });

    public void Apply(LabEvent e)
    {
        switch (e.Kind)
        {
            case LabEventType.PlateRegister: ApplyPlateRegister(e); break;
            case LabEventType.SampleAlias: ApplySampleAlias(e); break;
            case LabEventType.ReagentBatchRegister: ApplyReagentRegister(e); break;
            case LabEventType.ReagentBatchFreeze: ApplyReagentFreeze(e); break;
            case LabEventType.TipAttach: ApplyTipAttach(e); break;
            case LabEventType.TipDiscard: ApplyTipDiscard(e); break;
            case LabEventType.LoadSample: ApplyLoadSample(e); break;
            case LabEventType.AddReagent: ApplyAddReagent(e); break;
            case LabEventType.Transfer: ApplyTransfer(e); break;
            case LabEventType.Mix: ApplyMix(e); break;
            case LabEventType.Reading: ApplyReading(e); break;
            case LabEventType.PlateFreeze: ApplyPlateFreeze(e); break;
        }
    }

    private void ApplyPlateRegister(LabEvent e)
    {
        var id = e.PlateId?.Trim();
        if (string.IsNullOrWhiteSpace(id)) { Add(ErrorCodes.Validation, e, "PlateRegister requires plateId"); return; }
        var rows = e.Rows ?? 8;
        var cols = e.Cols ?? 12;
        if (rows <= 0 || cols <= 0) { Add(ErrorCodes.PlateLayoutInvalid, e, $"plate {id} layout {rows}x{cols} is invalid"); return; }
        if (_p.Plates.ContainsKey(id)) { Add(ErrorCodes.PlateDuplicate, e, $"plate {id} already registered"); return; }
        _p.Plates[id] = new PlateInfo { PlateId = id, Rows = rows, Cols = cols, CreatedAt = e.OccurredAt };
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                var wref = new WellRef(id, PlateCoords.Format(r, c));
                _p.Wells[wref.Id] = new WellState { Ref = wref, Volume = Interval.Zero };
            }
    }

    private void ApplySampleAlias(LabEvent e)
    {
        var w = ResolveWell(e, out var ok);
        if (!ok) return;
        var well = _p.RequireWell(w);
        if (!string.IsNullOrWhiteSpace(e.Alias)) well.SampleAlias = e.Alias!.Trim();
    }

    private void ApplyReagentRegister(LabEvent e)
    {
        var batch = e.ReagentBatch?.Trim();
        if (string.IsNullOrWhiteSpace(batch)) { Add(ErrorCodes.Validation, e, "ReagentBatchRegister requires reagentBatch"); return; }
        if (_p.ReagentBatches.ContainsKey(batch)) { Add(ErrorCodes.ReagentDuplicate, e, $"reagent batch {batch} already registered"); return; }
        _p.ReagentBatches[batch] = new ReagentBatchInfo
        {
            Batch = batch, Name = e.ReagentName?.Trim(), RegisteredAt = e.OccurredAt,
        };
    }

    private void ApplyReagentFreeze(LabEvent e)
    {
        var batch = e.ReagentBatch?.Trim();
        if (batch is null || !_p.ReagentBatches.TryGetValue(batch, out var info))
        { Add(ErrorCodes.ReagentUnknown, e, $"freeze references unknown reagent batch '{batch}'"); return; }
        if (info.IsFrozen) { Add(ErrorCodes.ReagentAlreadyFrozen, e, $"reagent batch {batch} already frozen at {info.FrozenVersion}"); return; }
        info.FrozenAt = e.OccurredAt;
        info.FrozenVersion = string.IsNullOrWhiteSpace(e.FrozenVersion) ? $"v@${e.OccurredAt:yyyyMMddHHmmss}" : e.FrozenVersion!.Trim();
    }

    private void ApplyTipAttach(LabEvent e)
    {
        var tip = e.TipId?.Trim();
        if (string.IsNullOrWhiteSpace(tip)) { Add(ErrorCodes.Validation, e, "TipAttach requires tipId"); return; }
        if (!_p.Tips.TryGetValue(tip, out var life))
        {
            life = new TipLife { TipId = tip };
            _p.Tips[tip] = life;
        }
        else if (life.UseSeqs.Count > 0 || life.Discarded)
        {
            // re-attaching the same disposable tip id is itself a reuse violation
            _p.Errors.Add(new EvidenceError(ErrorCodes.TipReused,
                $"disposable tip {tip} attached again after use/discard", null, e.EventId) { TipId = tip });
        }
    }

    private void ApplyTipDiscard(LabEvent e)
    {
        var tip = e.TipId?.Trim();
        if (string.IsNullOrWhiteSpace(tip)) { Add(ErrorCodes.Validation, e, "TipDiscard requires tipId"); return; }
        if (!_p.Tips.TryGetValue(tip, out var life))
        { Add(ErrorCodes.TipUnknown, e, $"discard references unattached tip {tip}"); return; }
        life.Discarded = true;
    }

    private void ApplyLoadSample(LabEvent e)
    {
        var w = ResolveWell(e, out var ok);
        if (!ok) return;
        if (FrozenGuard(e, w)) return;
        if (!TryVolume(e.VolumeLow, e.VolumeHigh, "load", e, out var vol)) return;
        var well = _p.RequireWell(w);
        well.Volume += vol;
        if (!string.IsNullOrWhiteSpace(e.Alias)) well.SampleAlias = e.Alias!.Trim();
    }

    private void ApplyAddReagent(LabEvent e)
    {
        var w = ResolveWell(e, out var ok);
        if (!ok) return;
        if (FrozenGuard(e, w)) return;
        var batch = e.ReagentBatch?.Trim();
        if (batch is null || !_p.ReagentBatches.ContainsKey(batch))
        { Add(ErrorCodes.ReagentUnknown, e, $"AddReagent references unknown batch '{batch}'"); return; }
        if (!TryVolume(e.VolumeLow, e.VolumeHigh, "reagent", e, out var vol)) return;
        var well = _p.RequireWell(w);
        well.Volume += vol;
        if (!well.ReagentBatches.Contains(batch)) well.ReagentBatches.Add(batch);
        _p.ReagentEdges.Add(new ReagentEdge(e.Seq, e.EventId, batch,
            _p.ReagentBatches[batch].Name, w, vol, e.OccurredAt, e.IsReplacement));
    }

    private void ApplyTransfer(LabEvent e)
    {
        if (e.FromPlate is null || e.FromWell is null || e.ToPlate is null || e.ToWell is null)
        { Add(ErrorCodes.Validation, e, "Transfer requires fromPlate/fromWell/toPlate/toWell"); return; }
        var from = CheckWell(e.FromPlate, e.FromWell, e, "source");
        var to = CheckWell(e.ToPlate, e.ToWell, e, "destination");
        if (from is null || to is null) return;
        if (FrozenGuard(e, to)) return;

        // a well-coordinate shared by two plates must not be confused with a self-loop
        if (from.Id.Equals(to.Id, StringComparison.Ordinal))
        { Add(ErrorCodes.Validation, e, "transfer source and destination are the same well"); return; }

        var src = _p.RequireWell(from);
        var dst = _p.RequireWell(to);

        if (!CheckTip(e, out var tip)) return;

        if (!TryVolume(e.AspirateLow, e.AspirateHigh, "aspirate", e, out var aspirate)) return;

        // feasibility: maximum aspirated volume must be <= minimum available volume
        if (aspirate.High is { } high && src.Volume.Low is { } low && high > low + 1e-9)
        {
            Add(ErrorCodes.AspirateExceedsVolume, e,
                $"aspirate {aspirate} uL from {from.Id} cannot be guaranteed available: volume {src.Volume} uL");
            return;
        }

        var fraction = new Interval(aspirate.FractionLowOf(src.Volume), aspirate.FractionHighOf(src.Volume));
        if (fraction.Low is < 0) fraction = new Interval(0, fraction.High);

        src.Volume -= aspirate;
        dst.Volume += aspirate;
        if (tip is not null && _p.Tips.TryGetValue(tip, out var life)) life.UseSeqs.Add(e.Seq);

        _p.TransferEdges.Add(new TransferEdge(e.Seq, e.EventId, from, to,
            fraction, aspirate, tip, e.OccurredAt, e.IsReplacement));
    }

    private void ApplyMix(LabEvent e)
    {
        var w = ResolveWell(e, out var ok);
        if (!ok) return;
        if (FrozenGuard(e, w)) return;
        _p.RequireWell(w).LastMixAt = e.OccurredAt;
    }

    private void ApplyReading(LabEvent e)
    {
        var w = ResolveWell(e, out var ok);
        if (!ok) return;
        // readings do not mutate liquid; frozen plates may still be read
        var unit = e.Unit?.Trim();
        if (unit is null || !Units.IsKnown(unit)) { Add(ErrorCodes.UnitUnknown, e, $"reading unit '{unit}' is not in the registry"); return; }
        if (e.Value is not double v) { Add(ErrorCodes.Validation, e, "Reading requires value"); return; }
        var analyte = string.IsNullOrWhiteSpace(e.Analyte) ? "default" : e.Analyte!.Trim();
        var source = string.IsNullOrWhiteSpace(e.Source) ? "unnamed-source" : e.Source!.Trim();

        // explicit conversion into the canonical base unit of the unit's dimension;
        // the threshold is declared in the reading's own unit and converts alongside it
        var def = Units.Get(unit);
        var baseUnit = Units.BaseUnit(def.Dimension);
        var valueBase = Units.Convert(v, unit, baseUnit);
        double? threshold = e.ThresholdHigh is double th
            ? Units.Convert(th, unit, baseUnit)
            : null;
        var record = new ReadingRecord
        {
            Seq = e.Seq, EventId = e.EventId, OccurredAt = e.OccurredAt, Analyte = analyte,
            Source = source, OriginalUnit = unit, OriginalValue = v,
            ValueBase = valueBase, BaseUnit = baseUnit, ThresholdHigh = threshold,
        };
        _p.RequireWell(w).Readings.Add(record);
    }

    private void ApplyPlateFreeze(LabEvent e)
    {
        var id = e.PlateId?.Trim();
        if (id is null || !_p.Plates.TryGetValue(id, out var plate))
        { Add(ErrorCodes.UnknownPlate, e, $"freeze references unknown plate '{id}'"); return; }
        if (plate.FrozenAt is not null) return;
        _p.Plates[id] = new PlateInfo
        {
            PlateId = plate.PlateId, Rows = plate.Rows, Cols = plate.Cols,
            CreatedAt = plate.CreatedAt, FrozenAt = e.OccurredAt,
        };
    }

    // ---- helpers -----------------------------------------------------------

    private bool CheckTip(LabEvent e, out string? tip)
    {
        tip = e.TipId?.Trim();
        if (string.IsNullOrWhiteSpace(tip)) { tip = null; return true; } // bare transfer allowed
        if (!_p.Tips.TryGetValue(tip, out var life))
        { Add(ErrorCodes.TipUnknown, e, $"transfer uses unattached tip {tip}"); return false; }
        if (life.Discarded)
        {
            _p.Errors.Add(new EvidenceError(ErrorCodes.TipDiscardedReused,
                $"disposable tip {tip} reused after discard", null, e.EventId) { TipId = tip });
            return false;
        }
        if (life.UseSeqs.Count > 0)
        {
            _p.Errors.Add(new EvidenceError(ErrorCodes.TipReused,
                $"disposable tip {tip} reused across {life.UseSeqs.Count + 1} transfers", null, e.EventId) { TipId = tip });
            return false;
        }
        return true;
    }

    private bool FrozenGuard(LabEvent e, WellRef w)
    {
        if (_p.IsFrozenPlate(w.PlateId))
        { Add(ErrorCodes.FrozenPlateMutation, e, $"cannot {e.Kind} on frozen plate {w.PlateId} (well {w.Well})"); return true; }
        return false;
    }

    private bool TryVolume(double? low, double? high, string what, LabEvent e, out Interval vol)
    {
        vol = Interval.Unknown;
        if (low is < 0 || high is < 0)
        { Add(ErrorCodes.VolumeInvalid, e, $"{what} volume must be non-negative"); return false; }
        if (low.HasValue && high.HasValue && low.Value > high.Value)
        { Add(ErrorCodes.VolumeInvalid, e, $"{what} volume interval [{low},{high}] is inverted"); return false; }
        vol = new Interval(low, high);
        if (vol.IsUnknown) { Add(ErrorCodes.VolumeInvalid, e, $"{what} volume is completely unknown; record an estimate interval"); return false; }
        return true;
    }

    private WellRef? ResolveWell(LabEvent e, out bool ok)
    {
        if (e.PlateId is null || e.Well is null)
        { Add(ErrorCodes.Validation, e, $"event {e.Kind} requires plateId and well"); ok = false; return null; }
        var r = CheckWell(e.PlateId, e.Well, e, "well");
        ok = r is not null;
        return r;
    }

    private WellRef? CheckWell(string plateId, string well, LabEvent e, string role)
    {
        var id = plateId.Trim();
        var w = well.Trim().ToUpperInvariant();
        if (!_p.Plates.TryGetValue(id, out var plate))
        { Add(ErrorCodes.UnknownPlate, e, $"{role} references unknown plate '{id}'"); return null; }
        if (!PlateCoords.TryParse(w, plate.Rows, plate.Cols, out var row, out var col))
        { Add(ErrorCodes.WellOutOfRange, e, $"{role} well {w} outside plate {id} layout {plate.Rows}x{plate.Cols}"); return null; }
        return new WellRef(id, PlateCoords.Format(row, col));
    }
}
