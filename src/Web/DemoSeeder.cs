using PlateTrace.Core.Engine;
using PlateTrace.Core.Models;

namespace PlateTrace.Web;

/// <summary>
/// Loads a small, fully valid two-plate scenario on first run so the UI has
/// data to explore: serial dilution across plates, one normal and one abnormal
/// reading, a ready-made carry-over hypothesis candidate.
/// </summary>
public static class DemoSeeder
{
    public static async Task SeedAsync(TraceService service, CancellationToken ct = default)
    {
        var batches = await service.ListBatchesAsync(ct).ConfigureAwait(false);
        if (batches.Count > 0) return;

        var t0 = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

        await service.SubmitAsync(new BatchSubmission
        {
            IdempotencyKey = "demo-setup",
            Note = "demo: register two plates, samples, diluent and tips",
            Events =
            {
                new EventDraft { Kind = LabEventType.PlateRegister, PlateId = "P1", Rows = 8, Cols = 12, OccurredAt = t0 },
                new EventDraft { Kind = LabEventType.PlateRegister, PlateId = "P2", Rows = 8, Cols = 12, OccurredAt = t0.AddMinutes(1) },
                new EventDraft { Kind = LabEventType.ReagentBatchRegister, ReagentBatch = "DIL-A", ReagentName = "diluent lot A", OccurredAt = t0.AddMinutes(2) },
                new EventDraft { Kind = LabEventType.LoadSample, PlateId = "P1", Well = "A1", Alias = "S-raw", VolumeLow = 200, VolumeHigh = 200, OccurredAt = t0.AddMinutes(3) },
                new EventDraft { Kind = LabEventType.AddReagent, PlateId = "P1", Well = "B1", ReagentBatch = "DIL-A", VolumeLow = 180, VolumeHigh = 180, OccurredAt = t0.AddMinutes(4) },
                new EventDraft { Kind = LabEventType.AddReagent, PlateId = "P2", Well = "A1", ReagentBatch = "DIL-A", VolumeLow = 180, VolumeHigh = 180, OccurredAt = t0.AddMinutes(5) },
            },
        }, ct).ConfigureAwait(false);

        await service.SubmitAsync(new BatchSubmission
        {
            IdempotencyKey = "demo-transfer",
            Note = "demo: serial dilution P1/A1 -> P1/B1 -> P2/A1 with a fresh tip each",
            Events =
            {
                new EventDraft { Kind = LabEventType.TipAttach, TipId = "T1", OccurredAt = t0.AddMinutes(6) },
                new EventDraft { Kind = LabEventType.Transfer, FromPlate = "P1", FromWell = "A1", ToPlate = "P1", ToWell = "B1",
                    TipId = "T1", AspirateLow = 20, AspirateHigh = 20, OccurredAt = t0.AddMinutes(7) },
                new EventDraft { Kind = LabEventType.Mix, PlateId = "P1", Well = "B1", OccurredAt = t0.AddMinutes(8) },
                new EventDraft { Kind = LabEventType.TipAttach, TipId = "T2", OccurredAt = t0.AddMinutes(9) },
                new EventDraft { Kind = LabEventType.Transfer, FromPlate = "P1", FromWell = "B1", ToPlate = "P2", ToWell = "A1",
                    TipId = "T2", AspirateLow = 20, AspirateHigh = 20, OccurredAt = t0.AddMinutes(10) },
                new EventDraft { Kind = LabEventType.Mix, PlateId = "P2", Well = "A1", OccurredAt = t0.AddMinutes(11) },
                new EventDraft { Kind = LabEventType.Reading, PlateId = "P1", Well = "A1", Analyte = "X", Source = "reader-1",
                    Value = 5000, Unit = "pg/mL", ThresholdHigh = 500, OccurredAt = t0.AddMinutes(12) },
                new EventDraft { Kind = LabEventType.Reading, PlateId = "P2", Well = "A1", Analyte = "X", Source = "reader-1",
                    Value = 30, Unit = "pg/mL", ThresholdHigh = 500, OccurredAt = t0.AddMinutes(13) },
            },
        }, ct).ConfigureAwait(false);
    }
}
