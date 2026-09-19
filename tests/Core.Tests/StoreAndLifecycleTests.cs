using PairwiseGsb.Core;
using Xunit;

namespace Core.Tests;

public class StoreAndLifecycleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pgsb-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static DateTimeOffset T(int m) => new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.Zero).AddMinutes(m);

    private static List<LabEvent> BaseEvents() => new()
    {
        new PlateRegistered { EventId = "p1", Timestamp = T(0), PlateId = "P1", Rows = 8, Cols = 12 },
        new VolumeDeclared { EventId = "v1", Timestamp = T(1), PlateId = "P1", Well = "A1", VolumeUl = 100 },
        new ReadingRecorded { EventId = "r1", Timestamp = T(2), PlateId = "P1", Well = "A1", Value = 5000, Unit = "RFU", Source = "s" },
    };

    [Fact]
    public void Batch_commit_is_atomic_on_validation_failure()
    {
        var store = new EventStore(_dir);
        var good = BaseEvents();
        var bad = new List<LabEvent>
        {
            new PlateRegistered { EventId = "p2", Timestamp = T(0), PlateId = "P2", Rows = 8, Cols = 12 },
            new TransferRecorded { EventId = "t1", Timestamp = T(1), TipId = "tip",
                Source = new WellRef("P2", "A1"), Dest = new WellRef("P9", "A1"), VolumeUl = 5 }, // unknown plate P9
        };
        var rejected = store.SubmitBatch("k-bad", bad);
        Assert.False(rejected.Applied);
        Assert.Empty(store.RawLog()); // nothing applied

        var ok = store.SubmitBatch("k-good", good);
        Assert.True(ok.Applied);
        Assert.Equal(3, store.RawLog().Count);
    }

    [Fact]
    public void Retry_with_same_idempotency_key_does_not_reapply()
    {
        var store = new EventStore(_dir);
        var first = store.SubmitBatch("k1", BaseEvents());
        var retry = store.SubmitBatch("k1", BaseEvents());
        Assert.True(first.Applied);
        Assert.True(retry.WasReplay);
        Assert.Equal(3, store.RawLog().Count);

        // Survives reload (durable idempotency)
        var reloaded = new EventStore(_dir);
        var again = reloaded.SubmitBatch("k1", BaseEvents());
        Assert.True(again.WasReplay);
        Assert.Equal(3, reloaded.RawLog().Count);
    }

    [Fact]
    public void Correction_supersedes_without_rewriting_the_log()
    {
        var svc = new LabService(_dir);
        svc.SubmitBatch("k1", BaseEvents());
        var correction = new CorrectionRecorded
        {
            EventId = "c1", Timestamp = T(3), SupersedesEventId = "r1", Reason = "读数单位录错",
            Replacement = new ReadingRecorded { EventId = "r1b", Timestamp = T(2), PlateId = "P1", Well = "A1", Value = 50, Unit = "RFU", Source = "s" },
        };
        var result = svc.SubmitBatch("k2", new List<LabEvent> { correction });
        Assert.True(result.Applied);

        Assert.Equal(4, svc.Store.RawLog().Count); // original kept
        var effective = svc.Store.EffectiveEvents();
        Assert.DoesNotContain(effective, e => e.EventId == "r1");
        Assert.Contains(effective, e => e.EventId == "r1b");
        Assert.Contains(effective, e => e is CorrectionRecorded);
        var state = svc.CurrentState();
        Assert.Equal(50, state.Well(new WellRef("P1", "A1")).Readings.Single().Value);
    }

    [Fact]
    public void Correction_requires_reason_and_existing_target()
    {
        var store = new EventStore(_dir);
        store.SubmitBatch("k1", BaseEvents());
        var noReason = store.SubmitBatch("k2", new List<LabEvent>
        {
            new CorrectionRecorded { EventId = "c1", Timestamp = T(3), SupersedesEventId = "r1", Reason = "",
                Replacement = new MixRecorded { EventId = "m1", Timestamp = T(2), PlateId = "P1", Well = "A1" } },
        });
        Assert.False(noReason.Applied);
        var ghost = store.SubmitBatch("k3", new List<LabEvent>
        {
            new CorrectionRecorded { EventId = "c2", Timestamp = T(3), SupersedesEventId = "nope", Reason = "x",
                Replacement = new MixRecorded { EventId = "m2", Timestamp = T(2), PlateId = "P1", Well = "A1" } },
        });
        Assert.False(ghost.Applied);
    }

    [Fact]
    public void Publish_requires_all_references_frozen()
    {
        var svc = new LabService(_dir);
        svc.SubmitBatch("k1", BaseEvents());
        Assert.Throws<InvalidOperationException>(() => svc.Publish(1000, "RFU"));

        svc.SubmitBatch("k2", new List<LabEvent>
        {
            new EntityFrozen { EventId = "f1", Timestamp = T(3), EntityKind = "plate", EntityId = "P1" },
        });
        var published = svc.Publish(1000, "RFU");
        Assert.Equal(ConclusionStatus.Published, svc.Conclusions.Status);
        Assert.Equal(RuleSet.RuleVersion, published.RuleVersion);
    }

    [Fact]
    public void Upstream_correction_marks_needs_review_but_keeps_old_conclusion()
    {
        var svc = new LabService(_dir);
        svc.SubmitBatch("k1", BaseEvents());
        svc.SubmitBatch("k2", new List<LabEvent>
        {
            new EntityFrozen { EventId = "f1", Timestamp = T(3), EntityKind = "plate", EntityId = "P1" },
        });
        var published = svc.Publish(1000, "RFU");

        svc.SubmitBatch("k3", new List<LabEvent>
        {
            new CorrectionRecorded { EventId = "c1", Timestamp = T(4), SupersedesEventId = "v1", Reason = "初始体积录错",
                Replacement = new VolumeDeclared { EventId = "v1b", Timestamp = T(1), PlateId = "P1", Well = "A1", VolumeUl = 90 } },
        });
        Assert.Equal(ConclusionStatus.NeedsReview, svc.Conclusions.Status);
        Assert.Same(published, svc.Conclusions.Published); // old conclusion untouched
    }

    [Fact]
    public void Background_jobs_recover_without_duplicate_results()
    {
        var svc = new LabService(_dir);
        svc.SubmitBatch("k1", BaseEvents());
        Assert.Single(svc.Jobs.Jobs);
        Assert.Equal(1, svc.RecoverJobs());
        Assert.Equal(JobStatus.Done, svc.Jobs.Jobs[0].Status);
        var result = svc.Jobs.Jobs[0].ResultJson;

        // Simulate crash-recovery: reload from disk, nothing should re-run.
        var svc2 = new LabService(_dir);
        Assert.Equal(0, svc2.RecoverJobs());
        Assert.Equal(result, svc2.Jobs.Jobs[0].ResultJson);

        // Enqueue with same id is idempotent.
        svc2.Jobs.Enqueue("recompute-k1", "recompute");
        Assert.Single(svc2.Jobs.Jobs);
    }

    [Fact]
    public void Export_preserves_paths_intervals_and_hypothesis_versions()
    {
        var svc = new LabService(_dir);
        svc.SubmitBatch("k1", BaseEvents());
        svc.UpsertHypothesis(new CarryoverHypothesis { Id = "h1", EventId = "r1" });
        var doc = svc.Export(1000, "RFU");
        Assert.Equal(RuleSet.RuleVersion, doc.RuleVersion);
        Assert.Single(doc.Anomalies);
        Assert.Equal("h1", Assert.Single(doc.HypothesisVersions).Id);
        Assert.Equal(new[] { "p1", "v1", "r1" }, doc.EventOrder);

        var path = svc.WriteExport(1000, "RFU");
        Assert.True(File.Exists(path));
    }
}
