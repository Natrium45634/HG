using System;
using System.Collections.Generic;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using HexTactics.Game.UI;

namespace HexTactics.Game.Render;

/// <summary>
/// Builds the board geometry: chamfered hexagonal prisms with height (one mesh for all cells,
/// matte + glossy surfaces), a transparent water sheet and low-poly decorations (rocks, trees,
/// bushes, crates, rubble, swamp bubbles, altar) via MultiMesh. Minimalist but volumetric.
/// </summary>
public partial class TerrainNode : Node3D
{
    private MeshInstance3D? _terrain;
    private MeshInstance3D? _water;
    private readonly List<Node3D> _decor = new();

    public float BaseY { get; private set; } = -0.6f;

    private static float Hash01(HexCoord c, int salt)
    {
        unchecked
        {
            uint h = (uint)(c.Q * 73856093) ^ (uint)(c.R * 19349663) ^ (uint)(salt * 83492791);
            h ^= h >> 13;
            h *= 0x5bd1e995;
            h ^= h >> 15;
            return (h & 0xFFFFFF) / (float)0xFFFFFF;
        }
    }

    public void Build(BattleMap map, bool showGrid = true)
    {
        foreach (var c in GetChildren())
        {
            RemoveChild(c);
            c.QueueFree();
        }
        _decor.Clear();

        int minH = 0;
        foreach (var c in map.Cells) minH = Math.Min(minH, c.Height);
        BaseY = minH * Board.LevelHeight - 0.45f;

        var matte = new SurfaceTool();
        matte.Begin(Mesh.PrimitiveType.Triangles);
        var glossy = new SurfaceTool();
        glossy.Begin(Mesh.PrimitiveType.Triangles);
        var water = new SurfaceTool();
        water.Begin(Mesh.PrimitiveType.Triangles);
        bool anyGlossy = false, anyWater = false;

        var decor = new DecorBatches();

        foreach (var cell in map.Cells)
        {
            var t = cell.Terrain;
            var baseColor = Ui.Parse(t.Color, new Color(0.5f, 0.6f, 0.4f));
            float v = 0.94f + 0.1f * Hash01(cell.Coord, 1);
            // Higher cells are slightly lighter: reads height without contours.
            float hl = 1f + Math.Clamp(cell.Height, -3, 4) * 0.035f;
            var top = new Color(baseColor.R * v * hl, baseColor.G * v * hl, baseColor.B * v * hl);
            float y = Board.SurfaceY(cell);
            var st = matte;
            float bevel = 0.06f;
            switch (t.Visual)
            {
                case TerrainVisual.Water:
                    y = Board.TopY(cell);
                    top = top.Darkened(0.45f);
                    st = glossy;
                    anyGlossy = true;
                    AddWaterSheet(water, cell, Ui.Parse(t.Color, new Color(0.3f, 0.5f, 0.8f)));
                    anyWater = true;
                    break;
                case TerrainVisual.Ice:
                    st = glossy;
                    anyGlossy = true;
                    break;
                case TerrainVisual.Pit:
                    top = new Color(0.07f, 0.065f, 0.06f);
                    bevel = 0.0f;
                    break;
                case TerrainVisual.Wall:
                    bevel = 0.09f;
                    break;
                case TerrainVisual.Special:
                    st = glossy;
                    anyGlossy = true;
                    break;
            }
            AddPrism(st, map, cell, y, top, bevel);
            AddDecor(decor, cell, Board.TopY(cell));
        }

        var mesh = new ArrayMesh();
        matte.Index();
        matte.Commit(mesh);
        mesh.SurfaceSetMaterial(0, Mats.VertexColor(roughness: 0.9f));
        if (anyGlossy)
        {
            glossy.Index();
            glossy.Commit(mesh);
            mesh.SurfaceSetMaterial(1, Mats.VertexColor(roughness: 0.25f));
        }
        _terrain = new MeshInstance3D { Mesh = mesh, Name = "Terrain" };
        AddChild(_terrain);

        if (anyWater)
        {
            var wm = water.Commit();
            var mat = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                VertexColorIsSrgb = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                Roughness = 0.08f,
                Metallic = 0.1f,
            };
            wm.SurfaceSetMaterial(0, mat);
            _water = new MeshInstance3D { Mesh = wm, Name = "Water", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(_water);
        }

        foreach (var n in decor.Build()) AddChild(n);
    }

    private static void Tri(SurfaceTool st, Vector3 n, Color c, Vector3 a, Vector3 b, Vector3 d)
    {
        st.SetNormal(n);
        st.SetColor(c);
        st.AddVertex(a);
        st.SetNormal(n);
        st.SetColor(c);
        st.AddVertex(b);
        st.SetNormal(n);
        st.SetColor(c);
        st.AddVertex(d);
    }

    private void AddPrism(SurfaceTool st, BattleMap map, Cell cell, float y, Color top, float bevel)
    {
        var c = cell.Coord;
        float outer = 1f - Board.Gap;
        float inner = outer - bevel * 0.9f;
        var center = Board.Center(c, y);
        var up = Vector3.Up;
        var inn = new Vector3[6];
        var outT = new Vector3[6];
        var outB = new Vector3[6];
        for (int i = 0; i < 6; i++)
        {
            inn[i] = Board.Corner(c, i, y, inner);
            outT[i] = Board.Corner(c, i, y - bevel, outer);
            outB[i] = Board.Corner(c, i, BaseY, outer);
        }
        for (int i = 0; i < 6; i++)
        {
            int j = (i + 1) % 6;
            Tri(st, up, top, center, inn[i], inn[j]);
            if (bevel > 0)
            {
                var bn = (inn[j] - inn[i]).Cross(outT[i] - inn[i]).Normalized();
                if (bn.Y < 0) bn = -bn;
                var bc = top.Lightened(0.10f);
                Tri(st, bn, bc, inn[j], inn[i], outT[i]);
                Tri(st, bn, bc, inn[j], outT[i], outT[j]);
            }
        }
        // Sides are always drawn: the gaps between tiles read as a subtle grid.
        var side = top.Darkened(0.32f);
        var sideLow = top.Darkened(0.55f);
        for (int i = 0; i < 6; i++)
        {
            int j = (i + 1) % 6;
            var mid = (outT[i] + outT[j]) * 0.5f;
            var n = new Vector3(mid.X - center.X, 0, mid.Z - center.Z).Normalized();
            // Vertical gradient: darker towards the bottom.
            st.SetNormal(n);
            st.SetColor(side);
            st.AddVertex(outT[j]);
            st.SetNormal(n);
            st.SetColor(side);
            st.AddVertex(outT[i]);
            st.SetNormal(n);
            st.SetColor(sideLow);
            st.AddVertex(outB[i]);
            st.SetNormal(n);
            st.SetColor(side);
            st.AddVertex(outT[j]);
            st.SetNormal(n);
            st.SetColor(sideLow);
            st.AddVertex(outB[i]);
            st.SetNormal(n);
            st.SetColor(sideLow);
            st.AddVertex(outB[j]);
        }
    }

    private static void AddWaterSheet(SurfaceTool st, Cell cell, Color color)
    {
        float y = Board.SurfaceY(cell);
        var c = cell.Coord;
        var col = new Color(color.R * 1.05f, color.G * 1.1f, color.B * 1.15f, 0.62f);
        var center = Board.Center(c, y);
        for (int i = 0; i < 6; i++)
        {
            var a = Board.Corner(c, i, y, 1.0f);
            var b = Board.Corner(c, (i + 1) % 6, y, 1.0f);
            Tri(st, Vector3.Up, col, center, a, b);
        }
    }

    // ------------------------------------------------------------------ decorations

    private sealed class DecorBatches
    {
        public readonly List<Transform3D> Rocks = new();
        public readonly List<Color> RockColors = new();
        public readonly List<Transform3D> Trunks = new();
        public readonly List<Transform3D> Crowns = new();
        public readonly List<Color> CrownColors = new();
        public readonly List<Transform3D> Bushes = new();
        public readonly List<Color> BushColors = new();
        public readonly List<Transform3D> Crates = new();
        public readonly List<Transform3D> Rubble = new();
        public readonly List<Transform3D> Bubbles = new();
        public readonly List<Vector3> Altars = new();

        private static MultiMeshInstance3D Batch(Mesh mesh, List<Transform3D> xf, List<Color>? colors, Material mat, string name)
        {
            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseColors = colors != null,
                Mesh = mesh,
                InstanceCount = xf.Count,
            };
            for (int i = 0; i < xf.Count; i++)
            {
                mm.SetInstanceTransform(i, xf[i]);
                if (colors != null) mm.SetInstanceColor(i, colors[i]);
            }
            return new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = mat, Name = name };
        }

        public IEnumerable<Node3D> Build()
        {
            var vc = Mats.VertexColor(roughness: 0.85f);
            if (Rocks.Count > 0)
                yield return Batch(new SphereMesh { Radius = 0.5f, Height = 0.8f, RadialSegments = 7, Rings = 3 }, Rocks, RockColors, vc, "Rocks");
            if (Trunks.Count > 0)
                yield return Batch(new CylinderMesh { TopRadius = 0.06f, BottomRadius = 0.09f, Height = 0.5f, RadialSegments = 6, Rings = 1 }, Trunks, null,
                    Mats.Solid(new Color(0.42f, 0.30f, 0.20f)), "Trunks");
            if (Crowns.Count > 0)
                yield return Batch(new CylinderMesh { TopRadius = 0.0f, BottomRadius = 0.42f, Height = 0.95f, RadialSegments = 7, Rings = 1 }, Crowns, CrownColors, vc, "Crowns");
            if (Bushes.Count > 0)
                yield return Batch(new SphereMesh { Radius = 0.22f, Height = 0.32f, RadialSegments = 8, Rings = 4 }, Bushes, BushColors, vc, "Bushes");
            if (Crates.Count > 0)
                yield return Batch(new BoxMesh { Size = new Vector3(0.5f, 0.5f, 0.5f) }, Crates, null, Mats.Solid(new Color(0.62f, 0.45f, 0.26f), 0.9f), "Crates");
            if (Rubble.Count > 0)
                yield return Batch(new BoxMesh { Size = new Vector3(0.16f, 0.1f, 0.14f) }, Rubble, null, Mats.Solid(new Color(0.52f, 0.49f, 0.45f)), "Rubble");
            if (Bubbles.Count > 0)
                yield return Batch(new SphereMesh { Radius = 0.07f, Height = 0.1f, RadialSegments = 6, Rings = 3 }, Bubbles, null,
                    Mats.Emissive(new Color(0.65f, 0.80f, 0.25f), 0.6f), "Bubbles");
            foreach (var p in Altars)
            {
                var n = new Node3D { Position = p, Name = "Altar" };
                n.AddChild(new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = 0.32f, BottomRadius = 0.42f, Height = 0.16f, RadialSegments = 6 },
                    MaterialOverride = Mats.Solid(new Color(0.78f, 0.66f, 0.30f), 0.35f, 0.6f),
                    Position = new Vector3(0, 0.08f, 0),
                });
                n.AddChild(new MeshInstance3D
                {
                    Mesh = new TorusMesh { InnerRadius = 0.5f, OuterRadius = 0.56f, Rings = 24, RingSegments = 6 },
                    MaterialOverride = Mats.Emissive(new Color(1f, 0.85f, 0.4f), 1.2f),
                    Position = new Vector3(0, 0.03f, 0),
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                });
                yield return n;
            }
        }
    }

    private static Transform3D Xf(Vector3 pos, float yaw, Vector3 scale) =>
        new(new Basis(Vector3.Up, yaw).Scaled(scale), pos);

    private static void AddDecor(DecorBatches d, Cell cell, float y)
    {
        var c = cell.Coord;
        var center = Board.Center(c, y);
        float r1 = Hash01(c, 7), r2 = Hash01(c, 11), r3 = Hash01(c, 13);
        Vector3 Jit(float amount, int salt) => new((Hash01(c, salt) - 0.5f) * amount, 0, (Hash01(c, salt + 1) - 0.5f) * amount);
        var tc = Ui.Parse(cell.Terrain.Color, Colors.Gray);
        switch (cell.Terrain.Visual)
        {
            case TerrainVisual.Rock:
            {
                float s = 0.75f + r1 * 0.35f;
                d.Rocks.Add(Xf(center + Jit(0.2f, 3) + new Vector3(0, 0.22f * s, 0), r2 * 6.28f, new Vector3(1.1f * s, 0.95f * s, 0.9f * s)));
                d.RockColors.Add(tc.Lightened(0.05f));
                if (r3 > 0.4f)
                {
                    d.Rocks.Add(Xf(center + new Vector3(0.35f, 0.08f, 0.2f) + Jit(0.15f, 5), r3 * 6.28f, new Vector3(0.45f, 0.35f, 0.4f)));
                    d.RockColors.Add(tc.Darkened(0.08f));
                }
                break;
            }
            case TerrainVisual.Vegetation:
            {
                bool tall = cell.Terrain.ObstacleHeight >= 1.5;
                if (tall)
                {
                    int n = r1 > 0.5f ? 2 : 1;
                    for (int i = 0; i < n; i++)
                    {
                        var p = center + Jit(n == 1 ? 0.25f : 0.9f, 20 + i * 3);
                        float s = 0.85f + Hash01(c, 30 + i) * 0.35f;
                        d.Trunks.Add(Xf(p + new Vector3(0, 0.25f * s, 0), 0, new Vector3(s, s, s)));
                        d.Crowns.Add(Xf(p + new Vector3(0, (0.5f + 0.47f) * s, 0), Hash01(c, 40 + i) * 6.28f, new Vector3(s, s, s)));
                        d.CrownColors.Add(tc.Lightened(0.05f * i));
                        d.Crowns.Add(Xf(p + new Vector3(0, (0.95f + 0.3f) * s, 0), Hash01(c, 50 + i) * 6.28f, new Vector3(0.7f * s, 0.6f * s, 0.7f * s)));
                        d.CrownColors.Add(tc.Lightened(0.12f));
                    }
                }
                else
                {
                    for (int i = 0; i < 3; i++)
                    {
                        var p = center + Jit(0.8f, 60 + i * 3) + new Vector3(0, 0.1f, 0);
                        float s = 0.8f + Hash01(c, 70 + i) * 0.5f;
                        d.Bushes.Add(Xf(p, 0, new Vector3(s, s, s)));
                        d.BushColors.Add(tc.Lightened(0.05f + 0.06f * i));
                    }
                }
                break;
            }
            case TerrainVisual.Crate:
                d.Crates.Add(Xf(center + Jit(0.2f, 80) + new Vector3(0, 0.25f, 0), r1 * 0.8f, Vector3.One));
                d.Crates.Add(Xf(center + new Vector3(0.28f, 0.2f, 0.18f), r2, new Vector3(0.8f, 0.8f, 0.8f)));
                if (r3 > 0.3f) d.Crates.Add(Xf(center + Jit(0.15f, 85) + new Vector3(0, 0.7f, 0), r3, new Vector3(0.7f, 0.7f, 0.7f)));
                break;
            case TerrainVisual.Rubble:
                for (int i = 0; i < 5; i++)
                    d.Rubble.Add(Xf(center + Jit(1.0f, 90 + i * 2) + new Vector3(0, 0.05f, 0), Hash01(c, 100 + i) * 6.28f, Vector3.One * (0.8f + Hash01(c, 110 + i))));
                break;
            case TerrainVisual.Hazard:
                for (int i = 0; i < 3; i++)
                    d.Bubbles.Add(Xf(center + Jit(1.0f, 120 + i * 2) + new Vector3(0, 0.03f, 0), 0, Vector3.One * (0.7f + Hash01(c, 130 + i))));
                break;
            case TerrainVisual.Special:
                d.Altars.Add(center);
                break;
        }
    }
}
