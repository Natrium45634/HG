using System;
using System.Collections.Generic;
using Godot;
using HexTactics.Core.Hex;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// 2D hexagonal test field for the skill editor: grey cells, highlighted area cells, a test unit
/// with its facing arrow, six clickable direction arrows around the field.
/// Coordinates are relative to the area anchor (the centre).
/// </summary>
public partial class HexCanvas : Control
{
    public int Radius { get; set; } = 5;
    public HexDirection Orientation { get; set; } = HexDirection.E;
    /// <summary>Area cells (relative, already rotated) → fill colour.</summary>
    public Dictionary<HexCoord, Color> Cells { get; } = new();
    /// <summary>Cells with their own extra effects (drawn with a dot).</summary>
    public HashSet<HexCoord> Marked { get; } = new();
    public HashSet<HexCoord> Blocked { get; } = new();
    public HexCoord? SelectedCell { get; set; }
    public HexCoord CasterCell { get; set; } = HexCoord.Zero;
    public bool ShowCasterGhost { get; set; }
    public Color AccentColor { get; set; } = Ui.Accent;
    public Dictionary<HexCoord, string> Labels { get; } = new();

    public event Action<HexCoord, MouseButton>? CellClicked;
    public event Action<HexDirection>? DirectionClicked;

    private HexCoord? _hover;

    public HexCanvas()
    {
        CustomMinimumSize = new Vector2(480, 460);
        MouseFilter = MouseFilterEnum.Stop;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    private float HexSize
    {
        get
        {
            int n = 2 * Radius + 4; // +arrows ring and their labels
            float byW = Size.X / (Mathf.Sqrt(3) * n);
            float byH = Size.Y / (1.5f * n + 0.5f);
            return Math.Max(6, Math.Min(byW, byH));
        }
    }

    private Vector2 Origin => Size / 2;

    private Vector2 ToPixel(HexCoord c)
    {
        var (x, y) = HexLayout.ToPlane(c, HexSize);
        return Origin + new Vector2((float)x, (float)y);
    }

    private HexCoord FromPixel(Vector2 p)
    {
        var d = p - Origin;
        return HexLayout.FromPlane(d.X, d.Y, HexSize);
    }

    private Vector2[] Poly(HexCoord c, float scale = 0.94f)
    {
        var center = ToPixel(c);
        var pts = new Vector2[6];
        for (int i = 0; i < 6; i++)
        {
            float a = Mathf.DegToRad(60 * i - 30);
            pts[i] = center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * HexSize * scale;
        }
        return pts;
    }

    private Vector2 ArrowPos(HexDirection d) => ToPixel(d.Offset() * (Radius + 1));

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.09f, 0.1f, 0.11f));
        foreach (var c in HexCoord.Spiral(HexCoord.Zero, Radius))
        {
            var pts = Poly(c);
            Color fill = new(0.24f, 0.26f, 0.29f);
            if (Cells.TryGetValue(c, out var col)) fill = col;
            if (Blocked.Contains(c)) fill = new Color(0.35f, 0.33f, 0.33f);
            DrawColoredPolygon(pts, fill);
            var outline = new Vector2[7];
            Array.Copy(pts, outline, 6);
            outline[6] = pts[0];
            DrawPolyline(outline, new Color(0, 0, 0, 0.35f), 1.5f);
            if (_hover == c) DrawPolyline(outline, new Color(1, 1, 1, 0.45f), 2f);
            if (SelectedCell == c)
            {
                var sel = Poly(c, 0.86f);
                var so = new Vector2[7];
                Array.Copy(sel, so, 6);
                so[6] = sel[0];
                DrawPolyline(so, Colors.White, 3f);
            }
            if (Marked.Contains(c)) DrawCircle(ToPixel(c) + new Vector2(HexSize * 0.42f, -HexSize * 0.38f), HexSize * 0.13f, Colors.White);
            if (Labels.TryGetValue(c, out var text))
            {
                var f = GetThemeDefaultFont();
                int fs = (int)Math.Max(10, HexSize * 0.32f);
                var ts = f.GetStringSize(text, HorizontalAlignment.Left, -1, fs);
                DrawString(f, ToPixel(c) + new Vector2(-ts.X / 2, HexSize * 0.62f), text, HorizontalAlignment.Left, -1, fs, new Color(1, 1, 1, 0.85f));
            }
        }
        // Anchor marker.
        var a0 = ToPixel(HexCoord.Zero);
        DrawArc(a0, HexSize * 0.75f, 0, Mathf.Tau, 24, new Color(1, 1, 1, 0.25f), 1.5f);
        // Test unit.
        DrawUnit(CasterCell, ShowCasterGhost);
        // Direction arrows around the field.
        for (int i = 0; i < 6; i++)
        {
            var d = (HexDirection)i;
            var p = ArrowPos(d);
            var v = (ToPixel(d.Offset()) - Origin).Normalized();
            bool active = d == Orientation;
            var col = active ? AccentColor : new Color(0.7f, 0.72f, 0.76f, 0.6f);
            var side = new Vector2(-v.Y, v.X);
            float s = HexSize * 0.55f;
            DrawColoredPolygon(new[] { p + v * s, p - v * s * 0.6f + side * s * 0.8f, p - v * s * 0.6f - side * s * 0.8f }, col);
            var f = GetThemeDefaultFont();
            int fs = (int)Math.Max(11, HexSize * 0.32f);
            var label = d.RuShortName();
            var ts = f.GetStringSize(label, HorizontalAlignment.Left, -1, fs);
            var lp = p + v * s * 1.9f;
            DrawString(f, lp + new Vector2(-ts.X / 2, ts.Y * 0.3f), label, HorizontalAlignment.Left, -1, fs, col);
        }
    }

    private void DrawUnit(HexCoord c, bool ghost)
    {
        var p = ToPixel(c);
        float r = HexSize * 0.42f;
        var body = ghost ? new Color(0.35f, 0.55f, 0.9f, 0.45f) : new Color(0.35f, 0.55f, 0.9f);
        DrawCircle(p, r, body);
        DrawArc(p, r, 0, Mathf.Tau, 24, new Color(1, 1, 1, ghost ? 0.4f : 0.9f), 2);
        var v = (ToPixel(Orientation.Offset()) - Origin).Normalized();
        var side = new Vector2(-v.Y, v.X);
        DrawColoredPolygon(new[] { p + v * r * 1.55f, p + v * r * 0.75f + side * r * 0.45f, p + v * r * 0.75f - side * r * 0.45f }, new Color(1f, 0.92f, 0.55f, ghost ? 0.5f : 1));
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseMotion mm)
        {
            var h = FromPixel(mm.Position);
            var nh = h.Length <= Radius ? h : (HexCoord?)null;
            if (nh != _hover)
            {
                _hover = nh;
                QueueRedraw();
            }
        }
        if (e is InputEventMouseButton mb && mb.Pressed && (mb.ButtonIndex == MouseButton.Left || mb.ButtonIndex == MouseButton.Right))
        {
            for (int i = 0; i < 6; i++)
            {
                var d = (HexDirection)i;
                if (ArrowPos(d).DistanceTo(mb.Position) < HexSize * 0.7f)
                {
                    DirectionClicked?.Invoke(d);
                    AcceptEvent();
                    return;
                }
            }
            var h = FromPixel(mb.Position);
            if (h.Length <= Radius)
            {
                CellClicked?.Invoke(h, mb.ButtonIndex);
                AcceptEvent();
            }
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized || what == NotificationMouseExit)
        {
            if (what == NotificationMouseExit) _hover = null;
            QueueRedraw();
        }
    }
}
