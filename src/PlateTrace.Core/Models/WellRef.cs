namespace PlateTrace.Core.Models;

/// <summary>
/// Globally unique well identity. The same coordinate (e.g. "A1") on different
/// plates is a distinct <see cref="WellRef"/>; coordinates must never be compared
/// across plates without also comparing <see cref="PlateId"/>.
/// </summary>
public sealed record WellRef(string PlateId, string Well)
{
    public string Id => $"{PlateId}/{Well}";

    public static bool SameCoordinate(WellRef a, WellRef b) =>
        string.Equals(a.Well, b.Well, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Id;
}
