using System;
using System.Collections.Generic;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Geometry;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using HexTactics.Game.UI;

namespace HexTactics.Game.Render;

/// <summary>
/// Builds the board: volumetric hex tiles from the core geometry (prisms with bevels, pits with
/// inner walls and a dark bottom, liquid basins), grouped by material (matte, metal, glossy,
/// glass, hologram, animated liquids), glowing accent lines (outlines, platform edges, wall
/// stripes, pit rims), patterns and symbols (never colour alone) and low-poly tech props.
/// Minimalist high-tech (docs/DECISIONS_2.md §20). Purely visual.
/// </summary>
public partial class TerrainNode : Node3D
{
    private static Shader? _liquidShader, _accentShader, _holoShader;

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

    private static Shader LoadShader(ref Shader? cache, string path)
    {
        cache ??= GD.Load<Shader>(path);
        return cache;
    }

    private sealed class Batch
    {
        public readonly SurfaceTool St = new();
        public bool Any;

        public Batch() => St.Begin(Mesh.PrimitiveType.Triangles);

        public void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 n, Color ca, Color cb, Color cc, Vector2 uv)
        {
            Any = true;
            // Core geometry is counter-clockwise around the normal; Godot front faces are clockwise.
            St.SetNormal(n);
            St.SetColor(ca);
            St.SetUV(uv);
            St.AddVertex(a);
            St.SetNormal(n);
            St.SetColor(cc);
            St.SetUV(uv);
            St.AddVertex(c);
            St.SetNormal(n);
            St.SetColor(cb);
            St.SetUV(uv);
            St.AddVertex(b);
        }

        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col, Vector2 uv, Vector3? n = null)
        {
            var nn = n ?? Vector3.Up;
            Tri(a, b, c, nn, col, col, col, uv);
            Tri(a, c, d, nn, col, col, col, uv);
        }
    }

    private readonly Dictionary<string, Batch> _batches = new();

    private Batch B(string key)
    {
        if (!_batches.TryGetValue(key, out var b))
        {
            b = new Batch();
            _batches[key] = b;
        }
        return b;
    }

    private static Vector3 V(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    public void Build(BattleMap map, bool showGrid = true, ContentDatabase? db = null)
    {
        foreach (var c in GetChildren())
        {
            RemoveChild(c);
            c.QueueFree();
        }
        _batches.Clear();

        int minH = 0;
        foreach (var c in map.Cells) minH = Math.Min(minH, c.Height);
        float deepest = 0;
        foreach (var c in map.Cells)
            if (Board.IsPit(c.Terrain))
                deepest = Math.Max(deepest, Math.Min(Board.PitDepth(c.Terrain), 1.6f));
        BaseY = minH * Board.LevelHeight - 0.45f - deepest * 0.5f;

        var decor = new DecorBatches();
        foreach (var cell in map.Cells)
        {
            BuildCell(cell);
            // An imported prop model replaces the built-in decoration (docs/DECISIONS_2.md §34); on any problem the decoration stays.
            if (!AddPropModel(cell, db)) AddDecor(decor, cell, Board.TopY(cell));
            AddTopMaterial(cell, db);
            AddSymbol(cell);
        }

        foreach (var (key, b) in _batches)
        {
            if (!b.Any) continue;
            var mesh = b.St.Commit();
            mesh.SurfaceSetMaterial(0, MaterialFor(key));
            var mi = new MeshInstance3D { Mesh = mesh, Name = "Terrain_" + key };
            if (key.StartsWith("accent", StringComparison.Ordinal) || key.StartsWith("holo", StringComparison.Ordinal) || key.EndsWith("_noshadow", StringComparison.Ordinal) || key.StartsWith("liquid", StringComparison.Ordinal))
                mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            AddChild(mi);
        }
        foreach (var n in decor.Build()) AddChild(n);
    }

    private bool AddPropModel(Cell cell, ContentDatabase? db)
    {
        var id = cell.Terrain.Look.PropModel;
        if (db == null || string.IsNullOrEmpty(id)) return false;
        var asset = db.Get<AssetDef>(id);
        var model = Assets.Model(asset);
        if (asset == null || model == null) return false;
        var holder = new Node3D
        {
            Name = "Prop_" + cell.Coord,
            Position = Board.Center(cell.Coord, Board.TopY(cell) + (float)asset.YOffset),
            RotationDegrees = new Vector3(0, (float)asset.RotationY + Hash01(cell.Coord, 21) * 360f, 0),
            Scale = Vector3.One * (float)asset.Scale * (0.9f + Hash01(cell.Coord, 22) * 0.2f),
        };
        holder.AddChild(model);
        AddChild(holder);
        return true;
    }

    private void AddTopMaterial(Cell cell, ContentDatabase? db)
    {
        var id = cell.Terrain.Look.TopMaterial;
        if (db == null || string.IsNullOrEmpty(id)) return;
        var mat = Assets.Material(db.Get<AssetDef>(id), db);
        if (mat == null) return;
        // A thin hexagonal plate over the top face (pointy-top hexes: the prism's corners at 30°).
        var plate = new MeshInstance3D
        {
            Name = "TopMat_" + cell.Coord,
            Mesh = new CylinderMesh { TopRadius = Board.HexSize * 0.9f, BottomRadius = Board.HexSize * 0.9f, Height = 0.012f, RadialSegments = 6, Rings = 1 },
            MaterialOverride = mat,
            Position = Board.Center(cell.Coord, Board.TopY(cell) + 0.008f),
            RotationDegrees = new Vector3(0, 30, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(plate);
    }

    private static Material MaterialFor(string key)
    {
        string k = key.Replace("_noshadow", "");
        switch (k)
        {
            case "liquid":
                return new ShaderMaterial { Shader = LoadShader(ref _liquidShader, "res://shaders/liquid.gdshader") };
            case "accent":
                return new ShaderMaterial { Shader = LoadShader(ref _accentShader, "res://shaders/accent.gdshader") };
            case "holo":
                return new ShaderMaterial { Shader = LoadShader(ref _holoShader, "res://shaders/hologram.gdshader") };
            case "glass":
                return new StandardMaterial3D
                {
                    VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    Roughness = 0.06f, Metallic = 0.1f, MetallicSpecular = 0.8f,
                };
            case "metal":
                return new StandardMaterial3D { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.55f, Metallic = 0.15f, MetallicSpecular = 0.45f };
            case "glossy":
                return new StandardMaterial3D { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.2f, MetallicSpecular = 0.6f };
            default:
                return new StandardMaterial3D { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.9f, MetallicSpecular = 0.3f };
        }
    }

    private static string SolidKey(TerrainLookDef look)
    {
        string k = look.Material switch
        {
            TerrainMaterial.Metal => "metal",
            TerrainMaterial.Glossy => "glossy",
            TerrainMaterial.Glass => "glass",
            TerrainMaterial.Hologram => "holo",
            TerrainMaterial.Liquid => "metal",
            _ => "matte",
        };
        if (!look.CastShadow && k != "holo") k += "_noshadow";
        return k;
    }

    private void BuildCell(Cell cell)
    {
        var t = cell.Terrain;
        var look = t.Look;
        var c = cell.Coord;
        var baseColor = Ui.Parse(t.Color, new Color(0.45f, 0.48f, 0.52f));
        float v = 0.96f + 0.06f * Hash01(c, 1);
        var top = new Color(baseColor.R * v, baseColor.G * v, baseColor.B * v);
        var side = string.IsNullOrEmpty(look.SideColor) ? top.Darkened(0.4f) : Ui.Parse(look.SideColor, top.Darkened(0.4f));
        var accent = Ui.Parse(look.AccentColor, new Color(0.37f, 0.83f, 1f));
        float y = cell.Height * Board.LevelHeight;
        bool pit = Board.IsPit(t);
        bool liquid = Board.IsLiquid(t);
        float bevel = (float)look.Bevel;

        var shape = new TileShape
        {
            Coord = c,
            HexSize = Board.HexSize,
            Gap = Board.Gap,
            TopY = y,
            BaseY = BaseY,
            Bevel = pit ? Math.Min(bevel, 0.04f) : bevel,
            ExtraHeight = pit || liquid ? 0 : Board.ExtraHeight(t),
            Depth = pit ? Board.PitDepth(t) : liquid ? Board.LiquidDepth(t) : 0,
            LiquidDrop = liquid ? Board.LiquidDrop : -1,
        };
        var tris = HexGeometry.Build(shape);
        string solidKey = SolidKey(look);
        float alpha = 1f - (float)Math.Clamp(look.Transparency, 0, 0.95);
        bool holo = look.Material == TerrainMaterial.Hologram;
        float glow = (float)look.Glow;
        var frame = new Color(0.27f, 0.30f, 0.34f);

        foreach (var tri in tris)
        {
            Color col;
            string key = solidKey;
            var uv = Vector2.Zero;
            switch (tri.Part)
            {
                case TilePart.Top:
                    col = top;
                    break;
                case TilePart.Bevel:
                    col = top.Lightened(0.12f);
                    break;
                case TilePart.Side:
                    col = side;
                    break;
                case TilePart.Rim:
                    col = pit ? new Color(0.20f, 0.22f, 0.25f) : frame;
                    key = "metal";
                    break;
                case TilePart.InnerWall:
                    col = pit ? new Color(0.09f, 0.10f, 0.12f) : top.Darkened(0.55f);
                    key = "matte";
                    break;
                case TilePart.Bottom:
                    col = pit ? new Color(0.012f, 0.014f, 0.018f) : top.Darkened(0.65f);
                    key = "matte";
                    break;
                default:
                    col = new Color(top, liquid ? Math.Min(alpha, look.Transparency > 0 ? alpha : 1f) : 1f);
                    key = "liquid";
                    uv = new Vector2(glow, (float)look.Animation);
                    break;
            }
            if (liquid && tri.Part is TilePart.Side) key = "metal";
            if (holo && tri.Part is TilePart.Top or TilePart.Bevel or TilePart.Side)
            {
                key = "holo";
                col = new Color(accent, alpha);
                uv = new Vector2(glow, 0);
            }
            else if (key == "glass" && tri.Part is TilePart.Top or TilePart.Bevel)
            {
                col = new Color(col, alpha);
            }
            Color Shade(float s) => s >= 0.999f ? col : new Color(col.R * s, col.G * s, col.B * s, col.A);
            B(key).Tri(V(tri.A), V(tri.B), V(tri.C), V(tri.Normal), Shade(tri.ShadeA), Shade(tri.ShadeB), Shade(tri.ShadeC), uv);
        }

        float topY = pit || liquid ? y : y + Board.ExtraHeight(t);
        float outer = 1f - Board.Gap;
        float inner = pit || liquid ? outer - Math.Max(0.06f, Math.Min(bevel, 0.04f) + 0.05f) : outer - bevel * 0.9f;
        var acc = B("accent");

        // Outline of the top face.
        if (!string.IsNullOrEmpty(look.OutlineColor) && !pit)
            Ring(acc, c, topY + 0.004f, inner, inner - (float)look.OutlineWidth, Ui.Parse(look.OutlineColor, Colors.White), new Vector2(glow > 0 ? glow * 0.5f : 0.05f, 0));
        // Glowing accent: pit/liquid rim edge, platform edge, wall stripe.
        if (glow > 0)
        {
            if (pit)
                Ring(acc, c, y + 0.006f, inner + 0.004f, inner - 0.035f, accent, new Vector2(glow, 2.2f));
            else if (liquid)
                Ring(acc, c, y + 0.004f, inner + 0.004f, inner - 0.025f, accent, new Vector2(glow * 0.4f, 0));
            else if (Board.ExtraHeight(t) > 0 && !holo)
                SideBand(acc, c, topY - 0.12f, topY - 0.07f, outer + 0.004f, accent, new Vector2(glow, 0));
            else if (!holo)
                Ring(acc, c, topY + 0.005f, inner - 0.01f, inner - 0.045f, accent, new Vector2(glow, 0));
        }
        if (look.Pattern != TerrainPattern.None) AddPattern(acc, c, pit ? y + 0.007f : topY + 0.006f, pit ? outer : inner - 0.06f, pit ? inner : 0, look.Pattern, accent, glow);
    }

    private static void Ring(Batch b, HexCoord c, float y, float outer, float inner, Color col, Vector2 uv)
    {
        for (int i = 0; i < 6; i++)
        {
            int j = (i + 1) % 6;
            var o0 = Board.Corner(c, i, y, outer);
            var o1 = Board.Corner(c, j, y, outer);
            var i0 = Board.Corner(c, i, y, inner);
            var i1 = Board.Corner(c, j, y, inner);
            b.Quad(o0, o1, i1, i0, col, uv);
        }
    }

    private static void SideBand(Batch b, HexCoord c, float y0, float y1, float radius, Color col, Vector2 uv)
    {
        var ctr = Board.Center(c, 0);
        for (int i = 0; i < 6; i++)
        {
            int j = (i + 1) % 6;
            var a0 = Board.Corner(c, i, y0, radius);
            var a1 = Board.Corner(c, j, y0, radius);
            var b0 = Board.Corner(c, i, y1, radius);
            var b1 = Board.Corner(c, j, y1, radius);
            var mid = (a0 + a1) * 0.5f;
            var n = new Vector3(mid.X - ctr.X, 0, mid.Z - ctr.Z).Normalized();
            b.Quad(a0, a1, b1, b0, col, uv, n);
        }
    }

    private static void Strip(Batch b, Vector3 a, Vector3 c, float width, Color col, Vector2 uv)
    {
        var d = c - a;
        d.Y = 0;
        if (d.LengthSquared() < 1e-8f) return;
        var side = new Vector3(-d.Z, 0, d.X).Normalized() * (width / 2);
        b.Quad(a + side, c + side, c - side, a - side, col, uv);
    }

    /// <summary>Patterns: an extra cue besides colour (docs/DECISIONS_2.md §20).</summary>
    private static void AddPattern(Batch b, HexCoord c, float y, float outer, float innerHole, TerrainPattern p, Color accent, float glow)
    {
        var ctr = Board.Center(c, y);
        var uv = new Vector2(Math.Max(0.15f, glow * 0.35f), 0);
        var dim = new Color(accent, 0.9f);
        switch (p)
        {
            case TerrainPattern.Stripes:
                for (int s = -2; s <= 2; s++)
                {
                    float off = s * 0.22f;
                    float half = Mathf.Sqrt(Math.Max(0.01f, outer * outer * 0.72f - off * off));
                    Strip(b, ctr + new Vector3(off, 0, -half), ctr + new Vector3(off, 0, half), 0.05f, dim, uv);
                }
                break;
            case TerrainPattern.Grid:
                for (int s = -1; s <= 1; s += 2)
                {
                    Strip(b, ctr + new Vector3(s * 0.25f, 0, -outer * 0.7f), ctr + new Vector3(s * 0.25f, 0, outer * 0.7f), 0.035f, dim, uv);
                    Strip(b, ctr + new Vector3(-outer * 0.7f, 0, s * 0.25f), ctr + new Vector3(outer * 0.7f, 0, s * 0.25f), 0.035f, dim, uv);
                }
                break;
            case TerrainPattern.Chevrons:
                for (int k = -1; k <= 1; k++)
                {
                    var tip = ctr + new Vector3(k * 0.28f + 0.12f, 0, 0);
                    Strip(b, tip + new Vector3(-0.2f, 0, -0.32f), tip, 0.07f, dim, uv);
                    Strip(b, tip + new Vector3(-0.2f, 0, 0.32f), tip, 0.07f, dim, uv);
                }
                break;
            case TerrainPattern.Dots:
                for (int k = 0; k < 6; k++)
                {
                    float a = k * Mathf.Pi / 3 + 0.3f;
                    Disc(b, ctr + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * outer * 0.45f, 0.07f, dim, uv);
                }
                Disc(b, ctr, 0.08f, dim, uv);
                break;
            case TerrainPattern.Cross:
                Strip(b, ctr + new Vector3(-0.4f, 0, -0.4f), ctr + new Vector3(0.4f, 0, 0.4f), 0.08f, dim, uv);
                Strip(b, ctr + new Vector3(-0.4f, 0, 0.4f), ctr + new Vector3(0.4f, 0, -0.4f), 0.08f, dim, uv);
                break;
            case TerrainPattern.Ring:
                Ring(b, c, y, outer * 0.62f, outer * 0.52f, dim, uv);
                break;
            case TerrainPattern.Hazard:
            {
                // Yellow / black warning band: on the rim of a hole, or around the top of a tile.
                float o = outer, i = innerHole > 0 ? innerHole : outer - 0.12f;
                var black = new Color(0.05f, 0.05f, 0.05f);
                for (int k = 0; k < 6; k++)
                for (int part = 0; part < 3; part++)
                {
                    float t0 = part / 3f, t1 = (part + 1) / 3f;
                    var o0 = Board.Corner(c, k, y, o).Lerp(Board.Corner(c, (k + 1) % 6, y, o), t0);
                    var o1 = Board.Corner(c, k, y, o).Lerp(Board.Corner(c, (k + 1) % 6, y, o), t1);
                    var i0 = Board.Corner(c, k, y, i).Lerp(Board.Corner(c, (k + 1) % 6, y, i), t0);
                    var i1 = Board.Corner(c, k, y, i).Lerp(Board.Corner(c, (k + 1) % 6, y, i), t1);
                    b.Quad(o0, o1, i1, i0, (k * 3 + part) % 2 == 0 ? new Color(1f, 0.82f, 0.2f) : black, (k * 3 + part) % 2 == 0 ? uv : Vector2.Zero);
                }
                break;
            }
        }
    }

    private static void Disc(Batch b, Vector3 p, float r, Color col, Vector2 uv)
    {
        const int seg = 8;
        for (int s = 0; s < seg; s++)
        {
            float a0 = s * Mathf.Tau / seg, a1 = (s + 1) * Mathf.Tau / seg;
            b.Tri(p, p + new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * r, p + new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * r, Vector3.Up, col, col, col, uv);
        }
    }

    private void AddSymbol(Cell cell)
    {
        var sym = cell.Terrain.Look.Symbol;
        if (string.IsNullOrWhiteSpace(sym)) return;
        float y = Board.SurfaceY(cell) + 0.02f;
        var accent = Ui.Parse(cell.Terrain.Look.AccentColor, Colors.White);
        var l = new Label3D
        {
            Text = sym,
            FontSize = 96,
            PixelSize = 0.0042f,
            Modulate = new Color(accent, 0.9f),
            OutlineSize = 10,
            OutlineModulate = new Color(0, 0, 0, 0.6f),
            Billboard = BaseMaterial3D.BillboardModeEnum.Disabled,
            Shaded = false,
            DoubleSided = true,
            Position = Board.Center(cell.Coord, y) + new Vector3(0.38f, 0, 0.22f),
            RotationDegrees = new Vector3(-90, 0, 0),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Name = "Symbol",
        };
        AddChild(l);
    }

    // ------------------------------------------------------------------ props

    private sealed class DecorBatches
    {
        public readonly List<Transform3D> Blocks = new();
        public readonly List<Color> BlockColors = new();
        public readonly List<Transform3D> Pillars = new();
        public readonly List<Transform3D> PillarRings = new();
        public readonly List<Color> RingColors = new();
        public readonly List<Transform3D> Rails = new();
        public readonly List<Transform3D> Crates = new();
        public readonly List<Transform3D> CrateStripes = new();
        public readonly List<Transform3D> Rubble = new();
        public readonly List<Transform3D> Bubbles = new();
        public readonly List<Color> BubbleColors = new();
        public readonly List<Transform3D> Spikes = new();
        public readonly List<(Vector3 Pos, Color Accent)> Terminals = new();

        private static MultiMeshInstance3D Batch(Mesh mesh, List<Transform3D> xf, List<Color>? colors, Material mat, string name, bool shadow = true)
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
            return new MultiMeshInstance3D
            {
                Multimesh = mm, MaterialOverride = mat, Name = name,
                CastShadow = shadow ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
            };
        }

        public IEnumerable<Node3D> Build()
        {
            var vc = Mats.VertexColor(roughness: 0.6f);
            if (Blocks.Count > 0)
                yield return Batch(new BoxMesh { Size = new Vector3(0.62f, 0.5f, 0.55f) }, Blocks, BlockColors, vc, "Blocks");
            if (Pillars.Count > 0)
                yield return Batch(new CylinderMesh { TopRadius = 0.14f, BottomRadius = 0.17f, Height = 1f, RadialSegments = 6, Rings = 1 }, Pillars, null,
                    Mats.Solid(new Color(0.34f, 0.38f, 0.43f), 0.4f, 0.3f), "Pillars");
            if (PillarRings.Count > 0)
                yield return Batch(new CylinderMesh { TopRadius = 0.18f, BottomRadius = 0.18f, Height = 0.05f, RadialSegments = 6, Rings = 1 }, PillarRings, RingColors,
                    Mats.VertexColor(unshaded: true), "PillarRings", false);
            if (Rails.Count > 0)
                yield return Batch(new BoxMesh { Size = new Vector3(0.9f, 0.07f, 0.07f) }, Rails, null, Mats.Solid(new Color(0.55f, 0.6f, 0.66f), 0.35f, 0.4f), "Rails");
            if (Crates.Count > 0)
                yield return Batch(new BoxMesh { Size = new Vector3(0.5f, 0.5f, 0.5f) }, Crates, null, Mats.Solid(new Color(0.76f, 0.46f, 0.18f), 0.5f, 0.25f), "Crates");
            if (CrateStripes.Count > 0)
                yield return Batch(new BoxMesh { Size = new Vector3(0.52f, 0.06f, 0.52f) }, CrateStripes, null, Mats.Emissive(new Color(1f, 0.82f, 0.25f), 0.6f), "CrateStripes", false);
            if (Rubble.Count > 0)
                yield return Batch(new BoxMesh { Size = new Vector3(0.16f, 0.1f, 0.14f) }, Rubble, null, Mats.Solid(new Color(0.45f, 0.47f, 0.5f)), "Rubble");
            if (Bubbles.Count > 0)
                yield return Batch(new SphereMesh { Radius = 0.07f, Height = 0.1f, RadialSegments = 6, Rings = 3 }, Bubbles, BubbleColors, Mats.VertexColor(unshaded: true), "Bubbles", false);
            if (Spikes.Count > 0)
                yield return Batch(new CylinderMesh { TopRadius = 0f, BottomRadius = 0.05f, Height = 0.12f, RadialSegments = 4, Rings = 1 }, Spikes, null,
                    Mats.Solid(new Color(0.75f, 0.78f, 0.82f), 0.3f, 0.6f), "Spikes");
            foreach (var (p, accent) in Terminals)
            {
                var n = new Node3D { Position = p, Name = "Terminal" };
                n.AddChild(new MeshInstance3D
                {
                    Mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 0.42f, Height = 0.18f, RadialSegments = 6 },
                    MaterialOverride = Mats.Solid(new Color(0.22f, 0.26f, 0.31f), 0.35f, 0.4f),
                    Position = new Vector3(0, 0.09f, 0),
                });
                n.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(0.36f, 0.24f, 0.03f) },
                    MaterialOverride = Mats.Emissive(accent, 1.6f),
                    Position = new Vector3(0, 0.34f, 0),
                    RotationDegrees = new Vector3(-20, 0, 0),
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
        var t = cell.Terrain;
        var center = Board.Center(c, y);
        float r1 = Hash01(c, 7), r2 = Hash01(c, 11), r3 = Hash01(c, 13);
        Vector3 Jit(float amount, int salt) => new((Hash01(c, salt) - 0.5f) * amount, 0, (Hash01(c, salt + 1) - 0.5f) * amount);
        var tc = Ui.Parse(t.Color, Colors.Gray);
        var accent = Ui.Parse(t.Look.AccentColor, new Color(0.37f, 0.83f, 1f));
        var visual = t.Visual;
        if (visual == TerrainVisual.Vegetation) visual = t.ObstacleHeight >= 1.5 ? TerrainVisual.Pillars : TerrainVisual.Railing;
        switch (visual)
        {
            case TerrainVisual.Rock:
            {
                float s = 0.85f + r1 * 0.3f;
                d.Blocks.Add(Xf(center + Jit(0.2f, 3) + new Vector3(0, 0.25f * s, 0), r2 * 1.2f, new Vector3(s, s, s)));
                d.BlockColors.Add(tc);
                if (r3 > 0.45f)
                {
                    d.Blocks.Add(Xf(center + new Vector3(0.33f, 0.12f, 0.22f), r3 * 1.5f, new Vector3(0.45f, 0.45f, 0.45f)));
                    d.BlockColors.Add(tc.Darkened(0.12f));
                }
                break;
            }
            case TerrainVisual.Pillars:
            {
                float h = (float)Math.Max(1.0, t.ObstacleHeight * Board.LevelHeight * 1.1);
                for (int i = 0; i < 3; i++)
                {
                    float a = i * Mathf.Tau / 3 + r1;
                    var p = center + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 0.42f;
                    d.Pillars.Add(Xf(p + new Vector3(0, h / 2, 0), 0, new Vector3(1, h, 1)));
                    d.PillarRings.Add(Xf(p + new Vector3(0, h * 0.82f, 0), 0, Vector3.One));
                    d.RingColors.Add(accent * 1.8f);
                }
                break;
            }
            case TerrainVisual.Railing:
                for (int i = 0; i < 3; i++)
                {
                    float yaw = (i * 2 + (int)(r1 * 6)) * Mathf.Pi / 3 + Mathf.Pi / 6;
                    var dir = new Vector3(Mathf.Cos(yaw), 0, Mathf.Sin(yaw));
                    var p = center + dir * 0.62f;
                    d.Rails.Add(Xf(p + new Vector3(0, 0.32f, 0), -yaw + Mathf.Pi / 2, Vector3.One));
                    d.Rails.Add(Xf(p + new Vector3(0, 0.16f, 0), -yaw + Mathf.Pi / 2, Vector3.One));
                }
                break;
            case TerrainVisual.Crate:
                d.Crates.Add(Xf(center + Jit(0.2f, 80) + new Vector3(0, 0.25f, 0), r1 * 0.8f, Vector3.One));
                d.CrateStripes.Add(Xf(center + Jit(0.2f, 80) + new Vector3(0, 0.38f, 0), r1 * 0.8f, Vector3.One));
                d.Crates.Add(Xf(center + new Vector3(0.28f, 0.2f, 0.18f), r2, new Vector3(0.8f, 0.8f, 0.8f)));
                if (r3 > 0.3f) d.Crates.Add(Xf(center + Jit(0.15f, 85) + new Vector3(0, 0.7f, 0), r3, new Vector3(0.7f, 0.7f, 0.7f)));
                break;
            case TerrainVisual.Rubble:
                for (int i = 0; i < 5; i++)
                    d.Rubble.Add(Xf(center + Jit(1.0f, 90 + i * 2) + new Vector3(0, 0.05f, 0), Hash01(c, 100 + i) * 6.28f, Vector3.One * (0.8f + Hash01(c, 110 + i))));
                break;
            case TerrainVisual.Hazard:
            case TerrainVisual.Acid:
                for (int i = 0; i < 3; i++)
                {
                    d.Bubbles.Add(Xf(Board.Center(c, Board.SurfaceY(cell)) + Jit(1.0f, 120 + i * 2) + new Vector3(0, 0.03f, 0), 0, Vector3.One * (0.7f + Hash01(c, 130 + i))));
                    d.BubbleColors.Add(accent * 1.6f);
                }
                break;
            case TerrainVisual.Trap:
                for (int i = 0; i < 7; i++)
                {
                    float a = i * Mathf.Tau / 6;
                    var p = i == 6 ? center : center + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 0.32f;
                    d.Spikes.Add(Xf(p + new Vector3(0, 0.05f, 0), 0, Vector3.One));
                }
                break;
            case TerrainVisual.Special:
            case TerrainVisual.Terminal:
                d.Terminals.Add((center, accent));
                break;
        }
    }
}
