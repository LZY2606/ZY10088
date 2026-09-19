namespace PlateTrace.Core.Models;

/// <summary>
/// A closed, non-negative interval [Low, High]. Unknown bounds stay unknown: a null
/// bound represents genuine uncertainty and is never silently treated as zero.
/// </summary>
public readonly record struct Interval
{
    public double? Low { get; }
    public double? High { get; }

    public Interval(double? low, double? high)
    {
        if (low is < 0 || high is < 0)
            throw new ArgumentOutOfRangeException(nameof(Interval), "interval bounds must be non-negative");
        if (low.HasValue && high.HasValue && low.Value > high.Value + 1e-12)
            throw new ArgumentException($"inverted interval [{low}, {high}]", nameof(Interval));
        Low = low;
        High = high;
    }

    public static Interval Exact(double value) => new(value, value);
    public static readonly Interval Unknown = new(null, null);
    public static readonly Interval Zero = new(0, 0);

    public bool IsExact => Low.HasValue && High.HasValue && Math.Abs(Low.Value - High.Value) < 1e-9;
    public bool IsUnknown => !Low.HasValue && !High.HasValue;

    public static Interval operator +(Interval a, Interval b) =>
        new(a.Low + b.Low, a.High + b.High);

    public static Interval operator -(Interval a, Interval b)
    {
        // subtraction only needs a guaranteed lower bound here; the caller is a
        // validated transfer, so the result is known non-negative.
        var low = a.Low - b.High;
        var high = a.High - b.Low;
        return new Interval(low < 0 && low > -1e-9 ? 0 : low, high);
    }

    public static Interval operator *(Interval a, Interval b) =>
        new(a.Low * b.Low, a.High * b.High);

    /// <summary>Positive scaling only.</summary>
    public Interval Scale(double factor)
    {
        if (factor < 0) throw new ArgumentOutOfRangeException(nameof(factor));
        return new Interval(Low * factor, High * factor);
    }

    /// <summary>
    /// Smallest possible aspirated fraction: smallest aspirate over the largest
    /// possible source volume. Unknown aspirate upper bound does not force it to
    /// zero - a null source bound yields an unknown ratio bound.
    /// </summary>
    public double? FractionLowOf(Interval source)
    {
        if (source.High is null || source.High <= 0) return null;
        if (Low is null) return 0; // aspirate could be arbitrarily small
        return Low.Value / source.High.Value;
    }

    /// <summary>Largest possible aspirated fraction, clamped to 1.</summary>
    public double? FractionHighOf(Interval source)
    {
        if (High is null) return 1; // aspirate could be the whole source
        if (source.Low is null || source.Low <= 0) return null;
        return Math.Min(1, High.Value / source.Low.Value);
    }

    public bool OverlapsPositive(double threshold)
    {
        // the interval could contain a value strictly above the threshold
        if (High is null) return true;
        return High.Value > threshold + 1e-12;
    }

    public bool DefinitelyAbove(double threshold)
    {
        return Low.HasValue && Low.Value > threshold + 1e-12;
    }

    public override string ToString() => IsUnknown ? "[?,?]" : IsExact ? $"{Low:G6}" : $"[{Low:G4},{High:G4}]";
}
