using System;
using System.Collections.Generic;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

public readonly struct AreaCell
{
    public AreaCell(HexCoord cell, MaskCellDef? mask, int order)
    {
        Cell = cell;
        Mask = mask;
        Order = order;
    }

    public HexCoord Cell { get; }
    /// <summary>The mask cell the area cell came from (extra per-cell effects), if any.</summary>
    public MaskCellDef? Mask { get; }
    /// <summary>Order along the area (line: distance from anchor; chain: jump index).</summary>
    public int Order { get; }
}

/// <summary>
/// Areas are defined in canonical orientation (facing east, direction 0) as offsets from the
/// anchor, then rotated by the orientation and translated to the anchor. Template shapes and
/// hand-drawn masks use exactly the same transform, so masks rotate correctly in battle.
/// </summary>
public static class AreaResolver
{
    /// <summary>Canonical offsets (orientation east) of a shape. Chain/Global are dynamic and return the anchor only.</summary>
    public static List<(HexCoord Offset, MaskCellDef? Mask, int Order)> CanonicalOffsets(AreaDef area)
    {
        var list = new List<(HexCoord, MaskCellDef?, int)>();
        var seen = new HashSet<HexCoord>();

        void Add(HexCoord c, MaskCellDef? m, int order)
        {
            if (seen.Add(c)) list.Add((c, m, order));
        }

        int size = Math.Max(0, area.Size);
        switch (area.Shape)
        {
            case AreaShape.Single:
            case AreaShape.Chain:
            case AreaShape.Global:
                Add(HexCoord.Zero, null, 0);
                break;
            case AreaShape.Line:
                if (area.IncludeCenter) Add(HexCoord.Zero, null, 0);
                for (int k = 1; k <= size; k++) Add(HexDirection.E.Offset() * k, null, k);
                break;
            case AreaShape.Cone:
                if (area.IncludeCenter) Add(HexCoord.Zero, null, 0);
                foreach (var c in HexCoord.Spiral(HexCoord.Zero, size))
                    if (c != HexCoord.Zero && InSector(c, 60))
                        Add(c, null, c.Length);
                break;
            case AreaShape.Sector:
                if (area.IncludeCenter) Add(HexCoord.Zero, null, 0);
                foreach (var c in HexCoord.Spiral(HexCoord.Zero, size))
                    if (c != HexCoord.Zero && InSector(c, area.Angle))
                        Add(c, null, c.Length);
                break;
            case AreaShape.Radius:
                foreach (var c in HexCoord.Spiral(HexCoord.Zero, size))
                    if (c != HexCoord.Zero || area.IncludeCenter)
                        Add(c, null, c.Length);
                break;
            case AreaShape.Ring:
            {
                int inner = Math.Clamp(area.InnerRadius, 0, size);
                for (int r = inner; r <= size; r++)
                foreach (var c in HexCoord.Ring(HexCoord.Zero, r))
                    Add(c, null, r);
                break;
            }
            case AreaShape.Cross:
                if (area.IncludeCenter) Add(HexCoord.Zero, null, 0);
                for (int k = 1; k <= size; k++)
                for (int d = 0; d < 6; d++)
                    Add(((HexDirection)d).Offset() * k, null, k);
                break;
            case AreaShape.Mask:
                foreach (var mc in area.Cells) Add(mc.Offset, mc, mc.Offset.Length);
                break;
        }
        return list;
    }

    /// <summary>
    /// True if offset c (relative to anchor) lies within the angular sector of the given total
    /// angle centred on east. Exact for 60/120/180/240/300/360 via cube dot product; boundaries inclusive.
    /// </summary>
    public static bool InSector(HexCoord c, double angleDeg)
    {
        if (angleDeg >= 360 - 1e-9) return true;
        if (c == HexCoord.Zero) return true;
        double half = angleDeg / 2.0 * Math.PI / 180.0;
        var e = HexDirection.E.Offset();
        double dot = HexCoord.CubeDot(c, e); // |e|^2 = 2 in cube space
        double len2 = c.Q * c.Q + c.R * c.R + c.S * c.S;
        double cos = dot / Math.Sqrt(len2 * 2.0);
        return cos >= Math.Cos(half) - 1e-9;
    }

    /// <summary>World cells of the area for an anchor and orientation (static shapes; no chain resolution).</summary>
    public static List<AreaCell> Resolve(AreaDef area, HexCoord anchor, HexDirection orientation, BattleMap? map = null)
    {
        var result = new List<AreaCell>();
        if (area.Shape == AreaShape.Global && map != null)
        {
            foreach (var c in map.Cells) result.Add(new AreaCell(c.Coord, null, HexCoord.Distance(c.Coord, anchor)));
            return result;
        }
        foreach (var (off, mask, order) in CanonicalOffsets(area))
        {
            var cell = off.Rotate((int)orientation) + anchor;
            if (map != null && !map.Contains(cell)) continue;
            result.Add(new AreaCell(cell, mask, order));
        }
        return result;
    }
}
