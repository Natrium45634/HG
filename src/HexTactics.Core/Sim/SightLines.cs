using System;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>
/// Sight lines of creatures with several directions of view (docs/DECISIONS_3.md §40): a cell is seen
/// when it lies in a non-blind sector (angle within half the width, distance within the line's range)
/// and in no blind sector. Pure geometry — line of sight is checked by the caller.
/// </summary>
public static class SightLines
{
    private static (double X, double Z) World(int q, int r) => (Math.Sqrt(3) * (q + r / 2.0), 1.5 * r);

    private static double Angle(int dq, int dr)
    {
        var (x, z) = World(dq, dr);
        return Math.Atan2(z, x);
    }

    /// <summary>Angle in degrees between a direction and the vector from a cell to another (0…180).</summary>
    public static double AngleTo(HexDirection dir, HexCoord from, HexCoord to)
    {
        var o = dir.Offset();
        double a = Angle(o.Q, o.R), b = Angle(to.Q - from.Q, to.R - from.R);
        double d = Math.Abs(a - b) * 180 / Math.PI;
        while (d > 180) d = Math.Abs(d - 360);
        return d;
    }

    public static bool InSector(SightLineDef l, HexDirection dir, HexCoord from, HexCoord to, double range)
    {
        int dist = HexCoord.Distance(from, to);
        if (dist == 0) return true;
        double r = l.Range > 0 ? Math.Min(l.Range, range) : range;
        if (dist > r + 1e-9) return false;
        if (l.Width >= 359.9) return true;
        return AngleTo(dir, from, to) <= l.Width / 2 + 1e-6;
    }

    /// <summary>True if the cell is covered by the lines; throughObstacles — a covering line sees through obstacles.</summary>
    public static bool Sees(PerceptionDef p, HexCoord pos, HexDirection facing, HexCoord cell, double range, out bool throughObstacles)
    {
        throughObstacles = false;
        int primary = p.PrimaryLine()?.Offset ?? 0;
        foreach (var l in p.Lines)
            if (l.Blind && InSector(l, l.DirectionFor(facing, primary), pos, cell, double.MaxValue))
                return false;
        bool seen = false;
        foreach (var l in p.Lines)
        {
            if (l.Blind || !InSector(l, l.DirectionFor(facing, primary), pos, cell, range)) continue;
            seen = true;
            if (l.ThroughObstacles) throughObstacles = true;
        }
        return seen;
    }
}
