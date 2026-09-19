using PlateTrace.Core.Models;
using Xunit;

namespace PlateTrace.Core.Tests;

public class IntervalAndUnitsTests
{
    [Fact]
    public void Unknown_Bounds_Stay_Null_And_Are_Not_Zero()
    {
        var u = Interval.Unknown;
        Assert.Null(u.Low);
        Assert.Null(u.High);
        Assert.True(u.IsUnknown);
    }

    [Fact]
    public void Inverted_Interval_Is_Rejected()
        => Assert.Throws<ArgumentException>(() => new Interval(10, 5));

    [Fact]
    public void Interval_Arithmetic_Preserves_Ranges()
    {
        var a = new Interval(90, 110);
        var b = new Interval(20, 20);
        var sum = a + b;
        Assert.Equal(110, sum.Low);
        Assert.Equal(130, sum.High);
        var diff = a - b;
        Assert.Equal(70, diff.Low);
        Assert.Equal(90, diff.High);
        var product = new Interval(0.1, 0.2) * new Interval(0.5, 0.5);
        Assert.Equal(0.05, product.Low!.Value, 10);
        Assert.Equal(0.1, product.High!.Value, 10);
    }

    [Fact]
    public void Fraction_Of_Unknown_Volume_Is_Unknown_Bound_Not_Zero()
    {
        var aspirate = new Interval(20, 20);
        var src = Interval.Unknown;
        Assert.Null(aspirate.FractionLowOf(src));
        Assert.Null(aspirate.FractionHighOf(src)); // unknown is not forced to 1 either
    }

    [Fact]
    public void Fraction_Bounds_Use_Opposite_Extremes()
    {
        // aspirate [10,20] from source [100,200]: low = 10/200, high = 20/100
        var aspirate = new Interval(10, 20);
        var src = new Interval(100, 200);
        Assert.Equal(0.05, aspirate.FractionLowOf(src)!.Value, 10);
        Assert.Equal(0.2, aspirate.FractionHighOf(src)!.Value, 10);
    }

    [Fact]
    public void Threshold_Overlap_Handles_Unknown_High()
    {
        Assert.True(new Interval(0, null).OverlapsPositive(500));
        Assert.False(new Interval(0, 10).OverlapsPositive(500));
        Assert.True(new Interval(600, null).DefinitelyAbove(500));
    }

    [Fact]
    public void Same_Value_Different_Units_Is_Equal_After_Explicit_Conversion()
    {
        var inPgL = Units.Convert(10, "ng/mL", "pg/mL");
        Assert.Equal(10000, inPgL, 6);
        var inPgPerU = Units.Convert(1, "pg/uL", "pg/mL");
        Assert.Equal(1000, inPgPerU, 6);
    }

    [Fact]
    public void Cross_Dimension_Conversion_Is_Rejected()
    {
        Assert.True(Units.CanConvert("ng/mL", "pg/mL"));
        Assert.False(Units.CanConvert("ng/mL", "uL"));
        Assert.Throws<InvalidOperationException>(() => Units.Convert(1, "uL", "pg/mL"));
    }

    [Fact]
    public void Unknown_Unit_Is_Detected() => Assert.False(Units.IsKnown("furlongs/firkin"));
}
