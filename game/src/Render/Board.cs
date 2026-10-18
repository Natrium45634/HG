using System;
using System.Collections.Generic;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;

namespace HexTactics.Game.Render;

/// <summary>World-space conventions of the board (see docs/DECISIONS.md §1): X east, −Z north, Y up.</summary>
public static class Board
{
    public const float HexSize = 1.0f;
    public const float LevelHeight = 0.35f;
    public const float Gap = 0.05f;
    public const float WallHeight = 1.05f;
    public const float WaterDepth = 0.22f;

    public static Vector3 Center(HexCoord c, float y = 0)
    {
        var (x, z) = HexLayout.ToPlane(c, HexSize);
        return new Vector3((float)x, y, (float)z);
    }

    public static bool IsLiquid(TerrainDef t) =>
        t.Kind != TerrainKind.Pit && (t.Look.Material == TerrainMaterial.Liquid || t.Visual is TerrainVisual.Water or TerrainVisual.Lava or TerrainVisual.Acid);

    public static bool IsPit(TerrainDef t) => t.Kind == TerrainKind.Pit || t.Visual is TerrainVisual.Pit or TerrainVisual.Chasm;

    /// <summary>Visual height of walls/barriers above the walkable level (world units).</summary>
    public static float ExtraHeight(TerrainDef t) =>
        t.Look.ExtraHeight > 0 ? (float)t.Look.ExtraHeight * LevelHeight : t.Visual == TerrainVisual.Wall ? WallHeight : 0f;

    /// <summary>Depth of a pit (world units).</summary>
    public static float PitDepth(TerrainDef t) => (float)Math.Max(t.Look.Depth > 0 ? t.Look.Depth : 2.5, 0.5) * LevelHeight;

    /// <summary>Basin depth of a liquid (world units).</summary>
    public static float LiquidDepth(TerrainDef t) => (float)(0.4 + Math.Max(0, t.Look.Depth)) * LevelHeight;

    public const float LiquidDrop = 0.05f;

    /// <summary>Top surface height of a cell where units stand (liquids: wading below the surface).</summary>
    public static float TopY(Cell? cell)
    {
        if (cell == null) return 0;
        float y = cell.Height * LevelHeight;
        if (IsLiquid(cell.Terrain)) return y - Math.Min(WaterDepth, LiquidDepth(cell.Terrain) * 0.8f);
        return y;
    }

    /// <summary>Height of the visible top (walls are taller blocks; liquids show their surface; pits their rim).</summary>
    public static float SurfaceY(Cell cell)
    {
        float y = cell.Height * LevelHeight;
        var t = cell.Terrain;
        if (IsPit(t)) return y;
        if (IsLiquid(t)) return y - LiquidDrop;
        return y + ExtraHeight(t);
    }

    public static Vector3 CellTop(Cell cell) => Center(cell.Coord, TopY(cell));

    public static Vector3 Corner(HexCoord c, int i, float y, float scale = 1f)
    {
        double angle = Math.PI / 180.0 * (60 * i - 30);
        var center = Center(c, y);
        return center + new Vector3((float)(Math.Cos(angle) * HexSize * scale), 0, (float)(Math.Sin(angle) * HexSize * scale));
    }

    /// <summary>World direction vector of a hex direction (unit length, on the XZ plane).</summary>
    public static Vector3 DirVector(HexDirection d)
    {
        var a = Center(HexCoord.Zero);
        var b = Center(d.Offset());
        return (b - a).Normalized();
    }

    /// <summary>Yaw (radians around +Y) that makes −Z point along a hex direction.</summary>
    public static float YawOf(HexDirection d)
    {
        var v = DirVector(d);
        return Mathf.Atan2(-v.X, -v.Z);
    }

    /// <summary>
    /// Picks the cell under a ray, honouring cell heights: tests the planes of every distinct
    /// surface height from the highest down and returns the first cell whose top is at that plane.
    /// </summary>
    public static Cell? Pick(BattleMap map, Vector3 origin, Vector3 dir, List<float> levels)
    {
        if (Mathf.Abs(dir.Y) < 1e-5f) return null;
        foreach (var y in levels)
        {
            float t = (y - origin.Y) / dir.Y;
            if (t < 0) continue;
            var p = origin + dir * t;
            var hex = HexLayout.FromPlane(p.X, p.Z, HexSize);
            var cell = map.Get(hex);
            if (cell != null && Mathf.Abs(PickY(cell) - y) < 0.001f) return cell;
        }
        // Fallback: ground plane (lets the editor hover empty slots too).
        return null;
    }

    public static float PickY(Cell cell) => ExtraHeight(cell.Terrain) > 0 ? SurfaceY(cell) : Math.Max(TopY(cell), SurfaceY(cell));

    public static List<float> PickLevels(BattleMap map)
    {
        var set = new HashSet<float>();
        foreach (var c in map.Cells) set.Add(PickY(c));
        var list = new List<float>(set);
        list.Sort((a, b) => b.CompareTo(a));
        return list;
    }

    /// <summary>Hex under a ray on the plane y (for empty slots in the map editor).</summary>
    public static HexCoord? PickPlane(Vector3 origin, Vector3 dir, float y = 0)
    {
        if (Mathf.Abs(dir.Y) < 1e-5f) return null;
        float t = (y - origin.Y) / dir.Y;
        if (t < 0) return null;
        var p = origin + dir * t;
        return HexLayout.FromPlane(p.X, p.Z, HexSize);
    }
}

/// <summary>Material cache (one material per colour/kind keeps draw calls and allocations low).</summary>
public static class Mats
{
    private static readonly Dictionary<(Color, int), StandardMaterial3D> Cache = new();

    public static StandardMaterial3D Solid(Color c, float roughness = 0.8f, float metallic = 0f)
    {
        var key = (c, (int)(roughness * 100) + (int)(metallic * 10000));
        if (Cache.TryGetValue(key, out var m)) return m;
        m = new StandardMaterial3D { AlbedoColor = c, Roughness = roughness, Metallic = metallic };
        Cache[key] = m;
        return m;
    }

    public static StandardMaterial3D Emissive(Color c, float energy = 1.5f)
    {
        var key = (c, -1000 - (int)(energy * 10));
        if (Cache.TryGetValue(key, out var m)) return m;
        m = new StandardMaterial3D { AlbedoColor = c, EmissionEnabled = true, Emission = c, EmissionEnergyMultiplier = energy, Roughness = 0.5f };
        Cache[key] = m;
        return m;
    }

    public static StandardMaterial3D Transparent(Color c, bool unshaded = false, bool noDepth = false)
    {
        var key = (c, -2000 - (unshaded ? 1 : 0) - (noDepth ? 2 : 0));
        if (Cache.TryGetValue(key, out var m)) return m;
        m = new StandardMaterial3D
        {
            AlbedoColor = c,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = unshaded ? BaseMaterial3D.ShadingModeEnum.Unshaded : BaseMaterial3D.ShadingModeEnum.PerPixel,
            NoDepthTest = noDepth,
            Roughness = 0.3f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        Cache[key] = m;
        return m;
    }

    public static StandardMaterial3D VertexColor(bool unshaded = false, bool transparent = false, float roughness = 0.85f, bool noDepth = false)
    {
        var key = (new Color(0, 0, 0, 0), -3000 - (unshaded ? 1 : 0) - (transparent ? 2 : 0) - (noDepth ? 4 : 0) - (int)(roughness * 100) * 8);
        if (Cache.TryGetValue(key, out var m)) return m;
        m = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            VertexColorIsSrgb = true,
            Roughness = roughness,
            ShadingMode = unshaded ? BaseMaterial3D.ShadingModeEnum.Unshaded : BaseMaterial3D.ShadingModeEnum.PerPixel,
            Transparency = transparent ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled,
            CullMode = transparent ? BaseMaterial3D.CullModeEnum.Disabled : BaseMaterial3D.CullModeEnum.Back,
            NoDepthTest = noDepth,
        };
        if (unshaded) m.RenderPriority = 1;
        Cache[key] = m;
        return m;
    }
}
