using PlateTrace.Core.Engine;
using PlateTrace.Core.Models;

namespace PlateTrace.Core.Tests;

internal static class Scenario
{
    public static DateTimeOffset Base => new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    public static BatchSubmission Setup(DateTimeOffset? t0 = null, params string[] plates)
    {
        var t = t0 ?? Base;
        var sub = new BatchSubmission { IdempotencyKey = null, Events = new() };
        var m = 0;
        foreach (var plate in plates)
        {
            sub.Events.Add(new EventDraft { Kind = LabEventType.PlateRegister, PlateId = plate, Rows = 4, Cols = 6, OccurredAt = t.AddMinutes(m++) });
        }
        return sub;
    }

    public static EventDraft Plate(string id, int rows = 4, int cols = 6, DateTimeOffset? at = null) =>
        new() { Kind = LabEventType.PlateRegister, PlateId = id, Rows = rows, Cols = cols, OccurredAt = at ?? Base };

    public static EventDraft Reagent(string batch, string? name = null, DateTimeOffset? at = null) =>
        new() { Kind = LabEventType.ReagentBatchRegister, ReagentBatch = batch, ReagentName = name, OccurredAt = at ?? Base.AddMinutes(1) };

    public static EventDraft Load(string plate, string well, double lo, double? hi = null, string? alias = null, DateTimeOffset? at = null) =>
        new() { Kind = LabEventType.LoadSample, PlateId = plate, Well = well, VolumeLow = lo, VolumeHigh = hi ?? lo, Alias = alias, OccurredAt = at ?? Base.AddMinutes(2) };

    public static EventDraft AddDiluent(string plate, string well, string batch, double lo, double? hi = null, DateTimeOffset? at = null) =>
        new() { Kind = LabEventType.AddReagent, PlateId = plate, Well = well, ReagentBatch = batch, VolumeLow = lo, VolumeHigh = hi ?? lo, OccurredAt = at ?? Base.AddMinutes(3) };

    public static EventDraft Tip(string id, DateTimeOffset? at = null) =>
        new() { Kind = LabEventType.TipAttach, TipId = id, OccurredAt = at ?? Base.AddMinutes(4) };

    public static EventDraft Transfer(string fp, string fw, string tp, string tw, double alo, double? ahi = null,
        string? tip = null, DateTimeOffset? at = null) =>
        new()
        {
            Kind = LabEventType.Transfer, FromPlate = fp, FromWell = fw, ToPlate = tp, ToWell = tw,
            AspirateLow = alo, AspirateHigh = ahi ?? alo, TipId = tip, OccurredAt = at ?? Base.AddMinutes(5),
        };

    public static EventDraft Mix(string plate, string well, DateTimeOffset? at = null) =>
        new() { Kind = LabEventType.Mix, PlateId = plate, Well = well, OccurredAt = at ?? Base.AddMinutes(6) };

    public static EventDraft Read(string plate, string well, double value, string unit = "pg/mL",
        double? threshold = null, string source = "reader-1", string analyte = "X", DateTimeOffset? at = null) =>
        new()
        {
            Kind = LabEventType.Reading, PlateId = plate, Well = well, Value = value, Unit = unit,
            ThresholdHigh = threshold, Source = source, Analyte = analyte, OccurredAt = at ?? Base.AddMinutes(7),
        };

    public static EventDraft FreezeReagent(string batch, string version, DateTimeOffset? at = null) =>
        new() { Kind = LabEventType.ReagentBatchFreeze, ReagentBatch = batch, FrozenVersion = version, OccurredAt = at ?? Base.AddMinutes(8) };

    public static EventDraft FreezePlate(string plate, DateTimeOffset? at = null) =>
        new() { Kind = LabEventType.PlateFreeze, PlateId = plate, OccurredAt = at ?? Base.AddMinutes(9) };
}
