using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using HexTactics.Game.UI;

namespace HexTactics.Game.Render;

/// <summary>
/// Scenario objects without health on the board (docs/DECISIONS_4.md §56): a spawn base is a portal, a checkpoint a
/// flag, a route point a post, anything else a marker stone. Objects with health are structures and are drawn by
/// <see cref="UnitView"/>. Rebuilt only when the objects change; the portals turn slowly.
/// </summary>
public partial class ScenarioObjectsView : Node3D
{
    private string _key = "";
    private readonly List<Node3D> _spin = new();

    /// <summary>Also draws objects with health (the editor has no simulation for them).</summary>
    public bool IncludeStructures { get; set; }

    public void Sync(ScenarioDef? sc, BattleMap? map, ContentDatabase db)
    {
        string key = sc == null || map == null ? "" : string.Join(";", sc.Objects.Select(o =>
            $"{o.Id}|{o.Type}|{o.Anchor}|{o.Facing}|{o.Faction}|{o.Color}|{o.Health != null}|{string.Join(",", o.Footprint)}")) + IncludeStructures;
        if (key == _key) return;
        _key = key;
        foreach (var c in GetChildren()) c.QueueFree();
        _spin.Clear();
        if (sc == null || map == null) return;
        foreach (var o in sc.Objects)
        {
            if (o.Health != null && !IncludeStructures) continue;
            if (!map.Contains(o.Anchor)) continue;
            var fcol = Ui.Parse(o.Color, Ui.Parse(db.Faction(o.Faction)?.Color, new Color(0.7f, 0.7f, 0.7f)));
            var node = new Node3D { Name = "Obj_" + o.Id, Position = Board.Center(o.Anchor, Board.TopY(map.Get(o.Anchor))) };
            AddChild(node);
            foreach (var c in o.Cells().Where(map.Contains))
            {
                var p = Board.Center(c, Board.TopY(map.Get(c))) - node.Position;
                Part(node, new CylinderMesh { TopRadius = 0.82f, BottomRadius = 0.86f, Height = 0.08f, RadialSegments = 6, Rings = 1 },
                    Mats.Solid(fcol.Darkened(0.55f), 0.8f), p + new Vector3(0, 0.04f, 0), new Vector3(0, 30, 0));
            }
            Build(node, o, fcol);
        }
    }

    private void Build(Node3D node, ScenarioObjectDef o, Color fcol)
    {
        var glow = Mats.Emissive(fcol.Lightened(0.2f), 1.3f);
        switch (o.Type)
        {
            case ScenarioObjectType.SpawnBase:
            {
                // Portal: an upright ring turning slowly over a glowing disc.
                var spin = new Node3D { Name = "Portal", Position = new Vector3(0, 0.62f, 0) };
                node.AddChild(spin);
                Part(spin, new TorusMesh { InnerRadius = 0.42f, OuterRadius = 0.52f, Rings = 28, RingSegments = 6 }, glow, Vector3.Zero, new Vector3(90, 0, 0));
                Part(spin, new CylinderMesh { TopRadius = 0.42f, BottomRadius = 0.42f, Height = 0.02f, RadialSegments = 24 },
                    Mats.Transparent(new Color(fcol.R, fcol.G, fcol.B, 0.35f), unshaded: true), Vector3.Zero, new Vector3(90, 0, 0));
                _spin.Add(spin);
                Part(node, new CylinderMesh { TopRadius = 0.5f, BottomRadius = 0.6f, Height = 0.1f, RadialSegments = 6 }, glow, new Vector3(0, 0.1f, 0), new Vector3(0, 30, 0));
                break;
            }
            case ScenarioObjectType.Checkpoint:
                Part(node, new CylinderMesh { TopRadius = 0.04f, BottomRadius = 0.05f, Height = 1.2f, RadialSegments = 6 }, Mats.Solid(new Color(0.8f, 0.8f, 0.82f), 0.4f, 0.6f), new Vector3(0, 0.6f, 0));
                Part(node, new PrismMesh { Size = new Vector3(0.45f, 0.32f, 0.03f), LeftToRight = 0f }, glow, new Vector3(0.24f, 1.02f, 0), new Vector3(0, 0, -90));
                break;
            case ScenarioObjectType.RoutePoint:
                Part(node, new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.16f, Height = 0.45f, RadialSegments = 6 }, glow, new Vector3(0, 0.25f, 0));
                break;
            case ScenarioObjectType.BehaviorZone:
                Part(node, new TorusMesh { InnerRadius = 0.6f, OuterRadius = 0.66f, Rings = 24, RingSegments = 4 }, glow, new Vector3(0, 0.1f, 0));
                break;
            default:
                Part(node, new CylinderMesh { TopRadius = 0f, BottomRadius = 0.25f, Height = 0.45f, RadialSegments = 4 }, glow, new Vector3(0, 0.5f, 0));
                Part(node, new CylinderMesh { TopRadius = 0.25f, BottomRadius = 0f, Height = 0.35f, RadialSegments = 4 }, glow, new Vector3(0, 0.1f, 0));
                break;
        }
        if (o.Health != null)
            Part(node, new CylinderMesh { TopRadius = 0.36f, BottomRadius = 0.44f, Height = 0.9f, RadialSegments = 6 }, Mats.Solid(fcol, 0.5f), new Vector3(0, 0.5f, 0), new Vector3(0, 30, 0));
    }

    private static MeshInstance3D Part(Node3D parent, Mesh mesh, Material mat, Vector3 pos, Vector3? rot = null)
    {
        var m = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, Position = pos, RotationDegrees = rot ?? Vector3.Zero };
        parent.AddChild(m);
        return m;
    }

    public override void _Process(double delta)
    {
        foreach (var s in _spin) s.RotateY((float)delta * 0.8f);
    }

    /// <summary>
    /// The routes of the scenario on the overlay: the main line in the route colour with direction arrows, a ring at
    /// the start, a diamond at the end. The selected route is wider and brighter.
    /// </summary>
    public static void DrawRoutes(OverlayLayer o, ScenarioDef sc, string selected = "", float alpha = 0.85f)
    {
        foreach (var r in sc.Routes)
        {
            var path = r.MainPath();
            if (path.Count == 0) continue;
            bool sel = r.Id == selected;
            var col = Ui.Parse(r.Color, new Color(1f, 0.6f, 0.25f));
            col.A = sel ? 1f : alpha * (selected == "" ? 1f : 0.55f);
            DrawLine(o, path, col, sel ? 0.13f : 0.08f);
            o.RingHex(path[0], col, 0.07f, 0.6f);
            o.Dot(path[^1], col, sel ? 0.2f : 0.15f);
        }
    }

    public static void DrawLine(OverlayLayer o, IReadOnlyList<HexCoord> path, Color col, float width)
    {
        for (int i = 1; i < path.Count; i++)
        {
            if (HexCoord.Distance(path[i - 1], path[i]) != 1) continue;
            o.Line(path[i - 1], path[i], col, width);
            if (i % 3 == 0 && path[i - 1].DirectionTo(path[i]) is { } d) o.Arrow(path[i - 1], d, col, 0.55f);
        }
    }
}
