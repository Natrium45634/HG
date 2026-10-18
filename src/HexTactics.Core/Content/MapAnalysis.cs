using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

/// <summary>
/// Static map checks for the editor: start points on bad cells, unreachable objectives and
/// enclosed (locked) walkable regions. Uses the same height/passability rules as movement
/// for the "walk" category (static terrain only, units ignored).
/// </summary>
public static class MapAnalysis
{
    public static bool IsWalkable(ContentDatabase db, MapCellDef c, string moveCategory = "walk")
    {
        if (c.Blocked == true) return false;
        var t = db.Terrain(c.T);
        if (t == null) return false;
        return t.IsPassableFor(moveCategory);
    }

    public static bool CanStep(ContentDatabase db, MapCellDef from, MapCellDef to, string moveCategory = "walk")
    {
        if (!IsWalkable(db, to, moveCategory)) return false;
        if (moveCategory == "fly") return true;
        int dh = to.H - from.H;
        var h = db.Rules.Height;
        return dh <= h.MaxClimb && -dh <= h.MaxDrop;
    }

    /// <summary>Cells reachable from start (directed by climb/drop limits).</summary>
    public static HashSet<HexCoord> Reachable(ContentDatabase db, Dictionary<HexCoord, MapCellDef> cells, HexCoord start, string moveCategory = "walk")
    {
        var seen = new HashSet<HexCoord>();
        if (!cells.TryGetValue(start, out var s) || !IsWalkable(db, s, moveCategory)) return seen;
        var queue = new Queue<HexCoord>();
        queue.Enqueue(start);
        seen.Add(start);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            var from = cells[c];
            foreach (var n in c.Neighbors())
            {
                if (seen.Contains(n) || !cells.TryGetValue(n, out var to)) continue;
                if (!CanStep(db, from, to, moveCategory)) continue;
                seen.Add(n);
                queue.Enqueue(n);
            }
        }
        return seen;
    }

    public static List<ContentIssue> Analyze(ContentDatabase db, MapDef m)
    {
        var issues = new List<ContentIssue>();
        string src = "Map:" + m.Id;
        var cells = new Dictionary<HexCoord, MapCellDef>();
        foreach (var c in m.Cells) cells.TryAdd(c.C, c);
        if (cells.Count == 0) return issues;

        var starts = m.Markers.Where(k => k.Type == MarkerType.PlayerStart).ToList();
        foreach (var st in starts)
        {
            if (cells.TryGetValue(st.C, out var cell) && !IsWalkable(db, cell))
                issues.Add(new ContentIssue(IssueSeverity.Error, src, "Markers", $"Стартовая точка {st.C} на непроходимой клетке"));
        }

        var fromStarts = new HashSet<HexCoord>();
        foreach (var st in starts) fromStarts.UnionWith(Reachable(db, cells, st.C));

        if (starts.Count > 0)
        {
            foreach (var mk in m.Markers.Where(k => k.Type is MarkerType.Objective or MarkerType.Exit or MarkerType.EnemySpawn))
            {
                if (!cells.TryGetValue(mk.C, out var mc)) continue;
                if (!IsWalkable(db, mc))
                {
                    if (mk.Type != MarkerType.EnemySpawn)
                        issues.Add(new ContentIssue(IssueSeverity.Error, src, "Markers", $"Цель «{Label(mk)}» на непроходимой клетке {mk.C}"));
                    continue;
                }
                if (!fromStarts.Contains(mk.C))
                {
                    var sev = mk.Type == MarkerType.EnemySpawn ? IssueSeverity.Warning : IssueSeverity.Error;
                    issues.Add(new ContentIssue(sev, src, "Markers", $"«{Label(mk)}» {mk.C} недостижима пешком от стартовых точек"));
                }
            }

            for (int i = 0; i < starts.Count; i++)
            for (int j = i + 1; j < starts.Count; j++)
                if (fromStarts.Contains(starts[i].C) && !Reachable(db, cells, starts[i].C).Contains(starts[j].C))
                    issues.Add(new ContentIssue(IssueSeverity.Warning, src, "Markers", $"Стартовые точки {starts[i].C} и {starts[j].C} не связаны"));
        }

        // Enclosed regions: walkable components not reachable from the largest one.
        var unvisited = new HashSet<HexCoord>(cells.Values.Where(c => IsWalkable(db, c)).Select(c => c.C));
        var components = new List<HashSet<HexCoord>>();
        while (unvisited.Count > 0)
        {
            var seed = unvisited.OrderBy(c => c).First();
            var comp = new HashSet<HexCoord>();
            var queue = new Queue<HexCoord>();
            queue.Enqueue(seed);
            comp.Add(seed);
            unvisited.Remove(seed);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                foreach (var n in c.Neighbors())
                {
                    if (!unvisited.Contains(n)) continue;
                    // Undirected connectivity: either direction of step allowed.
                    if (!CanStep(db, cells[c], cells[n]) && !CanStep(db, cells[n], cells[c])) continue;
                    unvisited.Remove(n);
                    comp.Add(n);
                    queue.Enqueue(n);
                }
            }
            components.Add(comp);
        }
        if (components.Count > 1)
        {
            var main = components.OrderByDescending(c => c.Count).First();
            foreach (var comp in components)
            {
                if (comp == main) continue;
                var first = comp.OrderBy(c => c).First();
                issues.Add(new ContentIssue(IssueSeverity.Warning, src, "Cells",
                    $"Запертая область из {comp.Count} клеток (например {first}) — недоступна пешком из основной части"));
            }
        }
        return issues;
    }

    private static string Label(MapMarkerDef mk) => string.IsNullOrEmpty(mk.Label) ? (string.IsNullOrEmpty(mk.Id) ? mk.Type.ToString() : mk.Id) : mk.Label;
}
