using PlateTrace.Core.Engine;
using PlateTrace.Core.Models;
using Xunit;
using static PlateTrace.Core.Tests.Scenario;

namespace PlateTrace.Core.Tests;

public class TraceAndHypothesisTests
{
    private static BatchSubmission Batch(params EventDraft[] events) =>
        new() { IdempotencyKey = Guid.NewGuid().ToString("N"), Events = events.ToList() };

    private static async Task<(TraceService svc, string transferEventId, string abnormalWell)> BuildSerialDilutionAsync()
    {
        var svc = new TraceService(new InMemoryStore());
        await svc.InitializeAsync();
        var t = Base;

        await svc.SubmitAsync(Batch(
            Plate("P1", at: t), Plate("P2", at: t.AddMinutes(1)),
            Reagent("DIL-A", at: t.AddMinutes(2)),
            Load("P1", "A1", 200, alias: "raw", at: t.AddMinutes(3)),
            AddDiluent("P1", "B1", "DIL-A", 180, at: t.AddMinutes(4)),
            AddDiluent("P2", "A1", "DIL-A", 180, at: t.AddMinutes(5))));

        var moves = await svc.SubmitAsync(Batch(
            new EventDraft { Kind = LabEventType.TipAttach, TipId = "T1", OccurredAt = t.AddMinutes(6) },
            Transfer("P1", "A1", "P1", "B1", 20, tip: "T1", at: t.AddMinutes(7)),
            Mix("P1", "B1", t.AddMinutes(8)),
            new EventDraft { Kind = LabEventType.TipAttach, TipId = "T2", OccurredAt = t.AddMinutes(9) },
            Transfer("P1", "B1", "P2", "A1", 20, tip: "T2", at: t.AddMinutes(10)),
            Mix("P2", "A1", t.AddMinutes(11))));
        Assert.True(moves.Accepted, string.Join(";", moves.Batch.Errors.Select(e => e.Message)));

        var edgeToP2 = moves.Batch.Events.Single(e => e.Kind == LabEventType.Transfer && e.ToPlate == "P2").EventId;
        return (svc, edgeToP2, "P2/A1");
    }

    [Fact]
    public async Task All_Paths_And_Cumulative_Dilution_Intervals_Are_Reported()
    {
        var (svc, _, _) = await BuildSerialDilutionAsync();
        var tracer = new PathTracer(svc.Projection, 1e-9);
        var intoP2 = tracer.PathsInto(new WellRef("P2", "A1"));
        Assert.NotEmpty(intoP2);
        // one-edge prefix (B1->P2) plus the full two-edge path (A1->B1->P2)
        var full = intoP2.Single(p => p.Origin.Id == "P1/A1" && p.Target.Id == "P2/A1" && p.Edges.Count == 2);
        // exact volumes: 20/200 then 20/200 = 0.01
        Assert.Equal(0.01, full.Cumulative.Low!.Value, 6);
        Assert.Equal(0.01, full.Cumulative.High!.Value, 6);
        Assert.Equal(2, full.Edges.Count);
    }

    [Fact]
    public async Task Paths_Below_Threshold_Are_Filtered_Out()
    {
        var (svc, _, _) = await BuildSerialDilutionAsync();
        var tracer = new PathTracer(svc.Projection, minFraction: 0.5);
        Assert.Empty(tracer.PathsInto(new WellRef("P2", "A1")));
    }

    [Fact]
    public async Task CarryOver_Hypothesis_Explains_Abnormal_And_Predicts_Extra_Wells()
    {
        var (svc, firstEdge, _) = await BuildSerialDilutionAsync();
        var t = Base;
        // mark the final well abnormal and a middle well normal
        await svc.SubmitAsync(Batch(
            Read("P1", "B1", value: 2, unit: "pg/mL", threshold: 500, at: t.AddMinutes(12)),
            Read("P2", "A1", value: 900, unit: "pg/mL", threshold: 500, at: t.AddMinutes(13))));

        var firstTransfer = svc.Projection.TransferEdges[0].EventId;
        // source contaminant 100000 pg/mL, carry fraction 0.01 -> 1000 injected into B1;
        // downstream dilution 0.1 -> 100 pg/mL, above the 500 threshold only if carry 0.1.
        // Use carry 0.1: B1 gets 10000 (>=500), P2/A1 gets 1000 (>=500).
        var h = new HypothesisRecord
        {
            HypothesisId = "H1", Version = 1, Kind = HypothesisKind.CarryOver,
            TargetEventId = firstTransfer, Analyte = "X",
            ConcentrationLow = 100000, ConcentrationHigh = 100000,
            CarryFractionLow = 0.1, CarryFractionHigh = 0.1, MinPathFraction = 1e-9,
        };
        var ev = new HypothesisEngine(svc.Projection).Evaluate(h, "J1", DateTimeOffset.UtcNow);
        Assert.Contains(ev.ExplainedAnomalies, e => e.WellId == "P2/A1");
        Assert.DoesNotContain(ev.UnexplainedAnomalies, e => e.WellId == "P2/A1");
        // B1 reads normal (2 < 500) but the carry predicts 10000 pg/mL there
        Assert.Contains(ev.ExtraPredictedNormal, e => e.WellId == "P1/B1");
    }

    [Fact]
    public async Task Contaminated_Reagent_Batch_Propagates_To_Every_Downstream_Well()
    {
        var (svc, _, _) = await BuildSerialDilutionAsync();
        var h = new HypothesisRecord
        {
            HypothesisId = "H2", Version = 1, Kind = HypothesisKind.ContaminatedReagent,
            ReagentBatch = "DIL-A", Analyte = "X",
            ConcentrationLow = 10000, ConcentrationHigh = 10000, MinPathFraction = 1e-9,
        };
        var ev = new HypothesisEngine(svc.Projection).Evaluate(h, "J2", DateTimeOffset.UtcNow);
        // both diluent-fed wells are reachable
        var all = ev.ExplainedAnomalies.Concat(ev.ExtraPredictedNormal).Concat(ev.ExtraPredictedUnread)
            .Select(e => e.WellId).ToHashSet();
        Assert.Contains("P1/B1", all);
        Assert.Contains("P2/A1", all);
    }

    [Fact]
    public async Task Two_Readings_Same_Value_Different_Units_Are_Consistent_After_Conversion()
    {
        var svc = new TraceService(new InMemoryStore());
        await svc.InitializeAsync();
        var t = Base;
        await svc.SubmitAsync(Batch(Plate("P1", at: t)));
        await svc.SubmitAsync(Batch(
            Read("P1", "A1", 10, unit: "ng/mL", source: "reader-1", threshold: 20000, at: t.AddMinutes(1)),
            Read("P1", "A1", 10000, unit: "pg/mL", source: "reader-2", threshold: 20000, at: t.AddMinutes(2))));
        var comparisons = svc.BuildReadingComparisons();
        Assert.Single(comparisons);
        Assert.Equal(ReadingConsistency.Consistent, comparisons[0].Consistency);
        Assert.DoesNotContain(svc.Projection.Errors, e => e.Code == Engine.ErrorCodes.ReadingConflict);
    }

    [Fact]
    public async Task Conflicting_Readings_Produce_Evidence_Error()
    {
        var svc = new TraceService(new InMemoryStore());
        await svc.InitializeAsync();
        var t = Base;
        await svc.SubmitAsync(Batch(Plate("P1", at: t)));
        await svc.SubmitAsync(Batch(
            Read("P1", "A1", 10000, unit: "pg/mL", source: "reader-1", at: t.AddMinutes(1)),
            Read("P1", "A1", 1000, unit: "pg/mL", source: "reader-2", at: t.AddMinutes(2))));
        Assert.Contains(svc.Projection.Errors, e => e.Code == Engine.ErrorCodes.ReadingConflict);
    }

    [Fact]
    public async Task Idempotent_Retry_Replays_Without_Duplicate_Events()
    {
        var svc = new TraceService(new InMemoryStore());
        await svc.InitializeAsync();
        var t = Base;
        var sub = new BatchSubmission { IdempotencyKey = "fixed-key", Events = { Plate("P1", at: t) } };
        var r1 = await svc.SubmitAsync(sub);
        var r2 = await svc.SubmitAsync(sub);
        Assert.True(r1.Accepted);
        Assert.True(r2.IdempotentReplay);
        Assert.Equal(r1.Batch.BatchId, r2.Batch.BatchId);
        var batches = await svc.ListBatchesAsync();
        Assert.Single(batches);
        Assert.Single(svc.Projection.Plates);
    }

    [Fact]
    public async Task Correction_Supersedes_Working_Projection_But_Raw_Log_Stays()
    {
        var svc = new TraceService(new InMemoryStore());
        await svc.InitializeAsync();
        var t = Base;
        var bad = await svc.SubmitAsync(new BatchSubmission
        {
            IdempotencyKey = "b1",
            Events = { Plate("P1", at: t), Load("P1", "A1", 100, at: t.AddMinutes(1)) },
        });
        var loadId = bad.Batch.Events.Single(e => e.Kind == LabEventType.LoadSample).EventId;

        var corr = await svc.SubmitAsync(new BatchSubmission
        {
            IdempotencyKey = "b2",
            Events =
            {
                new EventDraft
                {
                    Kind = LabEventType.Correction, OccurredAt = t.AddMinutes(5),
                    SupersedesEventId = loadId, Reason = "实际录入 250 uL（电子秤复核）",
                    Replacement = new EventDraft
                    {
                        Kind = LabEventType.LoadSample, PlateId = "P1", Well = "A1",
                        VolumeLow = 250, VolumeHigh = 250,
                    },
                },
            },
        });
        Assert.True(corr.Accepted, string.Join(";", corr.Batch.Errors.Select(e => e.Message)));
        Assert.Equal(250, svc.Projection.RequireWell(new WellRef("P1", "A1")).Volume.Low);
        // raw log still carries the original 100 uL event
        var batches = await svc.ListBatchesAsync();
        var raw = batches.SelectMany(b => b.Events).Single(e => e.EventId == loadId);
        Assert.Equal(100, raw.VolumeLow);
        Assert.Contains(loadId, svc.Projection.SupersededEventIds);
    }
}
