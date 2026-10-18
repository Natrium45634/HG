using System;
using System.Collections.Generic;
using Godot;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;

namespace HexTactics.Game.Render;

/// <summary>
/// Immediate-mode overlay drawn on top of cell surfaces: fills, outlines, hatching, path lines,
/// dots and arrows. Rebuilt every frame by the screen that owns it (cheap: a few thousand tris).
/// Uses shapes as well as colours so information is never colour-only.
/// </summary>
public partial class OverlayLayer : MeshInstance3D
{
    private readonly ImmediateMesh _mesh = new();
    private readonly List<(Vector3 A, Vector3 B, Vector3 C, Color Col)> _tris = new();
    private readonly List<(Vector3 A, Vector3 B, Vector3 C, Color Col)> _topTris = new();
    private BattleMap? _map;
    private float _time;

    public OverlayLayer()
    {
        Mesh = _mesh;
        CastShadow = ShadowCastingSetting.Off;
    }

    public void Bind(BattleMap map) => _map = map;

    public override void _Process(double delta) => _time += (float)delta;

    public float Pulse(float speed = 3f) => 0.5f + 0.5f * Mathf.Sin(_time * speed);

    private float Y(HexCoord c, float lift)
    {
        var cell = _map?.Get(c);
        return (cell == null ? 0 : Math.Max(Board.SurfaceY(cell), Board.TopY(cell))) + lift;
    }

    public void Clear()
    {
        _tris.Clear();
        _topTris.Clear();
    }

    private void Add(Vector3 a, Vector3 b, Vector3 c, Color col, bool onTop)
    {
        (onTop ? _topTris : _tris).Add((a, b, c, col));
    }

    public void FillHex(HexCoord c, Color col, float scale = 0.9f, float lift = 0.03f, bool onTop = false)
    {
        float y = Y(c, lift);
        var center = Board.Center(c, y);
        for (int i = 0; i < 6; i++)
            Add(center, Board.Corner(c, i, y, scale), Board.Corner(c, (i + 1) % 6, y, scale), col, onTop);
    }

    public void RingHex(HexCoord c, Color col, float width = 0.07f, float scale = 0.95f, float lift = 0.035f, bool onTop = false)
    {
        float y = Y(c, lift);
        for (int i = 0; i < 6; i++)
        {
            int j = (i + 1) % 6;
            var o0 = Board.Corner(c, i, y, scale);
            var o1 = Board.Corner(c, j, y, scale);
            var i0 = Board.Corner(c, i, y, scale - width);
            var i1 = Board.Corner(c, j, y, scale - width);
            Add(o0, o1, i1, col, onTop);
            Add(o0, i1, i0, col, onTop);
        }
    }

    /// <summary>Dashed ring (telegraphed threat).</summary>
    public void DashedRing(HexCoord c, Color col, float width = 0.08f, float scale = 0.93f, float lift = 0.04f)
    {
        float y = Y(c, lift);
        for (int i = 0; i < 6; i++)
        {
            int j = (i + 1) % 6;
            var o0 = Board.Corner(c, i, y, scale);
            var o1 = Board.Corner(c, j, y, scale);
            var i0 = Board.Corner(c, i, y, scale - width);
            var i1 = Board.Corner(c, j, y, scale - width);
            for (int k = 0; k < 3; k++)
            {
                float t0 = k / 3f + 0.04f, t1 = (k + 0.6f) / 3f;
                Add(o0.Lerp(o1, t0), o0.Lerp(o1, t1), i0.Lerp(i1, t1), col, false);
                Add(o0.Lerp(o1, t0), i0.Lerp(i1, t1), i0.Lerp(i1, t0), col, false);
            }
        }
    }

    /// <summary>Diagonal stripes inside a hex (blocked or threatened cells).</summary>
    public void HatchHex(HexCoord c, Color col, float lift = 0.032f, int stripes = 5, float phase = 0)
    {
        float y = Y(c, lift);
        var center = Board.Center(c, y);
        var dir = new Vector3(0.7071f, 0, 0.7071f);
        var perp = new Vector3(-0.7071f, 0, 0.7071f);
        for (int s = 0; s < stripes; s++)
        {
            float off = (s - (stripes - 1) / 2f + phase % 1f) * 0.32f;
            float half = Mathf.Sqrt(Math.Max(0.01f, 0.72f - off * off)) * 0.95f;
            var p0 = center + perp * off - dir * half;
            var p1 = center + perp * off + dir * half;
            Strip(p0, p1, 0.06f, col, false);
        }
    }

    public void Strip(Vector3 a, Vector3 b, float width, Color col, bool onTop)
    {
        var d = b - a;
        d.Y = 0;
        if (d.LengthSquared() < 1e-8f) return;
        var side = new Vector3(-d.Z, 0, d.X).Normalized() * (width / 2);
        Add(a + side, b + side, b - side, col, onTop);
        Add(a + side, b - side, a - side, col, onTop);
    }

    public void Line(HexCoord a, HexCoord b, Color col, float width = 0.09f, float lift = 0.06f, bool onTop = true)
    {
        var pa = Board.Center(a, Y(a, lift));
        var pb = Board.Center(b, Y(b, lift));
        Strip(pa, pb, width, col, onTop);
    }

    public void Dot(HexCoord c, Color col, float radius = 0.12f, float lift = 0.07f, bool onTop = true)
    {
        var p = Board.Center(c, Y(c, lift));
        Disc(p, radius, col, onTop);
    }

    public void Disc(Vector3 p, float radius, Color col, bool onTop = true, int seg = 12)
    {
        for (int i = 0; i < seg; i++)
        {
            float a0 = Mathf.Tau * i / seg, a1 = Mathf.Tau * (i + 1) / seg;
            Add(p, p + new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * radius, p + new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * radius, col, onTop);
        }
    }

    public void Circle(Vector3 p, float radius, float width, Color col, bool onTop = true, int seg = 32, float fraction = 1f)
    {
        int n = Math.Max(2, (int)(seg * fraction));
        for (int i = 0; i < n; i++)
        {
            float a0 = -Mathf.Pi / 2 + Mathf.Tau * fraction * i / n, a1 = -Mathf.Pi / 2 + Mathf.Tau * fraction * (i + 1) / n;
            var o0 = p + new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * radius;
            var o1 = p + new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * radius;
            var i0 = p + new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * (radius - width);
            var i1 = p + new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * (radius - width);
            Add(o0, o1, i1, col, onTop);
            Add(o0, i1, i0, col, onTop);
        }
    }

    /// <summary>Flat arrow from a cell towards a direction (facing previews).</summary>
    public void Arrow(HexCoord c, HexDirection d, Color col, float length = 0.8f, float lift = 0.08f, bool onTop = true)
    {
        var p = Board.Center(c, Y(c, lift));
        var v = Board.DirVector(d);
        ArrowAt(p, v, col, length, onTop);
    }

    public void ArrowAt(Vector3 p, Vector3 v, Color col, float length, bool onTop = true)
    {
        var side = new Vector3(-v.Z, 0, v.X);
        var tip = p + v * length;
        var neck = p + v * (length * 0.55f);
        Strip(p + v * 0.15f, neck, 0.12f, col, onTop);
        Add(neck + side * 0.22f, tip, neck - side * 0.22f, col, onTop);
    }

    public void Commit()
    {
        _mesh.ClearSurfaces();
        if (_tris.Count > 0)
        {
            _mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, Mats.VertexColor(unshaded: true, transparent: true));
            foreach (var (a, b, c, col) in _tris)
            {
                _mesh.SurfaceSetColor(col);
                _mesh.SurfaceAddVertex(a);
                _mesh.SurfaceSetColor(col);
                _mesh.SurfaceAddVertex(b);
                _mesh.SurfaceSetColor(col);
                _mesh.SurfaceAddVertex(c);
            }
            _mesh.SurfaceEnd();
        }
        if (_topTris.Count > 0)
        {
            _mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles, Mats.VertexColor(unshaded: true, transparent: true, noDepth: true));
            foreach (var (a, b, c, col) in _topTris)
            {
                _mesh.SurfaceSetColor(col);
                _mesh.SurfaceAddVertex(a);
                _mesh.SurfaceSetColor(col);
                _mesh.SurfaceAddVertex(b);
                _mesh.SurfaceSetColor(col);
                _mesh.SurfaceAddVertex(c);
            }
            _mesh.SurfaceEnd();
        }
    }
}
