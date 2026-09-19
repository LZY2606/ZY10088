using PlateTrace.Core.Models;

namespace PlateTrace.Core.Engine;

public sealed class HypothesisRequest
{
    public string? HypothesisId { get; set; }
    public HypothesisKind Kind { get; set; }
    public string Description { get; set; } = "";
    public string? TargetEventId { get; set; }
    public string? SourcePlate { get; set; }
    public string? SourceWell { get; set; }
    public string? InjectionPlate { get; set; }
    public string? InjectionWell { get; set; }
    public string? ReagentBatch { get; set; }
    public string? TipId { get; set; }
    public string Analyte { get; set; } = "default";
    public double? ConcentrationLow { get; set; }
    public double? ConcentrationHigh { get; set; }
    public double? CarryFractionLow { get; set; }
    public double? CarryFractionHigh { get; set; }
    public double MinPathFraction { get; set; } = 1e-6;
}
