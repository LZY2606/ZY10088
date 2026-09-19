using PairwiseGsb.Core;
using Xunit;

namespace Core.Tests;

public class TracingTests
{
    private static DateTimeOffset T(int m) => new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.Zero).AddMinutes(m);

    private static List<LabEvent> ChainScenario()
    {
        // P1!A1 (200uL) -> 50uL -> P2!A1 ; P2!A1 -> 25uL -> P3!A1
        return new List<LabEvent>
        {
            new PlateRegistered { EventId = "p1", Timestamp = T(0), PlateId = "P1", Rows = 8, Cols = 12 },
            new PlateRegistered { EventId = "p2", Timestamp = T(0), PlateId = "P2", Rows = 8, Cols = 12 },
            new PlateRegistered { EventId = "p3", Timestamp = T(0), PlateId = "P3", Rows = 8, Cols = 12 },
            new VolumeDeclared { EventId = "v1", Timestamp = T(1), PlateId = "P1", Well = "A1", VolumeUl = 200 },
            new TransferRecorded { EventId = "t1", Timestamp = T(2), TipId = "tip1",
                Source = new WellRef("P1", "A1"), Dest = new WellRef("P2", "A1"), VolumeUl = 50 },
            new TransferRecorded { EventId = "t2", Timestamp = T(3), TipId = "tip2",
                Source = new WellRef("P2", "A1"), Dest = new WellRef("P3", "A1"), VolumeUl = 25 },
            new ReadingRecorded { EventId = "r1", Timestamp = T(4), PlateId = "P3", Well = "A1",
                Value = 5000, Unit = "RFU", Source = "reader-1" },
        };
    }

    [Fact]
    public void Anomaly_traces_all_upstream_paths_with_cumulative_dilution()
    {
        var state = LabState.Build(ChainScenario());
        var result = Tracer.Trace(state, 1000, "RFU");
        Assert.Single(result.Anomalies);
        var path = Assert.Single(result.Paths);
        Assert.Equal(new WellRef("P1", "A1"), path.Origin);
        Assert.Equal(new WellRef("P3", "A1"), path.Terminus);
        Assert.Equal(2, path.Steps.Count);
        // edge factors: 50/50 = 1.0, then 25/25 = 1.0 -> cumulative 1.0
        Assert.Equal(Interval.Exact(1.0), path.CumulativeConcentration);
        Assert.Equal(Interval.Exact(1.0), path.CumulativeDilution);
    }

    [Fact]
    public void Dilution_range_reflects_partial_transfer()
    {
        var events = ChainScenario();
        events.Insert(4, new VolumeDeclared { EventId = "v2", Timestamp = T(1), PlateId = "P2", Well = "A1", VolumeUl = 150 });
        var state = LabState.Build(events);
        var result = Tracer.Trace(state, 1000, "RFU");
        var path = Assert.Single(result.Paths);
        // P2!A1 = 150 + 50 = 200; edge1 factor = 50/200 = 0.25; edge2 = 25/25 = 1
        Assert.Equal(0.25, path.CumulativeConcentration.Lo, 6);
        Assert.Equal(4.0, path.CumulativeDilution.Lo, 6);
    }

    [Fact]
    public void Unknown_volumes_keep_interval_open_not_zero()
    {
        var events = ChainScenario();
        events.Insert(4, new VolumeDeclared { EventId = "vx", Timestamp = T(1), PlateId = "P2", Well = "A1", VolumeUl = null });
        var state = LabState.Build(events);
        var result = Tracer.Trace(state, 1000, "RFU");
        var path = Assert.Single(result.Paths);
        Assert.Equal(0.0, path.CumulativeConcentration.Lo);
        Assert.Equal(1.0, path.CumulativeConcentration.Hi);
        Assert.False(path.CumulativeConcentration.IsExact);
        // The dilution range stays open-ended; the unknown volume is not zeroed.
        Assert.Equal(double.PositiveInfinity, path.CumulativeDilution.Hi);
    }

    [Fact]
    public void Min_fraction_filters_paths()
    {
        var events = ChainScenario();
        events.Insert(4, new VolumeDeclared { EventId = "v2", Timestamp = T(1), PlateId = "P2", Well = "A1", VolumeUl = 150 });
        var state = LabState.Build(events);
        Assert.Single(Tracer.Trace(state, 1000, "RFU", 0.2).Paths);
        Assert.Empty(Tracer.Trace(state, 1000, "RFU", 0.5).Paths);
    }

    [Fact]
    public void Readings_in_different_units_require_explicit_conversion()
    {
        var events = ChainScenario();
        events.Add(new ReadingRecorded { EventId = "r2", Timestamp = T(4), PlateId = "P2", Well = "A1",
            Value = 9, Unit = "mRFU", Source = "reader-2" });
        var state = LabState.Build(events);
        var without = Tracer.Trace(state, 1000, "RFU");
        Assert.Contains(without.UnitProblems, p => p.Kind == EvidenceErrorKind.IncomparableUnits);
        Assert.Single(without.Anomalies); // only the RFU reading counts

        events.Add(new UnitConversionDeclared { EventId = "u1", Timestamp = T(5),
            FromUnit = "mRFU", ToUnit = "RFU", Factor = 0.001 });
        var state2 = LabState.Build(events);
        var withConv = Tracer.Trace(state2, 1000, "RFU");
        Assert.Empty(withConv.UnitProblems);
        Assert.Single(withConv.Anomalies); // 9 mRFU = 0.009 RFU, still normal
    }

    [Fact]
    public void Same_value_different_units_compared_only_after_conversion()
    {
        var events = new List<LabEvent>
        {
            new PlateRegistered { EventId = "p1", Timestamp = T(0), PlateId = "P1", Rows = 8, Cols = 12 },
            new ReadingRecorded { EventId = "r1", Timestamp = T(1), PlateId = "P1", Well = "A1", Value = 2, Unit = "RFU", Source = "s1" },
            new ReadingRecorded { EventId = "r2", Timestamp = T(1), PlateId = "P1", Well = "A2", Value = 2, Unit = "kRFU", Source = "s2" },
        };
        var state = LabState.Build(events);
        var trace = Tracer.Trace(state, 1.5, "RFU");
        Assert.Single(trace.Anomalies); // r2 incomparable -> excluded
        events.Add(new UnitConversionDeclared { EventId = "u1", Timestamp = T(2), FromUnit = "kRFU", ToUnit = "RFU", Factor = 1000 });
        var trace2 = Tracer.Trace(LabState.Build(events), 1.5, "RFU");
        Assert.Equal(2, trace2.Anomalies.Count);
    }
}
