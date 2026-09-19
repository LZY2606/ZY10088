using PlateTrace.Core.Engine;
using PlateTrace.Core.Jobs;
using PlateTrace.Core.Models;
using PlateTrace.Core.Store;
using Xunit;
using static PlateTrace.Core.Tests.Scenario;

namespace PlateTrace.Core.Tests;

public class ConclusionAndJobTests
{
    private static BatchSubmission Batch(params EventDraft[] events) =>
        new() { IdempotencyKey = Guid.NewGuid().ToString("N"), Events = events.ToList() };

    [Fact]
    public async Task Publishing_Requires_All_Plates_And_Reagent_Versions_Frozen()
    {
        var store = new InMemoryStore();
        var svc = new TraceService(store);
        await svc.InitializeAsync();
        var t = Base;
        await svc.SubmitAsync(Batch(
            Plate("P1", at: t), Reagent("DIL-A", at: t.AddMinutes(1)),
            Load("P1", "A1", 100, at: t.AddMinutes(2))));

        var blocked = await svc.PublishConclusionAsync(new PublishRequest
        {
            Title = "too early",
            ReferencedPlates = { "P1" },
            ReferencedReagentBatches = { "DIL-A" },
        });
        Assert.False(blocked.Published);
        Assert.Contains(blocked.Errors, e => e.Code == ErrorCodes.FrozenPlateMutation);
        Assert.Contains(blocked.Errors, e => e.Code == ErrorCodes.ReagentAlreadyFrozen);

        await svc.SubmitAsync(Batch(
            FreezeReagent("DIL-A", "DIL-A@r1", t.AddMinutes(3)),
            FreezePlate("P1", t.AddMinutes(4))));

        var published = await svc.PublishConclusionAsync(new PublishRequest
        {
            Title = "批次复盘 v1", ReferencedPlates = { "P1" }, ReferencedReagentBatches = { "DIL-A" },
        });
        Assert.True(published.Published);
        Assert.Equal(ConclusionState.Published, published.Conclusion!.State);
        Assert.NotNull(published.Conclusion.Snapshot);
        Assert.Equal(RuleVersion.Current, published.Conclusion.Snapshot!.RuleVersion);
    }

    [Fact]
    public async Task Upstream_Correction_Flips_Working_Conclusion_To_NeedsReview_But_Keeps_Published_Snapshot()
    {
        var store = new InMemoryStore();
        var svc = new TraceService(store);
        await svc.InitializeAsync();
        var t = Base;
        var setup = await svc.SubmitAsync(Batch(
            Plate("P1", at: t), Load("P1", "A1", 100, at: t.AddMinutes(1)),
            FreezePlate("P1", t.AddMinutes(2))));
        var published = await svc.PublishConclusionAsync(new PublishRequest
        {
            Title = "发布结论", ReferencedPlates = { "P1" },
        });
        Assert.True(published.Published);
        var publishId = published.Conclusion!.ConclusionId;

        // corrections can still correct past operations; a new working revision appears
        var loadId = setup.Batch.Events.Single(e => e.Kind == LabEventType.LoadSample).EventId;
        await svc.SubmitAsync(Batch(new EventDraft
        {
            Kind = LabEventType.Correction, SupersedesEventId = loadId,
            Reason = "体积登记错误", OccurredAt = t.AddMinutes(10),
            Replacement = new EventDraft { Kind = LabEventType.LoadSample, PlateId = "P1", Well = "A1", VolumeLow = 120, VolumeHigh = 120 },
        }));

        var all = await svc.ListConclusionsAsync();
        var publishedSnap = all.Single(c => c.State == ConclusionState.Published);
        var review = all.Where(c => c.ConclusionId == publishId && c.State == ConclusionState.NeedsReview).ToList();
        Assert.NotNull(publishedSnap.Snapshot); // old conclusion immutable
        Assert.Single(review);
        Assert.Contains(review[0].ReviewReasons, x => x.Contains("体积登记错误"));
        Assert.Null(review[0].Snapshot);
    }

    [Fact]
    public async Task Job_Recovery_Completes_Exactly_Once_After_Interruption()
    {
        var store = new InMemoryStore();
        var svc = new TraceService(store);
        await svc.InitializeAsync();
        var t = Base;
        await svc.SubmitAsync(Batch(Plate("P1", at: t),
            Load("P1", "A1", 200, at: t.AddMinutes(1))));

        var h = await svc.SaveHypothesisAsync(new HypothesisRequest
        {
            Kind = HypothesisKind.CarryOver, TargetEventId = "does-not-exist-yet",
            Analyte = "X", ConcentrationLow = 1, ConcentrationHigh = 1,
        });
        var runner = new JobRunner(store, () => svc);
        var job = await runner.EnqueueEvaluationAsync(h);
        Assert.Equal(JobState.Pending, job.State);

        // simulate crash mid-flight
        job.State = JobState.Running; job.Attempts = 1; job.StartedAt = DateTimeOffset.UtcNow;
        await store.UpsertJobAsync(job);
        await runner.RecoverAsync();
        var recovered = (await store.ListJobsAsync()).Single(j => j.JobId == job.JobId);
        Assert.Equal(JobState.Pending, recovered.State);
        Assert.Equal(2, recovered.Attempts);

        var processed = await runner.PumpAsync();
        Assert.Equal(1, processed);
        var done = (await store.ListJobsAsync()).Single(j => j.JobId == job.JobId);
        Assert.Equal(JobState.Completed, done.State);
        Assert.True(done.HasResult);

        // a second pump must not record the result again
        var processedAgain = await runner.PumpAsync();
        Assert.Equal(0, processedAgain);
        var stillOne = (await store.ListJobsAsync()).Count(j => j.JobId == job.JobId);
        Assert.Equal(1, stillOne);
    }

    [Fact]
    public async Task Hypothesis_Job_Is_Idempotent_Across_Retries()
    {
        var store = new InMemoryStore();
        var svc = new TraceService(store);
        await svc.InitializeAsync();
        var h = await svc.SaveHypothesisAsync(new HypothesisRequest
        {
            Kind = HypothesisKind.ContaminatedReagent, ReagentBatch = "DIL-A",
            Analyte = "X", ConcentrationLow = 1,
        });
        var runner = new JobRunner(store, () => svc);
        var j1 = await runner.EnqueueEvaluationAsync(h);
        var j2 = await runner.EnqueueEvaluationAsync(h);
        Assert.Equal(j1.JobId, j2.JobId);
        Assert.Single(await store.ListJobsAsync());
    }

    [Fact]
    public async Task FileStore_Rebuilds_Projection_And_Preserves_Rejected_Batches()
    {
        var dir = Path.Combine(Path.GetTempPath(), "platetrace-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileTraceStore(dir);
            var svc = new TraceService(store);
            await svc.InitializeAsync();
            var t = Base;
            await svc.SubmitAsync(Batch(Plate("P1", at: t)));
            var rejected = await svc.SubmitAsync(Batch(Load("P1", "A1", -1, at: t.AddMinutes(1))));
            Assert.False(rejected.Accepted);

            // reopen with a brand new service instance
            var store2 = new FileTraceStore(dir);
            var svc2 = new TraceService(store2);
            await svc2.InitializeAsync();
            Assert.Contains(svc2.Projection.Plates.Keys, k => k == "P1");
            var batches = await store2.ListBatchesAsync();
            Assert.Equal(2, batches.Count);
            Assert.Contains(batches, b => b.Outcome == BatchOutcome.Rejected);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Export_Bundle_Contains_Paths_Intervals_And_Hypothesis_Versions()
    {
        var store = new InMemoryStore();
        var svc = new TraceService(store);
        await svc.InitializeAsync();
        var t = Base;
        await svc.SubmitAsync(Batch(
            Plate("P1", at: t), Plate("P2", at: t.AddMinutes(1)),
            Load("P1", "A1", 200, at: t.AddMinutes(2)),
            Tip("T1", t.AddMinutes(3)),
            Transfer("P1", "A1", "P2", "A1", 20, tip: "T1", at: t.AddMinutes(4))));
        await svc.SaveHypothesisAsync(new HypothesisRequest
        {
            Kind = HypothesisKind.CarryOver, TargetEventId = "x", Analyte = "X",
            ConcentrationLow = 1, ConcentrationHigh = 2,
        });

        var bundle = await svc.BuildExportBundleAsync();
        Assert.Equal(StoreFormat.Version, bundle.FormatVersion);
        Assert.NotEmpty(bundle.Paths);
        Assert.All(bundle.Paths, p => Assert.True(p.Cumulative.High is > 0));
        Assert.Single(bundle.Hypotheses);
        Assert.All(bundle.Batches, b => Assert.Equal(RuleVersion.Current, b.RuleVersion));
    }
}
