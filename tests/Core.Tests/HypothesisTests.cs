using PairwiseGsb.Core;
using Xunit;

namespace Core.Tests;

public class HypothesisTests
{
    private static DateTimeOffset T(int m) => new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.Zero).AddMinutes(m);

    private static List<LabEvent> Scenario()
    {
        // P1!A1 -> P2!A1 (tip1, event t1); P1!A2 -> P2!A2 (tip2, event t2, diluent DIL-7)
        return new List<LabEvent>
        {
            new PlateRegistered { EventId = "p1", Timestamp = T(0), PlateId = "P1", Rows = 8, Cols = 12 },
            new PlateRegistered { EventId = "p2", Timestamp = T(0), PlateId = "P2", Rows = 8, Cols = 12 },
            new ReagentBatchRegistered { EventId = "b1", Timestamp = T(0), BatchId = "DIL-7", Kind = "diluent", Version = "v1" },
            new VolumeDeclared { EventId = "v1", Timestamp = T(1), PlateId = "P1", Well = "A1", VolumeUl = 100 },
            new VolumeDeclared { EventId = "v2", Timestamp = T(1), PlateId = "P1", Well = "A2", VolumeUl = 100 },
            new TransferRecorded { EventId = "t1", Timestamp = T(2), TipId = "tip1",
                Source = new WellRef("P1", "A1"), Dest = new WellRef("P2", "A1"), VolumeUl = 10 },
            new TransferRecorded { EventId = "t2", Timestamp = T(3), TipId = "tip2",
                Source = new WellRef("P1", "A2"), Dest = new WellRef("P2", "A2"), VolumeUl = 10, DiluentBatchId = "DIL-7" },
            new ReadingRecorded { EventId = "r1", Timestamp = T(4), PlateId = "P2", Well = "A1", Value = 5000, Unit = "RFU", Source = "s" },
            new ReadingRecorded { EventId = "r2", Timestamp = T(4), PlateId = "P2", Well = "A2", Value = 10, Unit = "RFU", Source = "s" },
        };
    }

    [Fact]
    public void Carryover_hypothesis_explains_anomaly_and_predicts_downstream()
    {
        var state = LabState.Build(Scenario());
        var trace = Tracer.Trace(state, 1000, "RFU");
        var hyp = new CarryoverHypothesis { Id = "h1", EventId = "t1" };
        var analysis = HypothesisStore.Analyze(state, trace, new List<Hypothesis> { hyp });

        var explained = Assert.Single(analysis.Explanations);
        Assert.True(explained.Explained);
        Assert.Contains("h1", explained.ExplainedByHypothesisIds);
        // P1!A1 itself is a source but has no reading -> not an "extra predicted normal well"
        Assert.DoesNotContain(analysis.ExtraPredictedNormalWells, w => w.PlateId == "P1" && w.Well == "A1");
    }

    [Fact]
    public void Diluent_batch_hypothesis_flags_wells_that_received_the_batch()
    {
        var state = LabState.Build(Scenario());
        var trace = Tracer.Trace(state, 1000, "RFU");
        var hyp = new DiluentBatchHypothesis { Id = "h2", BatchId = "DIL-7" };
        var analysis = HypothesisStore.Analyze(state, trace, new List<Hypothesis> { hyp });

        // P2!A2 received DIL-7 and has a normal reading -> extra prediction
        Assert.Contains(analysis.ExtraPredictedNormalWells, w => w.PlateId == "P2" && w.Well == "A2");
        // The anomalous P2!A1 is not explained by this hypothesis
        Assert.False(Assert.Single(analysis.Explanations).Explained);
    }

    [Fact]
    public void Hypothesis_edits_bump_version_and_keep_history()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pgsb-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new HypothesisStore(dir);
            store.Upsert(new CarryoverHypothesis { Id = "h1", EventId = "t1" });
            store.Upsert(new CarryoverHypothesis { Id = "h1", EventId = "t2", Note = "修正目标事件" });
            var current = Assert.Single(store.Current());
            Assert.Equal(2, current.Version);
            Assert.Equal(2, store.History.Count);

            var reloaded = new HypothesisStore(dir);
            Assert.Equal(2, Assert.Single(reloaded.Current()).Version);
        }
        finally { Directory.Delete(dir, true); }
    }
}
