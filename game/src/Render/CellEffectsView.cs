using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Sim;
using HexTactics.Game.UI;

namespace HexTactics.Game.Render;

/// <summary>
/// Visuals of cell effects on top of terrain (the terrain mesh itself never changes for an effect).
/// One small animated node per effect instance, keyed by its serial number.
/// </summary>
public partial class CellEffectsView : Node3D
{
    private readonly Dictionary<long, (Node3D Node, CellEffectInstance Inst, Cell Cell)> _nodes = new();
    private int _version = -1;
    private float _t;
    private readonly Random _rng = new(1);

    /// <summary>Content for the effects' own looks (cyclic visual effects of docs/DECISIONS_3.md §47).</summary>
    public ContentDatabase? Db { get; set; }

    public void Sync(BattleMap map, Func<Cell, bool> visible)
    {
        if (map.VisualVersion == _version) return;
        _version = map.VisualVersion;
        var alive = new HashSet<long>();
        foreach (var cell in map.Cells)
        {
            foreach (var e in cell.Effects)
            {
                if (e.Intrinsic) continue; // intrinsic effects are drawn by the terrain style
                alive.Add(e.Serial);
                if (_nodes.ContainsKey(e.Serial)) continue;
                var n = Make(e.Def, cell);
                AddChild(n);
                AttachLoop(n, e.Def, cell);
                _nodes[e.Serial] = (n, e, cell);
            }
        }
        foreach (var key in new List<long>(_nodes.Keys))
        {
            if (alive.Contains(key)) continue;
            _nodes[key].Node.QueueFree();
            _nodes.Remove(key);
        }
        foreach (var (node, _, cell) in _nodes.Values) node.Visible = visible(cell);
    }

    /// <summary>
    /// The effect's cyclic visual effect (fire, ice, poison pool…): replaces the built-in style, is added over it, or is
    /// not duplicated on the cell — by the effect's «наложение». Only the look: the damage stays in the effect's data.
    /// </summary>
    private void AttachLoop(Node3D n, CellEffectDef def, Cell cell)
    {
        if (Db == null || string.IsNullOrEmpty(def.LoopVfx) || Db.Get<VfxDef>(def.LoopVfx) is not { } vfx) return;
        if (vfx.Overlay == VfxOverlay.NoDuplicate &&
            _nodes.Values.Any(x => x.Cell == cell && x.Node.HasMeta("loop") && (string)x.Node.GetMeta("loop") == vfx.Id))
            return;
        if (vfx.Overlay == VfxOverlay.Replace)
            foreach (var c in n.GetChildren())
                if (c is not Label3D)
                    c.QueueFree();
        n.SetMeta("loop", vfx.Id);
        var loop = Vfx.Start(n, vfx, n.GlobalPosition, Db, persistent: true);
        if (loop != null && !vfx.Loop) loop.SetMeta("oneshot", true);
    }

    public void UpdateVisibility(Func<Cell, bool> visible)
    {
        foreach (var (node, _, cell) in _nodes.Values) node.Visible = visible(cell);
    }

    private static MeshInstance3D M(Node3D parent, Mesh mesh, Material mat, Vector3 pos, Vector3? scale = null)
    {
        var m = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Position = pos, Scale = scale ?? Vector3.One, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        parent.AddChild(m);
        return m;
    }

    private Node3D Make(CellEffectDef def, Cell cell)
    {
        var root = new Node3D { Position = Board.Center(cell.Coord, Math.Max(Board.TopY(cell), Board.SurfaceY(cell))), Name = def.Id };
        var c = Ui.Parse(def.Color, new Color(1, 0.5f, 0.2f));
        float r() => (float)_rng.NextDouble();
        switch (def.Visual)
        {
            case CellEffectVisual.Flames:
                for (int i = 0; i < 5; i++)
                {
                    float h = 0.45f + r() * 0.35f;
                    var p = new Vector3((r() - 0.5f) * 0.95f, h / 2, (r() - 0.5f) * 0.95f);
                    var col = i % 3 == 0 ? new Color(1f, 0.82f, 0.25f) : i % 3 == 1 ? c : new Color(0.95f, 0.32f, 0.12f);
                    M(root, new CylinderMesh { TopRadius = 0, BottomRadius = 0.16f + r() * 0.08f, Height = h, RadialSegments = 5, Rings = 1 }, Mats.Emissive(col, 1.8f), p);
                }
                // A light on every third burning cell is enough for the glow and keeps the light count low.
                if (((cell.Coord.Q * 7 + cell.Coord.R * 13) % 3 + 3) % 3 == 0)
                    root.AddChild(new OmniLight3D { LightColor = c, LightEnergy = 1.1f, OmniRange = 3.2f, Position = new Vector3(0, 0.7f, 0), ShadowEnabled = false });
                break;
            case CellEffectVisual.Gas:
                for (int i = 0; i < 3; i++)
                    M(root, new SphereMesh { Radius = 0.32f, Height = 0.45f, RadialSegments = 10, Rings = 5 }, Mats.Transparent(new Color(c.R, c.G, c.B, 0.38f)),
                        new Vector3((r() - 0.5f) * 0.7f, 0.25f + r() * 0.15f, (r() - 0.5f) * 0.7f));
                break;
            case CellEffectVisual.Smoke:
                for (int i = 0; i < 4; i++)
                    M(root, new SphereMesh { Radius = 0.42f, Height = 0.7f, RadialSegments = 10, Rings = 5 }, Mats.Transparent(new Color(c.R, c.G, c.B, 0.55f)),
                        new Vector3((r() - 0.5f) * 0.7f, 0.35f + r() * 0.3f, (r() - 0.5f) * 0.7f));
                break;
            case CellEffectVisual.Ice:
                M(root, new CylinderMesh { TopRadius = 0.86f, BottomRadius = 0.86f, Height = 0.03f, RadialSegments = 6 }, Mats.Transparent(new Color(c.R, c.G, c.B, 0.55f)), new Vector3(0, 0.02f, 0))
                    .RotationDegrees = new Vector3(0, 30, 0);
                for (int i = 0; i < 3; i++)
                    M(root, new PrismMesh { Size = new Vector3(0.12f, 0.3f, 0.12f) }, Mats.Solid(c.Lightened(0.3f), 0.1f), new Vector3((r() - 0.5f) * 0.9f, 0.15f, (r() - 0.5f) * 0.9f));
                break;
            case CellEffectVisual.Sparks:
                for (int i = 0; i < 4; i++)
                {
                    var m = M(root, new BoxMesh { Size = new Vector3(0.04f, 0.04f, 0.45f) }, Mats.Emissive(c, 3f), new Vector3((r() - 0.5f) * 0.8f, 0.12f + r() * 0.2f, (r() - 0.5f) * 0.8f));
                    m.RotationDegrees = new Vector3(r() * 40 - 20, r() * 360, 0);
                }
                break;
            case CellEffectVisual.Liquid:
                M(root, new CylinderMesh { TopRadius = 0.75f, BottomRadius = 0.75f, Height = 0.02f, RadialSegments = 18 }, Mats.Transparent(new Color(c.R, c.G, c.B, 0.5f)), new Vector3(0, 0.015f, 0));
                break;
            case CellEffectVisual.Runes:
                M(root, new TorusMesh { InnerRadius = 0.6f, OuterRadius = 0.68f, Rings = 6, RingSegments = 4 }, Mats.Emissive(c, 1.6f), new Vector3(0, 0.03f, 0));
                M(root, new TorusMesh { InnerRadius = 0.28f, OuterRadius = 0.33f, Rings = 3, RingSegments = 4 }, Mats.Emissive(c, 1.6f), new Vector3(0, 0.03f, 0));
                break;
            case CellEffectVisual.Barrier:
                M(root, new CylinderMesh { TopRadius = 0.9f, BottomRadius = 0.9f, Height = 1.1f, RadialSegments = 6 }, Mats.Transparent(new Color(c.R, c.G, c.B, 0.35f)), new Vector3(0, 0.55f, 0))
                    .RotationDegrees = new Vector3(0, 30, 0);
                break;
            default:
                M(root, new TorusMesh { InnerRadius = 0.55f, OuterRadius = 0.7f, Rings = 6, RingSegments = 4 }, Mats.Emissive(c, 1.2f), new Vector3(0, 0.03f, 0));
                break;
        }
        if (!string.IsNullOrEmpty(def.Icon))
        {
            root.AddChild(new Label3D
            {
                Text = def.Icon,
                FontSize = 40,
                PixelSize = 0.0045f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Position = new Vector3(0.45f, 0.35f, 0.35f),
                Modulate = c.Lightened(0.35f),
                OutlineSize = 8,
                Shaded = false,
            });
        }
        return root;
    }

    public override void _Process(double delta)
    {
        _t += (float)delta;
        foreach (var (node, inst, _) in _nodes.Values)
        {
            if (!node.Visible) continue;
            float fade = (float)Math.Clamp(inst.Fraction * 3, 0.25, 1);
            switch (inst.Def.Visual)
            {
                case CellEffectVisual.Flames:
                {
                    int i = 0;
                    foreach (var ch in node.GetChildren())
                    {
                        if (ch is MeshInstance3D m)
                        {
                            float f = 0.75f + 0.35f * Mathf.Sin(_t * (9 + i * 2.3f) + i * 1.7f);
                            m.Scale = new Vector3(1, f * fade * (0.8f + 0.2f * inst.Level), 1);
                        }
                        else if (ch is OmniLight3D l)
                        {
                            l.LightEnergy = 0.6f + 0.25f * Mathf.Sin(_t * 13);
                        }
                        i++;
                    }
                    break;
                }
                case CellEffectVisual.Gas:
                case CellEffectVisual.Smoke:
                {
                    int i = 0;
                    foreach (var ch in node.GetChildren())
                    {
                        if (ch is not MeshInstance3D m) continue;
                        float s = (0.85f + 0.15f * Mathf.Sin(_t * 1.3f + i)) * fade;
                        m.Scale = new Vector3(s, s, s);
                        i++;
                    }
                    node.RotationDegrees = new Vector3(0, _t * 12, 0);
                    break;
                }
                case CellEffectVisual.Sparks:
                {
                    int i = 0;
                    foreach (var ch in node.GetChildren())
                    {
                        if (ch is MeshInstance3D m) m.Visible = Mathf.Sin(_t * 23 + i * 2.1f) > -0.2f;
                        i++;
                    }
                    break;
                }
                case CellEffectVisual.Runes:
                    node.RotationDegrees = new Vector3(0, _t * 25, 0);
                    break;
            }
        }
    }
}
