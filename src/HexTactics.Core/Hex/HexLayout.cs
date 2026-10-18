using System;

namespace HexTactics.Core.Hex;

/// <summary>
/// Conversion between hex coordinates and the 2D board plane (pointy-top).
/// Board plane: X = east, Y = south (maps to Godot world X / Z). Size = outer radius.
/// </summary>
public static class HexLayout
{
    public static readonly double Sqrt3 = Math.Sqrt(3.0);

    public static (double X, double Y) ToPlane(HexCoord h, double size = 1.0)
    {
        double x = size * Sqrt3 * (h.Q + h.R / 2.0);
        double y = size * 1.5 * h.R;
        return (x, y);
    }

    public static (double Q, double R) FractionalFromPlane(double x, double y, double size = 1.0)
    {
        double q = (Sqrt3 / 3.0 * x - 1.0 / 3.0 * y) / size;
        double r = (2.0 / 3.0 * y) / size;
        return (q, r);
    }

    public static HexCoord FromPlane(double x, double y, double size = 1.0)
    {
        var (q, r) = FractionalFromPlane(x, y, size);
        return HexCoord.Round(q, r);
    }

    /// <summary>Corner i (0..5) of a pointy-top hex; corner 0 at -30° (east-south-east)... in plane coords.</summary>
    public static (double X, double Y) Corner(HexCoord h, int i, double size = 1.0)
    {
        var (cx, cy) = ToPlane(h, size);
        double angle = Math.PI / 180.0 * (60 * i - 30);
        return (cx + size * Math.Cos(angle), cy + size * Math.Sin(angle));
    }

    /// <summary>Angle (degrees, 0 = east, CCW in top view with north up) from a to b.</summary>
    public static double AngleBetween(HexCoord a, HexCoord b)
    {
        var (ax, ay) = ToPlane(a);
        var (bx, by) = ToPlane(b);
        double deg = Math.Atan2(-(by - ay), bx - ax) * 180.0 / Math.PI;
        if (deg < 0) deg += 360.0;
        return deg;
    }

    /// <summary>Axial → "odd-r" offset coordinates (column, row): rows are horizontal, odd rows shifted right.</summary>
    public static (int Col, int Row) ToOffset(HexCoord h) => (h.Q + FloorDiv2(h.R), h.R);

    /// <summary>"Odd-r" offset (column, row) → axial.</summary>
    public static HexCoord FromOffset(int col, int row) => new(col - FloorDiv2(row), row);

    private static int FloorDiv2(int v) => v >= 0 ? v / 2 : -((-v + 1) / 2);

    public static HexDirection DirectionFromAngle(double deg)
    {
        int idx = (int)Math.Floor(deg / 60.0 + 0.5);
        return HexDirectionExt.FromIndex(idx);
    }
}
