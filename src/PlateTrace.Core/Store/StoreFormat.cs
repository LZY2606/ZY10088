namespace PlateTrace.Core.Store;

/// <summary>
/// On-disk format policy. The store is a set of plain UTF-8 files under a data
/// directory - no external database or cloud account is required.
///
/// Compatibility policy:
///   * <c>major</c> changes (1.x -&gt; 2.x) may restructure files and require migration;
///     an older reader refuses a newer major version.
///   * <c>minor</c> changes only add optional fields/files and remain readable by
///     code written for the same major version.
/// </summary>
public static class StoreFormat
{
    public const string FormatName = "plate-trace-store";
    public const int Major = 1;
    public const int Minor = 0;
    public const string Version = FormatName + "/1.0";

    public const string FormatFile = "format.json";
    public const string BatchesFile = "batches.jsonl";
    public const string HypothesesFile = "hypotheses.jsonl";
    public const string ConclusionsFile = "conclusions.jsonl";
    public const string JobsFile = "jobs.json";
    public const string LockFile = ".lock";
}
