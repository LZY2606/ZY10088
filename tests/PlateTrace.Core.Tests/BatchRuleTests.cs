using PlateTrace.Core.Engine;
using PlateTrace.Core.Models;
using Xunit;
using static PlateTrace.Core.Tests.Scenario;

namespace PlateTrace.Core.Tests;

public class BatchRuleTests
{
    private static async Task<TraceService> NewServiceAsync()
    {
        var svc = new TraceService(new InMemoryStore());
        await svc.InitializeAsync();
        return svc;
    }

    private static BatchSubmission Batch(params EventDraft[] events) =>
        new() { IdempotencyKey = Guid.NewGuid().ToString("N"), Events = events.ToList() };

    [Fact]
    public async Task Same_Coordinate_On_Different_Plates_Is_Distinct_And_Transfer_Succeeds()
    {
        var svc = await NewServiceAsync();
        var t = Base;
        var r = await svc.SubmitAsync(Batch(
            Plate("P1", at: t), Plate("P2", at: t.AddMinutes(1)),
            Load("P1", "A1", 100, at: t.AddMinutes(2)),
            Tip("T1", t.AddMinutes(3)),
            Transfer("P1", "A1", "P2", "A1", 20, tip: "T1", at: t.AddMinutes(4))));
        Assert.True(r.Accepted, string.Join(";", r.Batch.Errors.Select(e => e.Message)));
        Assert.Equal(80, svc.Projection.RequireWell(new WellRef("P1", "A1")).Volume.Low);
        Assert.Equal(20, svc.Projection.RequireWell(new WellRef("P2", "A1")).Volume.High);
        Assert.Equal(2, svc.Projection.Plates.Count);
    }

    [Fact]
    public async Task Aspirate_Exceeding_Available_Volume_Rejects_Whole_Batch()
    {
        var svc = await NewServiceAsync();
        var t = Base;
        var r = await svc.SubmitAsync(Batch(
            Plate("P1", at: t), Plate("P2", at: t.AddMinutes(1)),
            Load("P1", "A1", 50, at: t.AddMinutes(2)),
            Tip("T1", t.AddMinutes(3)),
            Transfer("P1", "A1", "P2", "A1", 80, tip: "T1", at: t.AddMinutes(4)),
            Load("P2", "A1", 999, at: t.AddMinutes(5))));
        Assert.False(r.Accepted);
        Assert.Contains(r.Batch.Errors, e => e.Code == Engine.ErrorCodes.AspirateExceedsVolume);
        // atomicity: nothing applied
        var p2well = svc.Projection.TryWell(new WellRef("P2", "A1"));
        // P2 plate registration was in the rejected batch too, so nothing exists at all
        Assert.Null(p2well);
        Assert.Empty(svc.Projection.TransferEdges);
        Assert.DoesNotContain(svc.Projection.Plates.Keys, k => k == "P2");
    }

    [Fact]
    public async Task Unknown_Volume_Is_Not_Zero_And_Can_Still_Reject_When_Exceeding_Low_Bound()
    {
        var svc = await NewServiceAsync();
        var t = Base;
        // source volume [50, ?]; aspirating exactly 50 low-bound is feasible
        var ok = await svc.SubmitAsync(Batch(
            Plate("P1", at: t), Plate("P2", at: t.AddMinutes(1)),
            new EventDraft { Kind = LabEventType.LoadSample, PlateId = "P1", Well = "A1",
                VolumeLow = 50, VolumeHigh = null, OccurredAt = t.AddMinutes(2) },
            Tip("T1", t.AddMinutes(3)),
            Transfer("P1", "A1", "P2", "A1", 50, tip: "T1", at: t.AddMinutes(4))));
        Assert.True(ok.Accepted, string.Join(";", ok.Batch.Errors.Select(e => e.Message)));
        var src = svc.Projection.RequireWell(new WellRef("P1", "A1")).Volume;
        Assert.Equal(0, src.Low);
        Assert.Null(src.High); // remains unknown, never snapped to zero
    }

    [Fact]
    public async Task Disposable_Tip_Reuse_Is_An_Error()
    {
        var svc = await NewServiceAsync();
        var t = Base;
        var r = await svc.SubmitAsync(Batch(
            Plate("P1", at: t),
            Load("P1", "A1", 100, at: t.AddMinutes(1)),
            Load("P1", "A2", 100, at: t.AddMinutes(2)),
            Load("P1", "A3", 100, at: t.AddMinutes(3)),
            Tip("T1", t.AddMinutes(4)),
            Transfer("P1", "A1", "P1", "B1", 10, tip: "T1", at: t.AddMinutes(5)),
            Transfer("P1", "A2", "P1", "B2", 10, tip: "T1", at: t.AddMinutes(6))));
        Assert.False(r.Accepted);
        Assert.Contains(r.Batch.Errors, e => e.Code == Engine.ErrorCodes.TipReused);
    }

    [Fact]
    public async Task Tip_Reuse_After_Discard_Is_An_Error()
    {
        var svc = await NewServiceAsync();
        var t = Base;
        var first = await svc.SubmitAsync(Batch(
            Plate("P1", at: t),
            Load("P1", "A1", 100, at: t.AddMinutes(1)),
            Tip("T1", at: t.AddMinutes(2)),
            Transfer("P1", "A1", "P1", "B1", 10, tip: "T1", at: t.AddMinutes(3)),
            new EventDraft { Kind = LabEventType.TipDiscard, TipId = "T1", OccurredAt = t.AddMinutes(4) }));
        Assert.True(first.Accepted);

        var second = await svc.SubmitAsync(Batch(
            Load("P1", "A2", 100, at: t.AddMinutes(5)),
            Transfer("P1", "A2", "P1", "B2", 10, tip: "T1", at: t.AddMinutes(6))));
        Assert.False(second.Accepted);
        Assert.Contains(second.Batch.Errors, e => e.Code == Engine.ErrorCodes.TipDiscardedReused);
    }

    [Fact]
    public async Task Time_Descending_Events_Reject()
    {
        var svc = await NewServiceAsync();
        var t = Base;
        var r = await svc.SubmitAsync(Batch(
            Plate("P1", at: t.AddMinutes(10)),
            Load("P1", "A1", 10, at: t)));
        Assert.False(r.Accepted);
        Assert.Contains(r.Batch.Errors, e => e.Code == Engine.ErrorCodes.TimeDescending);
    }

    [Fact]
    public async Task Empty_Batch_Is_Rejected()
    {
        var svc = await NewServiceAsync();
        var r = await svc.SubmitAsync(new BatchSubmission());
        Assert.False(r.Accepted);
        Assert.Contains(r.Batch.Errors, e => e.Code == Engine.ErrorCodes.EmptyBatch);
    }

    [Fact]
    public async Task Frozen_Plate_Rejects_Mutation_But_Allows_Reading()
    {
        var svc = await NewServiceAsync();
        var t = Base;
        await svc.SubmitAsync(Batch(Plate("P1", at: t), Load("P1", "A1", 100, at: t.AddMinutes(1)), FreezePlate("P1", t.AddMinutes(2))));
        var bad = await svc.SubmitAsync(Batch(Load("P1", "A2", 5, at: t.AddMinutes(3))));
        Assert.False(bad.Accepted);
        Assert.Contains(bad.Batch.Errors, e => e.Code == Engine.ErrorCodes.FrozenPlateMutation);
        var read = await svc.SubmitAsync(Batch(Read("P1", "A1", 1, threshold: 10, at: t.AddMinutes(4))));
        Assert.True(read.Accepted, string.Join(";", read.Batch.Errors.Select(e => e.Message)));
    }

    [Fact]
    public async Task Well_Outside_Layout_Is_Rejected()
    {
        var svc = await NewServiceAsync();
        var r = await svc.SubmitAsync(Batch(Plate("P1", rows: 2, cols: 2),
            Load("P1", "C3", 10)));
        Assert.False(r.Accepted);
        Assert.Contains(r.Batch.Errors, e => e.Code == Engine.ErrorCodes.WellOutOfRange);
    }

    [Fact]
    public async Task Reading_Unknown_Unit_Is_Rejected()
    {
        var svc = await NewServiceAsync();
        var r = await svc.SubmitAsync(Batch(Plate("P1"),
            Read("P1", "A1", 1, unit: "bogus")));
        Assert.False(r.Accepted);
        Assert.Contains(r.Batch.Errors, e => e.Code == Engine.ErrorCodes.UnitUnknown);
    }
}
