namespace PairwiseGsb.Core;

/// <summary>
/// Closed interval [Lo, Hi] used for quantities that may be partially unknown.
/// An unknown quantity is [0, +inf) and is never collapsed to zero.
/// </summary>
public readonly record struct Interval(double Lo, double Hi)
{
    public static readonly Interval Unknown = new(0.0, double.PositiveInfinity);
    public static readonly Interval Zero = new(0.0, 0.0);

    public static Interval Exact(double value) => new(value, value);

    public bool IsUnknown => Lo == 0.0 && double.IsPositiveInfinity(Hi);
    public bool IsExact => Lo == Hi;

    public static Interval FromNullable(double? value) =>
        value is null ? Unknown : Exact(value.Value);

    public static Interval operator +(Interval a, Interval b) => new(a.Lo + b.Lo, a.Hi + b.Hi);

    /// <summary>Interval subtraction clamped at zero (volumes cannot go negative).</summary>
    public static Interval operator -(Interval a, Interval b) =>
        new(Math.Max(0.0, a.Lo - b.Hi), Math.Max(0.0, a.Hi - b.Lo));

    public static Interval operator *(Interval a, Interval b) => new(a.Lo * b.Lo, a.Hi * b.Hi);

    public static Interval operator /(Interval a, Interval b)
    {
        var lo = b.Hi <= 0.0 ? 0.0 : a.Lo / b.Hi;
        var hi = b.Lo <= 0.0
            ? (a.Hi <= 0.0 ? 0.0 : double.PositiveInfinity)
            : a.Hi / b.Lo;
        return new Interval(lo, hi);
    }

    public Interval Intersect(Interval other) =>
        new(Math.Max(Lo, other.Lo), Math.Min(Hi, other.Hi));

    public bool DefinitelyExceeds(Interval other) => Lo > other.Hi;

    public override string ToString() =>
        $"[{Lo:0.###}, {(double.IsPositiveInfinity(Hi) ? "inf" : Hi.ToString("0.###"))}]";
}
