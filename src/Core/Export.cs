using System.Text.Json;

namespace PairwiseGsb.Core;

/// <summary>Export keeps trace paths, interval arithmetic and hypothesis
/// versions so a conclusion can be replayed and audited offline.</summary>
public sealed record ExportDocument(
    DateTimeOffset ExportedAt,
    string RuleVersion,
    ConclusionStatus ConclusionStatus,
    PublishedConclusion? Published,
    List<AnomalousWell> Anomalies,
    List<TracePath> Paths,
    List<Hypothesis> HypothesisVersions,
    List<string> EventOrder,
    List<EvidenceError> EvidenceErrors,
    List<(string FromUnit, string ToUnit, double Factor)> UnitConversions);

public static class Exporter
{
    public static ExportDocument Build(
        LabState state, TraceResult trace, IReadOnlyList<Hypothesis> hypotheses,
        ConclusionStore conclusions) =>
        new(
            DateTimeOffset.UtcNow,
            state.RuleVersion,
            conclusions.Status,
            conclusions.Published,
            trace.Anomalies,
            trace.Paths,
            hypotheses.ToList(),
            state.EventOrder.Select(e => e.EventId).ToList(),
            state.Errors.Concat(trace.UnitProblems).ToList(),
            state.Conversions);

    public static string Write(ExportDocument doc, string dataDir)
    {
        var dir = Path.Combine(dataDir, "exports");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"export-{doc.ExportedAt:yyyyMMddHHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(doc,
            new JsonSerializerOptions(RuleSet.Json) { WriteIndented = true }));
        return path;
    }
}
