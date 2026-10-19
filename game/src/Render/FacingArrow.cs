using System;
using Godot;
using HexTactics.Core.Hex;

namespace HexTactics.Game.Render;

public enum ArrowState
{
    Normal,
    Hover,
    Grabbed,
    /// <summary>Preview of a direction (not yet confirmed).</summary>
    Preview,
    /// <summary>The unit cannot turn (stunned, rooted in place…).</summary>
    Invalid,
    /// <summary>Facing after arriving at a route node.</summary>
    Arrival,
    /// <summary>Common facing of a group order.</summary>
    Group,
}

/// <summary>
/// The interactive facing arrow (docs/DECISIONS_3.md §40): a thin body with a clear head and a dark
/// outline, lying just above the hex top, starting at the edge of the figure. Glows on hover, fades in,
/// keeps a readable size at any zoom. Purely visual: the battle screen hit-tests it and turns gestures
/// into commands.
/// </summary>
public partial class FacingArrow : Node3D
{
    private readonly MeshInstance3D _outline = new() { Name = "Outline", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    private readonly MeshInstance3D _body = new() { Name = "Body", CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    private readonly StandardMaterial3D _bodyMat;
    private readonly StandardMaterial3D _outlineMat;
    private float _fade;
    private float _yaw;

    /// <summary>Where the arrow starts (distance from the hex centre) and its length, before scaling.</summary>
    public const float Start = 0.34f;
    public const float BaseLength = 0.5f;

    public ArrowState State { get; set; }
    public float SizeFactor { get; set; } = 1f;
    public float AnimSpeed { get; set; } = 1f;
    /// <summary>Extra scale that compensates the camera distance (set by the battle screen).</summary>
    public float ZoomScale { get; set; } = 1f;
    public HexCoord Cell { get; private set; }
    public float Yaw => _yaw;
    /// <summary>World position of the hex top the arrow belongs to (the arrow starts at the figure's edge).</summary>
    public Vector3 Anchor { get; private set; }

    private Vector3 Dir => new(-Mathf.Sin(_yaw), 0, -Mathf.Cos(_yaw));
    /// <summary>Length scale: grows with the size setting and the zoom, but the arrow stays inside its own hex.</summary>
    private float Scale => Math.Min(SizeFactor * ZoomScale, (0.98f - Start) / BaseLength);
    /// <summary>Width scale: thicker when zoomed out, so the arrow stays readable.</summary>
    private float WidthScale => SizeFactor * ZoomScale;

    public FacingArrow()
    {
        _bodyMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            NoDepthTest = true,
            RenderPriority = 12,
            EmissionEnabled = true,
        };
        _outlineMat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            NoDepthTest = true,
            RenderPriority = 11,
            AlbedoColor = new Color(0.05f, 0.05f, 0.07f, 0.85f),
        };
        _body.Mesh = BuildMesh(0f);
        _body.MaterialOverride = _bodyMat;
        _outline.Mesh = BuildMesh(0.045f);
        _outline.MaterialOverride = _outlineMat;
        AddChild(_outline);
        AddChild(_body);
        Visible = false;
    }

    /// <summary>A flat arrow along −Z (yaw 0) from 0 to BaseLength, grown by «pad» for the outline.</summary>
    private static ArrayMesh BuildMesh(float pad)
    {
        float l0 = -pad, l1 = BaseLength + pad * 1.6f, neck = BaseLength * 0.58f;
        float body = 0.045f + pad, head = 0.15f + pad * 1.4f;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        void Tri(Vector3 a, Vector3 b, Vector3 c)
        {
            st.AddVertex(a);
            st.AddVertex(b);
            st.AddVertex(c);
        }
        // Body (rectangle) and head (triangle), both faces so the camera always sees them.
        var p0 = new Vector3(-body, 0, -l0);
        var p1 = new Vector3(body, 0, -l0);
        var p2 = new Vector3(body, 0, -neck);
        var p3 = new Vector3(-body, 0, -neck);
        Tri(p0, p1, p2);
        Tri(p0, p2, p3);
        Tri(p0, p2, p1);
        Tri(p0, p3, p2);
        var h0 = new Vector3(-head, 0, -neck);
        var h1 = new Vector3(head, 0, -neck);
        var tip = new Vector3(0, 0, -l1);
        Tri(h0, h1, tip);
        Tri(h0, tip, h1);
        return st.Commit();
    }

    /// <summary>Shows the arrow at a hex (world position of its top) pointing along a yaw.</summary>
    public void Show(HexCoord cell, Vector3 top, float yaw)
    {
        if (!Visible || cell != Cell) _fade = 0;
        Cell = cell;
        Anchor = top + new Vector3(0, 0.07f, 0);
        _yaw = yaw;
        Position = Anchor + Dir * Start;
        Visible = true;
    }

    public void SetYaw(float yaw) => _yaw = yaw;

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _fade = Math.Min(1f, _fade + (float)delta * 7f * AnimSpeed);
        float grow = (State is ArrowState.Hover or ArrowState.Grabbed ? 1.08f : 1f) * (0.75f + 0.25f * _fade);
        Position = Anchor + Dir * Start;
        Basis = new Basis(Vector3.Up, _yaw).Scaled(new Vector3(WidthScale * grow, 1, Scale * grow));
        float pulse = 0.5f + 0.5f * Mathf.Sin(Time.GetTicksMsec() / 1000f * 6f * AnimSpeed);
        Color c = State switch
        {
            ArrowState.Hover => new Color(1f, 0.97f, 0.72f),
            ArrowState.Grabbed => new Color(1f, 0.82f, 0.3f),
            ArrowState.Preview => new Color(1f, 0.85f, 0.4f, 0.8f),
            ArrowState.Invalid => new Color(1f, 0.32f, 0.25f),
            ArrowState.Arrival => new Color(0.55f, 0.85f, 1f, 0.8f),
            ArrowState.Group => new Color(0.5f, 1f, 0.7f),
            _ => new Color(1f, 0.92f, 0.55f),
        };
        c.A *= _fade;
        _bodyMat.AlbedoColor = c;
        float glow = State is ArrowState.Hover or ArrowState.Grabbed ? 0.9f + 0.6f * pulse : 0.25f;
        _bodyMat.Emission = new Color(c.R, c.G, c.B) * glow;
        _outlineMat.AlbedoColor = new Color(0.05f, 0.05f, 0.07f, 0.85f * _fade);
    }

    /// <summary>Screen-space hit test: distance from the mouse to the arrow segment (with a generous grab zone).</summary>
    public bool Hit(Camera3D cam, Vector2 mouse, float grabPx)
    {
        return HitDistance(cam, mouse) <= grabPx;
    }

    /// <summary>Screen distance from the mouse to the arrow (infinite on the figure itself near the hex centre).</summary>
    public float HitDistance(Camera3D cam, Vector2 mouse)
    {
        if (!Visible) return float.MaxValue;
        var c = cam.UnprojectPosition(Anchor);
        var a = cam.UnprojectPosition(Anchor + Dir * Start);
        var b = cam.UnprojectPosition(Anchor + Dir * (Start + BaseLength * Scale + 0.08f));
        // A click on the figure itself (near the hex centre) is not a grab of the arrow.
        if (mouse.DistanceTo(c) < a.DistanceTo(c) * 0.75f) return float.MaxValue;
        return DistToSegment(mouse, a, b);
    }

    /// <summary>A comfortable point to grab the arrow (world): the middle of its body.</summary>
    public Vector3 GrabPoint() => Anchor + Dir * (Start + BaseLength * 0.6f * Scale);

    public static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 < 1e-6f ? 0 : Math.Clamp((p - a).Dot(ab) / len2, 0, 1);
        return p.DistanceTo(a + ab * t);
    }

    /// <summary>Yaw that points from a world position towards another (on the table plane).</summary>
    public static float YawTowards(Vector3 from, Vector3 to)
    {
        var v = to - from;
        return Mathf.Atan2(-v.X, -v.Z);
    }

    /// <summary>The hex direction closest to a yaw.</summary>
    public static HexDirection Snap(float yaw)
    {
        var best = HexDirection.E;
        float bestD = float.MaxValue;
        for (int i = 0; i < 6; i++)
        {
            var d = (HexDirection)i;
            float diff = Math.Abs(Mathf.AngleDifference(yaw, Board.YawOf(d)));
            if (diff < bestD)
            {
                bestD = diff;
                best = d;
            }
        }
        return best;
    }
}
