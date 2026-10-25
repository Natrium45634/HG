using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

/// <summary>A piece of a map: cells (and optionally markers) relative to an anchor at (0, 0).</summary>
public sealed class MapClip
{
    public List<MapCellDef> Cells { get; } = new();
    public string Source { get; init; } = "";
    public int Count => Cells.Count;
}

/// <summary>What changing the size of a map removes (ТЗ ч. 5, 5.6): shown before the change.</summary>
public sealed class ResizeImpact
{
    public List<HexCoord> Removed { get; } = new();
    public List<HexCoord> Added { get; } = new();
    /// <summary>Markers, route points and objects of scenarios on removed cells.</summary>
    public List<string> Losses { get; } = new();
}

/// <summary>
/// Operations of the map editor kept out of the interface (docs/DECISIONS_5.md §85): a rectangle by the projection,
/// copy, rotation by 60° and mirror, paste/stamp, mixed values of a selection, eraser, resizing with its losses.
/// </summary>
public static class MapOps
{
    /// <summary>Cells inside the rectangle of the board plane between the centres of two cells.</summary>
    public static List<HexCoord> Rect(MapDef map, HexCoord a, HexCoord b)
    {
        var (ax, ay) = HexLayout.ToPlane(a);
        var (bx, by) = HexLayout.ToPlane(b);
        double x0 = Math.Min(ax, bx) - 1e-6, x1 = Math.Max(ax, bx) + 1e-6, y0 = Math.Min(ay, by) - 1e-6, y1 = Math.Max(ay, by) + 1e-6;
        var list = new List<HexCoord>();
        foreach (var c in map.Cells)
        {
            var (x, y) = HexLayout.ToPlane(c.C);
            if (x >= x0 && x <= x1 && y >= y0 && y <= y1) list.Add(c.C);
        }
        return list.OrderBy(c => c.R).ThenBy(c => c.Q).ToList();
    }

    /// <summary>The anchor of a set of cells: the top row, then the leftmost (in the plane).</summary>
    public static HexCoord Anchor(IEnumerable<HexCoord> cells) =>
        cells.OrderBy(c => c.R).ThenBy(c => HexLayout.ToPlane(c).X).First();

    /// <summary>Copies cells of the map relative to their anchor.</summary>
    public static MapClip Copy(MapDef map, IEnumerable<HexCoord> cells, string source = "")
    {
        var set = cells.ToHashSet();
        var clip = new MapClip { Source = source };
        if (set.Count == 0) return clip;
        var at = Anchor(set);
        foreach (var c in map.Cells.Where(c => set.Contains(c.C)))
        {
            var copy = c.Clone();
            copy.C = c.C - at;
            clip.Cells.Add(copy);
        }
        return clip;
    }

    /// <summary>A map template as a stamp: its cells relative to their anchor.</summary>
    public static MapClip FromTemplate(MapTemplateDef t)
    {
        var map = new MapDef { Cells = t.Cells };
        return Copy(map, t.Cells.Select(c => c.C), t.DisplayName);
    }

    /// <summary>Rotated by 60° steps (counter-clockwise) and/or mirrored left-right, around the anchor.</summary>
    public static MapClip Transform(MapClip clip, int rotate, bool mirror)
    {
        var res = new MapClip { Source = clip.Source };
        foreach (var c in clip.Cells)
        {
            var p = c.C;
            if (mirror) p = new HexCoord(-p.Q - p.R, p.R);
            p = p.Rotate(rotate);
            var copy = c.Clone();
            copy.C = p;
            res.Cells.Add(copy);
        }
        // Keep the anchor at the top-left again so the stamp is placed by the same corner.
        if (res.Cells.Count > 0)
        {
            var at = Anchor(res.Cells.Select(c => c.C));
            foreach (var c in res.Cells) c.C -= at;
        }
        return res;
    }

    /// <summary>
    /// Pastes the clip with its anchor at <paramref name="at"/>: cells of the map are replaced; cells outside the map are
    /// added when <paramref name="addCells"/>, otherwise skipped. Returns the changed and the skipped cells.
    /// </summary>
    public static (List<HexCoord> Changed, int Skipped) Paste(MapDef map, MapClip clip, HexCoord at, bool addCells)
    {
        var byC = map.Cells.ToDictionary(c => c.C);
        var changed = new List<HexCoord>();
        int skipped = 0;
        foreach (var c in clip.Cells)
        {
            var p = c.C + at;
            var copy = c.Clone();
            copy.C = p;
            if (byC.TryGetValue(p, out var old))
            {
                map.Cells[map.Cells.IndexOf(old)] = copy;
                changed.Add(p);
            }
            else if (addCells)
            {
                map.Cells.Add(copy);
                changed.Add(p);
            }
            else skipped++;
        }
        return (changed, skipped);
    }

    /// <summary>The cell back to its terrain: overrides, start effects, tags, name and description removed.</summary>
    public static bool Erase(MapCellDef c)
    {
        bool had = c.MoveCost != null || c.Blocked != null || c.BlocksLoS != null || c.Cover != null || c.AllowPlacement != null || c.ObstacleHp != null ||
                   c.AllowedEffects != null || c.Effects != null || c.Tags != null || c.Name != null || c.Description != null;
        c.MoveCost = null;
        c.Blocked = null;
        c.BlocksLoS = null;
        c.Cover = null;
        c.AllowPlacement = null;
        c.ObstacleHp = null;
        c.AllowedEffects = null;
        c.Effects = null;
        c.Tags = null;
        c.Name = null;
        c.Description = null;
        return had;
    }

    /// <summary>
    /// Properties whose values differ between the selected cells (ТЗ ч. 5, 5.8): name → «3 значения: Пол, Стена, Лава».
    /// The coordinates are not compared.
    /// </summary>
    public static Dictionary<string, string> Mixed(IReadOnlyCollection<MapCellDef> cells, ContentDatabase? db = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (cells.Count < 2) return result;
        foreach (var p in typeof(MapCellDef).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.Name is nameof(MapCellDef.C) or nameof(ContentPart.Extra) || p.GetCustomAttribute<EdAttribute>() == null) continue;
            var values = cells.Select(c => Show(p.GetValue(c), p, db)).Distinct().ToList();
            if (values.Count > 1)
                result[p.Name] = $"{values.Count} {(values.Count < 5 ? "значения" : "значений")}: {string.Join(", ", values.Take(4))}{(values.Count > 4 ? "…" : "")}";
        }
        return result;
    }

    private static string Show(object? v, PropertyInfo p, ContentDatabase? db) => v switch
    {
        null => "—",
        string s when p.Name == nameof(MapCellDef.T) => db?.Terrain(s)?.DisplayName ?? s,
        string s => s,
        bool b => b ? "да" : "нет",
        double d => d.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU")),
        IEnumerable<string> l => "[" + string.Join(", ", l) + "]",
        _ => v.ToString() ?? "—",
    };

    /// <summary>A rectangular map resized to <paramref name="cols"/> × <paramref name="rows"/> (odd-r offset), as a preview.</summary>
    public static ResizeImpact ResizePreview(ContentDatabase? db, MapDef map, int cols, int rows)
    {
        var impact = new ResizeImpact();
        var keep = new HashSet<HexCoord>();
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < cols; col++)
                keep.Add(HexLayout.FromOffset(col, row));
        var have = map.Cells.Select(c => c.C).ToHashSet();
        impact.Removed.AddRange(have.Where(c => !keep.Contains(c)).OrderBy(c => c.R).ThenBy(c => c.Q));
        impact.Added.AddRange(keep.Where(c => !have.Contains(c)).OrderBy(c => c.R).ThenBy(c => c.Q));
        impact.Losses.AddRange(Losses(db, map, impact.Removed));
        return impact;
    }

    /// <summary>Applies the resize: removes the cells outside, adds the missing ones with the given terrain.</summary>
    public static ResizeImpact Resize(ContentDatabase? db, MapDef map, int cols, int rows, string terrain)
    {
        var impact = ResizePreview(db, map, cols, rows);
        var removed = impact.Removed.ToHashSet();
        map.Cells.RemoveAll(c => removed.Contains(c.C));
        foreach (var c in impact.Added) map.Cells.Add(new MapCellDef { C = c, T = terrain });
        map.Markers.RemoveAll(m => removed.Contains(m.C));
        foreach (var r in map.Routes) r.Points.RemoveAll(removed.Contains);
        return impact;
    }

    /// <summary>What is lost with the cells: markers, points of map routes, objects and route hexes of scenarios on this map.</summary>
    public static List<string> Losses(ContentDatabase? db, MapDef map, IEnumerable<HexCoord> cells)
    {
        var set = cells.ToHashSet();
        var list = new List<string>();
        foreach (var m in map.Markers.Where(m => set.Contains(m.C)))
            list.Add($"метка «{(string.IsNullOrEmpty(m.Label) ? m.Id : m.Label)}» {m.C}");
        foreach (var r in map.Routes)
            foreach (var p in r.Points.Where(set.Contains))
                list.Add($"точка маршрута «{r.Name}» {p}");
        if (db != null)
            foreach (var sc in db.All<ScenarioDef>().Where(s => s.Map == map.Id))
            {
                foreach (var o in sc.Objects.Where(o => set.Contains(o.Anchor) || o.Cells().Any(set.Contains)))
                    list.Add($"сценарий «{sc.DisplayName}»: объект «{o.DisplayName}» {o.Anchor}");
                foreach (var r in sc.Routes.Where(r => r.Edges.Any(e => e.Cells.Any(set.Contains)) || r.Nodes.Any(n => set.Contains(n.C))))
                    list.Add($"сценарий «{sc.DisplayName}»: маршрут «{r.DisplayName}» проходит по удаляемым клеткам");
                foreach (var u in sc.Units.Where(u => set.Contains(u.C)))
                    list.Add($"сценарий «{sc.DisplayName}»: юнит «{u.Unit}» {u.C}");
            }
        return list;
    }
}
