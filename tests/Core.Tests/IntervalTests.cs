using PairwiseGsb.Core;
using Xunit;

namespace Core.Tests;

public class IntervalTests
{
    [Fact]
    public void Unknown_is_not_zero()
    {
        var u = Interval.FromNullable(null);
        Assert.True(u.IsUnknown);
        Assert.NotEqual(Interval.Zero, u);
        Assert.Equal(double.PositiveInfinity, u.Hi);
    }

    [Fact]
    public void Arithmetic_keeps_bounds()
    {
        var a = new Interval(2, 4);
        var b = new Interval(1, 2);
        Assert.Equal(new Interval(3, 6), a + b);
        Assert.Equal(new Interval(0, 3), a - b); // clamped at zero
        Assert.Equal(new Interval(2, 8), a * b);
        Assert.Equal(new Interval(1, 4), a / b);
    }

    [Fact]
    public void Division_by_interval_containing_zero_yields_open_upper_bound()
    {
        var a = Interval.Exact(10);
        var b = new Interval(0, 5);
        var q = a / b;
        Assert.Equal(2.0, q.Lo);
        Assert.Equal(double.PositiveInfinity, q.Hi);
    }

    [Fact]
    public void Unknown_dilution_stays_open_ended()
    {
        var factor = Interval.Exact(50) / Interval.Unknown;
        Assert.Equal(0.0, factor.Lo);
        Assert.Equal(double.PositiveInfinity, factor.Hi);
    }
}
