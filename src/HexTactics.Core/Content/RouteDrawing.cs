using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

/// <summary>
/// Drawing a route by dragging over hexes (docs/DECISIONS_4.md §57): hexes are added one by one; a stretch skipped by a
/// fast mouse is filled with the shortest passable path (never a jump over hexes); moving back onto the previous hex
/// takes the last one away. Nothing is cut or redirected silently: with no passable way the straight line is drawn and
/// the check shows why it is wrong.
/// </summary>
public sealed class RouteDrawing
{
    private readonly ContentDatabase _db;
    private readonly Dictionary<HexCoord, MapCellDef> _cells = new();
    private readonly IReadOnlyList<string> _cats;
    private readonly HashSet<HexCoord> _blocked;

    public List<HexCoord> Cells { get; } = new();

    /// <summary>Hexes added by filling a gap (shown differently while drawing).</summary>
    public HashSet<HexCoord> Filled { get; } = new();

    public RouteDrawing(ContentDatabase db, MapDef map, IReadOnlyList<string>? categories = null, IEnumerable<HexCoord>? blocked = null)
    {
        _db = db;
        foreach (var c in map.Cells) _cells[c.C] = c;
        _cats = categories is { Count: > 0 } ? categories : new[] { "walk" };
        _blocked = blocked != null ? new HashSet<HexCoord>(blocked) : new HashSet<HexCoord>();
    }

    public bool Contains(HexCoord c) => _cells.ContainsKey(c);

    /// <summary>A step every move category of the route can make (structures other than the target block it).</summary>
    public bool CanStep(HexCoord a, HexCoord b)
    {
        if (_blocked.Contains(b) || !_cells.TryGetValue(a, out var from) || !_cells.TryGetValue(b, out var to)) return false;
        return _cats.All(cat => MapAnalysis.CanStep(_db, from, to, cat));
    }

    public bool IsWalkable(HexCoord c) => !_blocked.Contains(c) && _cells.TryGetValue(c, out var cell) && _cats.All(cat => MapAnalysis.IsWalkable(_db, cell, cat));

    /// <summary>Continues the line to a hex. Returns false when nothing changed.</summary>
    public bool To(HexCoord h)
    {
        if (Cells.Count == 0)
        {
            Cells.Add(h);
            return true;
        }
        var last = Cells[^1];
        if (h == last) return false;
        if (Cells.Count >= 2 && h == Cells[^2])
        {
            Filled.Remove(last);
            Cells.RemoveAt(Cells.Count - 1);
            return true;
        }
        if (HexCoord.Distance(last, h) == 1)
        {
            Cells.Add(h);
            return true;
        }
        var path = ShortestPath(last, h, HexCoord.Distance(last, h) * 3 + 6) ?? HexCoord.Line(last, h).Skip(1).ToList();
        foreach (var c in path.Take(path.Count - 1)) Filled.Add(c);
        Cells.AddRange(path);
        return true;
    }

    /// <summary>The shortest passable way from a to b (excluding a, including b) within a length; null — none.</summary>
    public List<HexCoord>? ShortestPath(HexCoord a, HexCoord b, int maxLength)
    {
        var prev = new Dictionary<HexCoord, HexCoord> { [a] = a };
        var queue = new Queue<(HexCoord C, int D)>();
        queue.Enqueue((a, 0));
        while (queue.Count > 0)
        {
            var (c, d) = queue.Dequeue();
            if (c == b) break;
            if (d >= maxLength) continue;
            foreach (var n in c.Neighbors())
            {
                if (prev.ContainsKey(n) || !CanStep(c, n)) continue;
                prev[n] = c;
                queue.Enqueue((n, d + 1));
            }
        }
        if (!prev.ContainsKey(b)) return null;
        var path = new List<HexCoord>();
        for (var c = b; c != a; c = prev[c]) path.Add(c);
        path.Reverse();
        return path;
    }

    /// <summary>
    /// «Продлить до цели»: the shortest passable way from the end of the line to a hex next to the target object
    /// (null — no way; the line is not changed then).
    /// </summary>
    public List<HexCoord>? WayTo(ScenarioObjectDef target, int maxLength = 200)
    {
        if (Cells.Count == 0) return null;
        var goal = target.Cells();
        if (goal.Any(g => HexCoord.Distance(g, Cells[^1]) <= 1)) return new List<HexCoord>();
        var prev = new Dictionary<HexCoord, HexCoord> { [Cells[^1]] = Cells[^1] };
        var queue = new Queue<(HexCoord C, int D)>();
        queue.Enqueue((Cells[^1], 0));
        while (queue.Count > 0)
        {
            var (c, d) = queue.Dequeue();
            if (goal.Any(g => HexCoord.Distance(g, c) <= 1))
            {
                var path = new List<HexCoord>();
                for (var x = c; x != Cells[^1]; x = prev[x]) path.Add(x);
                path.Reverse();
                return path;
            }
            if (d >= maxLength) continue;
            foreach (var n in c.Neighbors())
            {
                if (prev.ContainsKey(n) || !CanStep(c, n)) continue;
                prev[n] = c;
                queue.Enqueue((n, d + 1));
            }
        }
        return null;
    }
}
