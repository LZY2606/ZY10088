using PlateTrace.Core.Models;

namespace PlateTrace.Core.Engine;

public sealed class SubmitResult
{
    public bool Accepted { get; init; }
    public BatchRecord Batch { get; init; } = null!;
    public bool IdempotentReplay { get; init; }
    public Projection? Projection { get; set; }
}

public sealed class PublishRequest
{
    public string? ConclusionId { get; set; }
    public string Title { get; set; } = "";
    public List<string> ReferencedPlates { get; set; } = new();
    public List<string> ReferencedReagentBatches { get; set; } = new();
    public List<string> ReferencedHypothesisIds { get; set; } = new();
}

public sealed class PublishResult
{
    public bool Published { get; init; }
    public List<EvidenceError> Errors { get; init; } = new();
    public ConclusionRecord? Conclusion { get; init; }
}
