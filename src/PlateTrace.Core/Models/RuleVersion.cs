namespace PlateTrace.Core.Models;

/// <summary>
/// Version of the rule set that produced a projection/conclusion. Conclusions
/// pin the version active when published, so later rule changes never silently
/// rewrite an old conclusion.
/// </summary>
public static class RuleVersion
{
    public const string Current = "plate-trace-rules/1.0.0";
}
