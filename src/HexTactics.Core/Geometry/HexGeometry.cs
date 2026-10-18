using System;
using System.Collections.Generic;
using System.Numerics;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Geometry;

/// <summary>Part of a tile mesh (the renderer picks colour/material per part).</summary>
public enum TilePart
{
    Top,
    Bevel,
    Side,
    Rim,
    InnerWall,
    Bottom,
    LiquidSurface,
}

public readonly record struct TileTri(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal, TilePart Part, float ShadeA = 1f, float ShadeB = 1f, float ShadeC = 1f);

/// <summary>Parameters of one tile (world units; X east, Y up, Z south).</summary>
public sealed class TileShape
{
    public HexCoord Coord { get; init; }
    public float HexSize { get; init; } = 1f;
    /// <summary>Gap between neighbouring tiles (fraction of the radius) — reads as a grid.</summary>
    public float Gap { get; init; } = 0.05f;
    /// <summary>Height of the walkable surface (floor/platform top; pit rim).</summary>
    public float TopY { get; init; }
    /// <summary>Extra visual height above TopY (walls, barriers).</summary>
    public float ExtraHeight { get; init; }
    /// <summary>Bottom of the board (all prisms go down to it).</summary>
    public float BaseY { get; init; } = -0.45f;
    public float Bevel { get; init; } = 0.06f;
    /// <summary>Pit depth below TopY (0 = solid tile).</summary>
    public float Depth { get; init; }
    /// <summary>Liquid: surface this far below TopY; the basin floor is Depth below TopY.</summary>
    public float LiquidDrop { get; init; } = -1f;
}

/// <summary>
/// Volumetric hex tile geometry shared by the renderer and the tests (docs/DECISIONS_2.md §20):
/// solid prism (top, bevel, side faces down to the base), pit (rim, inner walls going down,
/// dark bottom) and liquid basin (rim, inner walls, surface below the rim).
/// </summary>
public static class HexGeometry
{
    public static Vector3 Center(HexCoord c, float y, float size)
    {
        var (x, z) = HexLayout.ToPlane(c, size);
        return new Vector3((float)x, y, (float)z);
    }

    public static Vector3 Corner(HexCoord c, int i, float y, float size, float scale)
    {
        double a = Math.PI / 180.0 * (60 * i - 30);
        var ctr = Center(c, y, size);
        return ctr + new Vector3((float)(Math.Cos(a) * size * scale), 0, (float)(Math.Sin(a) * size * scale));
    }

    private static Vector3 FaceNormal(Vector3 a, Vector3 b, Vector3 c, Vector3 outwardHint)
    {
        var n = Vector3.Cross(b - a, c - a);
        if (n.LengthSquared() < 1e-12f) return outwardHint;
        n = Vector3.Normalize(n);
        return Vector3.Dot(n, outwardHint) < 0 ? -n : n;
    }

    /// <summary>Adds a quad (two triangles) with a normal oriented along the hint.</summary>
    private static void Quad(List<TileTri> t, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 hint, TilePart part, float sa = 1, float sb = 1, float sc = 1, float sd = 1)
    {
        var n = FaceNormal(a, b, c, hint);
        t.Add(Wind(a, b, c, n, part, sa, sb, sc));
        t.Add(Wind(a, c, d, n, part, sa, sc, sd));
    }

    /// <summary>Triangle wound counter-clockwise when seen from the normal side.</summary>
    private static TileTri Wind(Vector3 a, Vector3 b, Vector3 c, Vector3 n, TilePart part, float sa = 1, float sb = 1, float sc = 1)
    {
        var cross = Vector3.Cross(b - a, c - a);
        return Vector3.Dot(cross, n) >= 0 ? new TileTri(a, b, c, n, part, sa, sb, sc) : new TileTri(a, c, b, n, part, sa, sc, sb);
    }

    /// <summary>Full mesh of one tile.</summary>
    public static List<TileTri> Build(TileShape s)
    {
        var tris = new List<TileTri>(64);
        if (s.Depth > 1e-4f) BuildRecess(s, tris);
        else BuildPrism(s, tris);
        return tris;
    }

    private static void BuildPrism(TileShape s, List<TileTri> tris)
    {
        var c = s.Coord;
        float outer = 1f - s.Gap;
        float bevel = Math.Clamp(s.Bevel, 0, 0.3f);
        float inner = outer - bevel * 0.9f;
        float top = s.TopY + s.ExtraHeight;
        var ctr = Center(c, top, s.HexSize);
        var inn = new Vector3[6];
        var outT = new Vector3[6];
        var outB = new Vector3[6];
        for (int i = 0; i < 6; i++)
        {
            inn[i] = Corner(c, i, top, s.HexSize, inner);
            outT[i] = Corner(c, i, top - bevel, s.HexSize, outer);
            outB[i] = Corner(c, i, s.BaseY, s.HexSize, outer);
        }
        for (int i = 0; i < 6; i++)
        {
            int j = (i + 1) % 6;
            tris.Add(Wind(ctr, inn[i], inn[j], Vector3.UnitY, TilePart.Top));
            if (bevel > 0)
            {
                var mid = (outT[i] + outT[j]) * 0.5f - ctr;
                var hint = Vector3.Normalize(new Vector3(mid.X, 0, mid.Z) + Vector3.UnitY);
                Quad(tris, inn[j], inn[i], outT[i], outT[j], hint, TilePart.Bevel);
            }
        }
        AddSides(s, tris, outT, outB, ctr);
    }

    private static void AddSides(TileShape s, List<TileTri> tris, Vector3[] top, Vector3[] bottom, Vector3 ctr)
    {
        for (int i = 0; i < 6; i++)
        {
            int j = (i + 1) % 6;
            var mid = (top[i] + top[j]) * 0.5f;
            var hint = Vector3.Normalize(new Vector3(mid.X - ctr.X, 0, mid.Z - ctr.Z));
            // Shade: 1 at the top edge, 0.55 at the base (vertical gradient).
            Quad(tris, top[j], top[i], bottom[i], bottom[j], hint, TilePart.Side, 1f, 1f, 0.55f, 0.55f);
        }
    }

    /// <summary>Pit or liquid basin: narrow rim at TopY, inner walls going down, bottom (or liquid surface).</summary>
    private static void BuildRecess(TileShape s, List<TileTri> tris)
    {
        var c = s.Coord;
        float outer = 1f - s.Gap;
        float rimW = Math.Max(0.06f, Math.Clamp(s.Bevel, 0, 0.3f) + 0.05f);
        float inner = outer - rimW;
        float top = s.TopY;
        float bottomY = top - s.Depth;
        var ctrTop = Center(c, top, s.HexSize);
        var outT = new Vector3[6];
        var outB = new Vector3[6];
        var rimIn = new Vector3[6];
        var wallB = new Vector3[6];
        for (int i = 0; i < 6; i++)
        {
            outT[i] = Corner(c, i, top, s.HexSize, outer);
            outB[i] = Corner(c, i, Math.Min(s.BaseY, bottomY - 0.05f), s.HexSize, outer);
            rimIn[i] = Corner(c, i, top - 0.02f, s.HexSize, inner);
            wallB[i] = Corner(c, i, bottomY, s.HexSize, inner * 0.94f);
        }
        for (int i = 0; i < 6; i++)
        {
            int j = (i + 1) % 6;
            // Rim: flat ring around the hole (glowing edge in the renderer).
            Quad(tris, outT[j], outT[i], rimIn[i], rimIn[j], Vector3.UnitY, TilePart.Rim);
            // Inner wall faces the centre of the hole; darker towards the bottom.
            var mid = (rimIn[i] + rimIn[j]) * 0.5f;
            var hint = Vector3.Normalize(new Vector3(ctrTop.X - mid.X, 0, ctrTop.Z - mid.Z));
            Quad(tris, rimIn[i], rimIn[j], wallB[j], wallB[i], hint, TilePart.InnerWall, 1f, 1f, 0.25f, 0.25f);
        }
        var ctrBottom = Center(c, bottomY, s.HexSize);
        for (int i = 0; i < 6; i++)
            tris.Add(Wind(ctrBottom, wallB[i], wallB[(i + 1) % 6], Vector3.UnitY, TilePart.Bottom, 0.2f, 0.2f, 0.2f));
        if (s.LiquidDrop >= 0)
        {
            float ly = top - s.LiquidDrop;
            var lc = Center(c, ly, s.HexSize);
            for (int i = 0; i < 6; i++)
            {
                var a = Corner(c, i, ly, s.HexSize, inner * 0.99f);
                var b = Corner(c, (i + 1) % 6, ly, s.HexSize, inner * 0.99f);
                tris.Add(Wind(lc, a, b, Vector3.UnitY, TilePart.LiquidSurface));
            }
        }
        AddSides(s, tris, outT, outB, ctrTop);
    }
}
