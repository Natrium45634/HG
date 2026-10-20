using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Sim;
using HexTactics.Game.UI;

namespace HexTactics.Game.Battle;

/// <summary>One entry of the radial menu: a skill (or the stance) with its availability against the target.</summary>
public sealed record RadialItem(SkillDef? Skill, string Label, string Icon, bool Enabled, string Reason, string Tooltip, bool Approach, Texture2D? Image = null);

/// <summary>
/// Radial skill menu (docs/DECISIONS_3.md §42): the defensive skill in a hexagonal button in the centre,
/// the first ring from 12 o'clock clockwise (weak → strong), the second ring for the rest, pages when even
/// that is full. Every icon shows its availability; the tooltip — name, description, costs, range,
/// cooldown and the reason it is unavailable.
/// </summary>
public partial class RadialMenu : Control
{
    public const int FirstCapacity = 8;
    public const int SecondCapacity = 12;
    private const float Slot = 60, SlotW = 92, Centre = 74;
    private float R1 = 118, R2 = 196;

    private RadialItem? _centre;
    private List<RadialItem> _first = new();
    private List<RadialItem> _second = new();
    private int _page;
    private Vector2 _anchor;
    private string _title = "";
    private Action<RadialItem>? _onPick;
    private readonly List<Button> _buttons = new();

    public bool IsOpen => Visible;
    public IReadOnlyList<RadialItem> First => _first;
    public IReadOnlyList<RadialItem> Second => _second;
    public RadialItem? CentreItem => _centre;
    public IReadOnlyList<Button> Buttons => _buttons;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;
    }

    public void Open(Vector2 at, string title, RadialItem? centre, List<RadialItem> first, List<RadialItem> second, Action<RadialItem> onPick)
    {
        _centre = centre;
        _first = first;
        _second = second;
        _title = title;
        _onPick = onPick;
        _page = 0;
        var size = GetViewportRect().Size;
        // Rings grow with the number of skills so that the icons never overlap.
        R1 = Math.Max(136, Math.Min(first.Count, FirstCapacity) * (SlotW + 30) / Mathf.Tau);
        R2 = Math.Max(R1 + 82, Math.Min(second.Count, SecondCapacity) * (SlotW * 0.9f + 30) / Mathf.Tau);
        float margin = (second.Count > 0 ? R2 : R1) + Slot;
        _anchor = new Vector2(Math.Clamp(at.X, margin, Math.Max(margin, size.X - margin)), Math.Clamp(at.Y, margin + 30, Math.Max(margin + 30, size.Y - margin)));
        Visible = true;
        Rebuild();
    }

    public void Close()
    {
        Visible = false;
        foreach (var b in _buttons) b.QueueFree();
        _buttons.Clear();
    }

    private int Pages => Math.Max(1, (int)Math.Ceiling(_second.Count / (double)SecondCapacity));

    private void Rebuild()
    {
        foreach (var b in _buttons) b.QueueFree();
        _buttons.Clear();
        if (_centre != null) AddSlot(_centre, _anchor, Centre, hex: true, width: Centre + 18);
        for (int i = 0; i < _first.Count && i < FirstCapacity; i++)
            AddSlot(_first[i], _anchor + Polar(i, Math.Max(_first.Count, 6), R1), Slot, false, SlotW);
        var page = _second.Skip(_page * SecondCapacity).Take(SecondCapacity).ToList();
        for (int i = 0; i < page.Count; i++)
            AddSlot(page[i], _anchor + Polar(i, Math.Max(page.Count, 8), R2), Slot * 0.9f, false, SlotW * 0.9f);
        if (Pages > 1)
        {
            var prev = PageButton("◀", -1);
            var next = PageButton("▶", 1);
            prev.Position = _anchor + new Vector2(-R2 - 70, R2 * 0.7f);
            next.Position = _anchor + new Vector2(R2 + 30, R2 * 0.7f);
        }
        QueueRedraw();
    }

    /// <summary>Slot position: from 12 o'clock, clockwise.</summary>
    private static Vector2 Polar(int i, int n, float r)
    {
        float a = -Mathf.Pi / 2 + Mathf.Tau * i / n;
        return new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
    }

    private Button PageButton(string text, int dir)
    {
        var b = new Button { Text = text, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(40, 32), TooltipText = $"Страница {_page + 1}/{Pages}" };
        b.Pressed += () =>
        {
            _page = ((_page + dir) % Pages + Pages) % Pages;
            Rebuild();
        };
        AddChild(b);
        _buttons.Add(b);
        return b;
    }

    private void AddSlot(RadialItem item, Vector2 centre, float size, bool hex, float width)
    {
        var b = new Button
        {
            Text = $"{item.Icon}\n{item.Label}",
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(width, size),
            Size = new Vector2(width, size),
            TooltipText = item.Tooltip + (item.Enabled ? (item.Approach ? "\n➜ сначала подойдёт к цели" : "") : "\n✕ Недоступно: " + item.Reason),
            Disabled = !item.Enabled,
            ClipText = true,
            Name = "Radial_" + (item.Skill?.Id ?? "stance"),
        };
        b.AddThemeFontSizeOverride("font_size", 12);
        if (item.Image != null)
        {
            // The skill's image icon (resource «иконка навыка») above the name.
            b.Icon = item.Image;
            b.ExpandIcon = true;
            b.Text = item.Label;
            b.IconAlignment = HorizontalAlignment.Center;
            b.VerticalIconAlignment = VerticalAlignment.Top;
            b.AddThemeConstantOverride("icon_max_width", (int)(size * 0.45f));
        }
        var bg = !item.Enabled ? new Color(0.17f, 0.17f, 0.19f, 0.95f) : hex ? new Color(0.20f, 0.30f, 0.42f, 0.97f) : new Color(0.22f, 0.24f, 0.28f, 0.97f);
        var border = !item.Enabled ? Ui.Bad : item.Approach ? Ui.Warn : hex ? new Color(0.55f, 0.8f, 1f) : Ui.Accent;
        int radius = hex ? 14 : (int)(size / 2);
        b.AddThemeStyleboxOverride("normal", Ui.Box(bg, border, radius, 2, 4));
        b.AddThemeStyleboxOverride("hover", Ui.Box(bg.Lightened(0.15f), border.Lightened(0.2f), radius, 3, 4));
        b.AddThemeStyleboxOverride("pressed", Ui.Box(bg.Lightened(0.25f), border, radius, 3, 4));
        b.AddThemeStyleboxOverride("disabled", Ui.Box(bg, border.Darkened(0.3f), radius, 1, 4));
        b.Position = centre - b.Size / 2;
        var it = item;
        b.Pressed += () =>
        {
            var cb = _onPick;
            Close();
            cb?.Invoke(it);
        };
        AddChild(b);
        _buttons.Add(b);
    }

    public override void _Draw()
    {
        if (!Visible) return;
        float outer = (_second.Count > 0 ? R2 : R1) + Slot * 0.8f;
        DrawCircle(_anchor, outer, new Color(0.05f, 0.06f, 0.08f, 0.6f));
        DrawArc(_anchor, R1, 0, Mathf.Tau, 64, new Color(1, 1, 1, 0.12f), 2);
        if (_second.Count > 0) DrawArc(_anchor, R2, 0, Mathf.Tau, 64, new Color(1, 1, 1, 0.10f), 2);
        var font = ThemeDB.FallbackFont;
        var t = _title + (Pages > 1 ? $" · стр. {_page + 1}/{Pages}" : "");
        var size = font.GetStringSize(t, HorizontalAlignment.Center, -1, 15);
        var pos = _anchor + new Vector2(-size.X / 2, -outer - 10);
        DrawRect(new Rect2(pos + new Vector2(-10, -size.Y + 2), size + new Vector2(20, 8)), new Color(0.08f, 0.09f, 0.11f, 0.92f));
        DrawString(font, pos, t, HorizontalAlignment.Left, -1, 15, Ui.Text);
    }

    public override void _GuiInput(InputEvent e)
    {
        // A click outside the items closes the menu (one step back).
        if (e is InputEventMouseButton { Pressed: true } mb && (mb.ButtonIndex == MouseButton.Left || mb.ButtonIndex == MouseButton.Right))
        {
            Close();
            AcceptEvent();
        }
    }
}
