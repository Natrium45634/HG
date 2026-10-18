using System;

namespace HexTactics.Core.Hex;

/// <summary>
/// Six directions of a pointy-top hex, index increases counter-clockwise
/// (top view, north up). See docs/DECISIONS.md §1.
/// </summary>
public enum HexDirection
{
    E = 0,
    NE = 1,
    NW = 2,
    W = 3,
    SW = 4,
    SE = 5,
}

public static class HexDirectionExt
{
    private static readonly HexCoord[] Offsets =
    {
        new(1, 0), new(1, -1), new(0, -1), new(-1, 0), new(-1, 1), new(0, 1),
    };

    private static readonly string[] RuNames =
    {
        "восток", "северо-восток", "северо-запад", "запад", "юго-запад", "юго-восток",
    };

    private static readonly string[] RuShort = { "В", "СВ", "СЗ", "З", "ЮЗ", "ЮВ" };

    public static HexCoord Offset(this HexDirection d) => Offsets[(int)d];

    public static HexDirection Rotate(this HexDirection d, int steps) => (HexDirection)((((int)d + steps) % 6 + 6) % 6);

    public static HexDirection Opposite(this HexDirection d) => d.Rotate(3);

    /// <summary>Smallest number of 60° steps between two directions (0..3).</summary>
    public static int StepsBetween(HexDirection a, HexDirection b)
    {
        int diff = Math.Abs((int)a - (int)b) % 6;
        return Math.Min(diff, 6 - diff);
    }

    /// <summary>Signed shortest rotation from a to b in steps (-2..3; +3 for opposite).</summary>
    public static int SignedSteps(HexDirection from, HexDirection to)
    {
        int diff = (((int)to - (int)from) % 6 + 6) % 6; // 0..5 CCW
        return diff <= 3 ? diff : diff - 6;
    }

    /// <summary>Angle in degrees, 0 = east, counter-clockwise (top view).</summary>
    public static double AngleDeg(this HexDirection d) => (int)d * 60.0;

    public static string RuName(this HexDirection d) => RuNames[(int)d];
    public static string RuShortName(this HexDirection d) => RuShort[(int)d];

    public static HexDirection FromIndex(int i) => (HexDirection)(((i % 6) + 6) % 6);
}
