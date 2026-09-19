using PlateTrace.Core.Models;

namespace PlateTrace.Core.Engine;

/// <summary>
/// Enumerates simple, time-respecting paths in the transfer graph. Edges always
/// point forward in time (a later transfer cannot feed an earlier one), and a
/// well never repeats on one path, so the search terminates. Every path carries
/// the interval product of transfer fractions - the cumulative dilution range.
/// Unknown fractions stay unknown (null bounds), they are never zero.
/// </summary>
public sealed class PathTracer
{
    private const int MaxDepth = 14;
    private const int MaxPathsPerQuery = 5000;

    private readonly Projection _p;
    private readonly double _minFraction;
    private readonly Dictionary<string, List<TransferEdge>> _out;
    private readonly Dictionary<string, List<TransferEdge>> _in;

    public PathTracer(Projection p, double minFraction = 1e-6)
    {
        _p = p;
        _minFraction = minFraction;
        _out = p.TransferEdges
            .GroupBy(e => e.From.Id)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.OccurredAt).ThenBy(e => e.Seq).ToList());
        _in = p.TransferEdges
            .GroupBy(e => e.To.Id)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.OccurredAt).ThenBy(e => e.Seq).ToList());
    }

    public List<TracePath> PathsInto(WellRef target)
    {
        var result = new List<TracePath>();
        var incoming = _p.TransferEdges.Where(e => e.To.Id == target.Id).ToList();
        foreach (var last in incoming)
            DfsBack(last, new List<TransferEdge> { last },
                new HashSet<string> { target.Id, last.From.Id }, result);
        return result.Where(path => Eligible(path.Cumulative)).ToList();
    }

    public List<TracePath> AllPaths()
    {
        var result = new List<TracePath>();
        var targets = _p.TransferEdges.Select(e => e.To).Distinct().ToList();
        foreach (var t in targets)
        {
            result.AddRange(PathsInto(t));
            if (result.Count > MaxPathsPerQuery) break;
        }
        return result.Take(MaxPathsPerQuery).ToList();
    }

    /// <summary>
    /// All forward paths beginning with a given edge (used by carry-over hypotheses).
    /// </summary>
    public List<TracePath> PathsFromEdge(TransferEdge start)
    {
        var result = new List<TracePath>();
        Dfs(start, new List<TransferEdge> { start },
            new HashSet<string> { start.From.Id, start.To.Id }, result);
        return result.Where(p => Eligible(p.Cumulative)).ToList();
    }

    /// <summary>Forward paths starting from a well (contaminated reagent entry points).</summary>
    public List<TracePath> PathsFromWell(WellRef start)
    {
        var result = new List<TracePath>();
        if (!_out.TryGetValue(start.Id, out var edges)) return result;
        foreach (var edge in edges)
            Dfs(edge, new List<TransferEdge> { edge },
                new HashSet<string> { start.Id, edge.To.Id }, result);
        return result.Where(p => Eligible(p.Cumulative)).ToList();
    }

    private void Dfs(TransferEdge edge, List<TransferEdge> path, HashSet<string> visited, List<TracePath> result)
    {
        if (result.Count >= MaxPathsPerQuery) return;
        var cumulative = Product(path);
        if (!CanStillBeEligible(cumulative)) return;

        // the current prefix is itself a complete origin -> edge.To path
        result.Add(new TracePath { Edges = new List<TransferEdge>(path), Cumulative = cumulative });
        if (path.Count >= MaxDepth) return;

        if (!_out.TryGetValue(edge.To.Id, out var next)) return;
        foreach (var n in next)
        {
            if (n.OccurredAt < edge.OccurredAt) continue; // respect event order
            if (n.Seq <= edge.Seq) continue;
            if (!visited.Add(n.To.Id)) continue;
            path.Add(n);
            Dfs(n, path, visited, result);
            path.RemoveAt(path.Count - 1);
            visited.Remove(n.To.Id);
        }
    }

    /// <summary>
    /// Walk backwards from a destination, prepending strictly earlier incoming
    /// edges. The finished path is ordered source -&gt; destination.
    /// </summary>
    private void DfsBack(TransferEdge edge, List<TransferEdge> pathBack, HashSet<string> visited,
        List<TracePath> result)
    {
        if (result.Count >= MaxPathsPerQuery) return;
        var ordered = Enumerable.Reverse(pathBack).ToList();
        var cumulative = Product(ordered);
        if (!CanStillBeEligible(cumulative)) return;

        result.Add(new TracePath { Edges = ordered, Cumulative = cumulative });
        if (ordered.Count >= MaxDepth) return;

        if (!_in.TryGetValue(edge.From.Id, out var earlier)) return;
        foreach (var prev in earlier)
        {
            if (prev.OccurredAt > edge.OccurredAt) continue;
            if (prev.Seq >= edge.Seq) continue;
            if (!visited.Add(prev.From.Id)) continue;
            pathBack.Add(prev);
            DfsBack(prev, pathBack, visited, result);
            pathBack.RemoveAt(pathBack.Count - 1);
            visited.Remove(prev.From.Id);
        }
    }

    private static Interval Product(List<TransferEdge> path)
    {
        var low = 1.0;
        double? high = 1.0;
        foreach (var e in path)
        {
            var f = e.Fraction;
            if (f.Low is { } fl) low *= fl; else low = 0;
            high = f.High is { } fh ? high * fh : null;
            if (high is not null && high < 0) high = 0;
        }
        return new Interval(low, high);
    }

    private bool Eligible(Interval fraction)
    {
        // a path qualifies if its range can reach the threshold: either the upper
        // bound is unknown, or it is at/above the minimum transferable fraction.
        if (fraction.High is null) return true;
        return fraction.High.Value >= _minFraction;
    }

    private bool CanStillBeEligible(Interval fraction)
    {
        if (fraction.High is null) return true;
        return fraction.High.Value >= _minFraction * 0.5;
    }
}
