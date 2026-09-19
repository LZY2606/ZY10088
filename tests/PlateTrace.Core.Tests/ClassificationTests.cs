using PlateTrace.Core.Engine;
using PlateTrace.Core.Models;
using Xunit;
using static PlateTrace.Core.Tests.Scenario;

namespace PlateTrace.Core.Tests;

public class ClassificationTests
{
    private static BatchSubmission Batch(params EventDraft[] events) =>
        new() { IdempotencyKey = Guid.NewGuid().ToString("N"), Events = events.ToList() };

    [Fact]
    public async Task Multiple_Routes_Combine_To_Definite_When_Any_Route_Guarantees_Threshold()
    {
        var svc = new TraceService(new InMemoryStore());
        await svc.InitializeAsync();
        var t = Base;
        await svc.SubmitAsync(Batch(
            Plate("P1", at: t), Plate("P2", at: t.AddMinutes(1)),
            Reagent("DIL-A", at: t.AddMinutes(2)),
            AddDiluent("P2", "A1", "DIL-A", 180, at: t.AddMinutes(3)),
            Read("P2", "A1", 900, threshold: 500, at: t.AddMinutes(4))));

        var h = new HypothesisRecord
        {
            HypothesisId = "H", Version = 1, Kind = HypothesisKind.ContaminatedReagent,
            ReagentBatch = "DIL-A", Analyte = "X",
            ConcentrationLow = 100000, ConcentrationHigh = 100000, MinPathFraction = 1e-9,
        };
        var ev = new HypothesisEngine(svc.Projection).Evaluate(h, "J", DateTimeOffset.UtcNow);
        var explanation = Assert.Single(ev.ExplainedAnomalies);
        Assert.Equal("P2/A1", explanation.WellId);
        Assert.Equal(ExplanationStatus.Definite, explanation.Status);
        Assert.True(explanation.PredictedRange.Low >= 500);
    }
}
