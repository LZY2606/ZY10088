using PairwiseGsb.Core;
using Xunit;

namespace Core.Tests;

public class EngineTests
{
    private static DateTimeOffset T(int m) => new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.Zero).AddMinutes(m);

    private static List<LabEvent> BaseSetup() => new()
    {
        new PlateRegistered { EventId = "p1", Timestamp = T(0), PlateId = "P1", Rows = 8, Cols = 12 },
        new PlateRegistered { EventId = "p2", Timestamp = T(0), PlateId = "P2", Rows = 8, Cols = 12 },
    };

    [Fact]
    public void Same_coordinate_on_different_plates_is_not_conflated()
    {
        var events = BaseSetup();
        events.Add(new VolumeDeclared { EventId = "v1", Timestamp = T(1), PlateId = "P1", Well = "A1", VolumeUl = 100 });
        events.Add(new VolumeDeclared { EventId = "v2", Timestamp = T(1), PlateId = "P2", Well = "A1", VolumeUl = 30 });
        var state = LabState.Build(events);
        Assert.Equal(100, state.Well(new WellRef("P1", "A1")).CurrentVolume.Lo);
        Assert.Equal(30, state.Well(new WellRef("P2", "A1")).CurrentVolume.Lo);
        Assert.Equal(2, state.Wells.Count);
    }

    [Fact]
    public void Volume_conservation_holds_across_transfer()
    {
        var events = BaseSetup();
        events.Add(new VolumeDeclared { EventId = "v1", Timestamp = T(1), PlateId = "P1", Well = "A1", VolumeUl = 200 });
        events.Add(new TransferRecorded { EventId = "t1", Timestamp = T(2), TipId = "tip1",
            Source = new WellRef("P1", "A1"), Dest = new WellRef("P2", "B2"), VolumeUl = 50 });
        var state = LabState.Build(events);
        Assert.Equal(150, state.Well(new WellRef("P1", "A1")).CurrentVolume.Lo);
        Assert.Equal(50, state.Well(new WellRef("P2", "B2")).CurrentVolume.Lo);
        Assert.Empty(state.Errors);
    }

    [Fact]
    public void Over_aspiration_is_an_evidence_error()
    {
        var events = BaseSetup();
        events.Add(new VolumeDeclared { EventId = "v1", Timestamp = T(1), PlateId = "P1", Well = "A1", VolumeUl = 40 });
        events.Add(new TransferRecorded { EventId = "t1", Timestamp = T(2), TipId = "tip1",
            Source = new WellRef("P1", "A1"), Dest = new WellRef("P2", "B2"), VolumeUl = 50 });
        var state = LabState.Build(events);
        Assert.Contains(state.Errors, e => e.Kind == EvidenceErrorKind.OverAspiration && e.EventIds.Contains("t1"));
    }

    [Fact]
    public void Unknown_source_volume_never_raises_over_aspiration()
    {
        var events = BaseSetup();
        events.Add(new VolumeDeclared { EventId = "v1", Timestamp = T(1), PlateId = "P1", Well = "A1", VolumeUl = null });
        events.Add(new TransferRecorded { EventId = "t1", Timestamp = T(2), TipId = "tip1",
            Source = new WellRef("P1", "A1"), Dest = new WellRef("P2", "B2"), VolumeUl = 5000 });
        var state = LabState.Build(events);
        Assert.DoesNotContain(state.Errors, e => e.Kind == EvidenceErrorKind.OverAspiration);
        Assert.True(state.Well(new WellRef("P2", "B2")).CurrentVolume.Hi > 0);
    }

    [Fact]
    public void Time_reversal_is_an_evidence_error()
    {
        var events = BaseSetup();
        events.Add(new MixRecorded { EventId = "m1", Timestamp = T(5), PlateId = "P1", Well = "A1" });
        events.Add(new MixRecorded { EventId = "m2", Timestamp = T(2), PlateId = "P1", Well = "A1" });
        var state = LabState.Build(events);
        Assert.Contains(state.Errors, e => e.Kind == EvidenceErrorKind.TimeReversal && e.EventIds.Contains("m2"));
    }

    [Fact]
    public void Disposable_tip_reuse_is_an_evidence_error()
    {
        var events = BaseSetup();
        events.Add(new VolumeDeclared { EventId = "v1", Timestamp = T(1), PlateId = "P1", Well = "A1", VolumeUl = 500 });
        events.Add(new TransferRecorded { EventId = "t1", Timestamp = T(2), TipId = "tipX",
            Source = new WellRef("P1", "A1"), Dest = new WellRef("P2", "B1"), VolumeUl = 10 });
        events.Add(new TransferRecorded { EventId = "t2", Timestamp = T(3), TipId = "tipX",
            Source = new WellRef("P1", "A1"), Dest = new WellRef("P2", "B2"), VolumeUl = 10 });
        var state = LabState.Build(events);
        Assert.Contains(state.Errors, e => e.Kind == EvidenceErrorKind.TipReuse && e.EventIds.Contains("t2"));
    }
}
