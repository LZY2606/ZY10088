namespace PlateTrace.Core.Models;

/// <summary>
/// Explicit, closed unit registry. Readings from two sources may carry different
/// units; they are only compared after an explicit conversion through this table.
/// Same-dimensional ratios (e.g. pg/mL vs pg/uL) are converted by volume factors.
/// </summary>
public sealed record UnitDef(string Symbol, string Dimension, double ToBase);

public static class Units
{
    public const string DimensionVolume = "volume";
    public const string DimensionConcentration = "concentration";
    public const string DimensionFraction = "fraction";

    private static readonly Dictionary<string, UnitDef> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        // volumes, base unit = microliter (uL)
        ["uL"] = new("uL", DimensionVolume, 1),
        ["mL"] = new("mL", DimensionVolume, 1000),
        // concentrations, base unit = pg/mL
        ["pg/mL"] = new("pg/mL", DimensionConcentration, 1),
        ["ng/mL"] = new("ng/mL", DimensionConcentration, 1000),
        ["pg/uL"] = new("pg/uL", DimensionConcentration, 1000),
        ["ng/uL"] = new("ng/uL", DimensionConcentration, 1_000_000),
        // dimensionless fractions
        ["ratio"] = new("ratio", DimensionFraction, 1),
        ["%"] = new("%", DimensionFraction, 0.01),
    };

    public static bool IsKnown(string unit) => unit is not null && Table.ContainsKey(unit);
    public static UnitDef Get(string unit) => Table[unit];

    /// <summary>Convert <paramref name="value"/> expressed in <paramref name="from"/> into <paramref name="to"/>.</summary>
    public static double Convert(double value, string from, string to)
    {
        if (!IsKnown(from)) throw new ArgumentException($"unknown unit '{from}'", nameof(from));
        if (!IsKnown(to)) throw new ArgumentException($"unknown unit '{to}'", nameof(to));
        var a = Table[from];
        var b = Table[to];
        if (!string.Equals(a.Dimension, b.Dimension, StringComparison.Ordinal))
            throw new InvalidOperationException($"dimension mismatch: '{from}' ({a.Dimension}) vs '{to}' ({b.Dimension})");
        return value * a.ToBase / b.ToBase;
    }

    /// <summary>True only when an explicit same-dimension conversion exists.</summary>
    public static bool CanConvert(string from, string to) =>
        IsKnown(from) && IsKnown(to) &&
        string.Equals(Table[from].Dimension, Table[to].Dimension, StringComparison.Ordinal);

    public static IReadOnlyCollection<UnitDef> All => Table.Values;

    /// <summary>Canonical base unit (ToBase == 1) for a dimension.</summary>
    public static string BaseUnit(string dimension) =>
        Table.Values.First(u => u.Dimension == dimension && Math.Abs(u.ToBase - 1) < 1e-12).Symbol;

    public static string DimensionOf(string unit) => Table[unit].Dimension;
}
