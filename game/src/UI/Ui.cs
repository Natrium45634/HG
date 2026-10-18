using System;
using Godot;

namespace HexTactics.Game.UI;

/// <summary>Palette and small factory helpers so screens can be built from code concisely.</summary>
public static class Ui
{
    public static readonly Color Bg = new(0.105f, 0.115f, 0.13f);
    public static readonly Color Panel = new(0.14f, 0.155f, 0.175f, 0.94f);
    public static readonly Color PanelLight = new(0.19f, 0.205f, 0.23f, 0.96f);
    public static readonly Color Border = new(0.32f, 0.35f, 0.40f);
    public static readonly Color Text = new(0.90f, 0.91f, 0.92f);
    public static readonly Color TextDim = new(0.62f, 0.65f, 0.70f);
    public static readonly Color Accent = new(0.95f, 0.76f, 0.30f);
    public static readonly Color Good = new(0.45f, 0.80f, 0.45f);
    public static readonly Color Bad = new(0.92f, 0.38f, 0.32f);
    public static readonly Color Warn = new(0.98f, 0.75f, 0.30f);
    public static readonly Color ApColor = new(0.35f, 0.62f, 0.98f);
    public static readonly Color HpColor = new(0.40f, 0.82f, 0.42f);
    public static readonly Color ReserveColor = new(0.98f, 0.82f, 0.40f);

    public static Color Parse(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            return Color.FromHtml(hex);
        }
        catch
        {
            return fallback;
        }
    }

    public static StyleBoxFlat Box(Color bg, Color? border = null, int radius = 6, int borderWidth = 1, int margin = 8)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border ?? Border,
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius,
            CornerRadiusBottomRight = radius,
            ContentMarginLeft = margin,
            ContentMarginRight = margin,
            ContentMarginTop = margin * 0.75f,
            ContentMarginBottom = margin * 0.75f,
        };
        sb.SetBorderWidthAll(borderWidth);
        return sb;
    }

    public static Theme BuildTheme()
    {
        var t = new Theme { DefaultFontSize = 15 };
        t.SetStylebox("panel", "PanelContainer", Box(Panel));
        t.SetStylebox("panel", "Panel", Box(Panel));
        var normal = Box(new Color(0.22f, 0.24f, 0.27f), new Color(0.34f, 0.37f, 0.42f), 5, 1, 8);
        var hover = Box(new Color(0.28f, 0.30f, 0.34f), Accent * new Color(1, 1, 1, 0.7f), 5, 1, 8);
        var pressed = Box(new Color(0.34f, 0.30f, 0.20f), Accent, 5, 1, 8);
        var disabled = Box(new Color(0.17f, 0.18f, 0.20f), new Color(0.25f, 0.26f, 0.28f), 5, 1, 8);
        var focus = Box(new Color(0, 0, 0, 0), Accent * new Color(1, 1, 1, 0.5f), 5, 1, 8);
        foreach (var type in new[] { "Button", "OptionButton", "MenuButton", "CheckButton" })
        {
            t.SetStylebox("normal", type, normal);
            t.SetStylebox("hover", type, hover);
            t.SetStylebox("pressed", type, pressed);
            t.SetStylebox("disabled", type, disabled);
            t.SetStylebox("focus", type, focus);
            t.SetColor("font_color", type, Text);
            t.SetColor("font_disabled_color", type, new Color(0.45f, 0.47f, 0.50f));
            t.SetColor("font_hover_color", type, Colors.White);
            t.SetColor("font_pressed_color", type, Accent);
        }
        t.SetColor("font_color", "Label", Text);
        var le = Box(new Color(0.10f, 0.11f, 0.12f), new Color(0.30f, 0.32f, 0.36f), 4, 1, 6);
        t.SetStylebox("normal", "LineEdit", le);
        t.SetStylebox("focus", "LineEdit", Box(new Color(0.10f, 0.11f, 0.12f), Accent, 4, 1, 6));
        t.SetStylebox("normal", "TextEdit", le);
        t.SetStylebox("focus", "TextEdit", Box(new Color(0.10f, 0.11f, 0.12f), Accent, 4, 1, 6));
        t.SetStylebox("panel", "PopupMenu", Box(PanelLight, Border, 6, 1, 6));
        t.SetStylebox("panel", "TooltipPanel", Box(new Color(0.08f, 0.09f, 0.10f, 0.97f), Accent * new Color(1, 1, 1, 0.6f), 4, 1, 8));
        t.SetColor("font_color", "TooltipLabel", Text);
        t.SetStylebox("tab_selected", "TabContainer", Box(PanelLight, Accent, 4, 1, 8));
        t.SetStylebox("tab_unselected", "TabContainer", Box(Panel, Border, 4, 1, 8));
        t.SetStylebox("tab_hovered", "TabContainer", Box(PanelLight, Border, 4, 1, 8));
        t.SetStylebox("panel", "TabContainer", Box(Panel, Border, 4, 1, 6));
        t.SetStylebox("panel", "ItemList", Box(new Color(0.10f, 0.11f, 0.12f), Border, 4, 1, 4));
        t.SetStylebox("panel", "Tree", Box(new Color(0.10f, 0.11f, 0.12f), Border, 4, 1, 4));
        t.SetColor("font_color", "ItemList", Text);
        t.SetColor("font_selected_color", "ItemList", Accent);
        t.SetStylebox("panel", "AcceptDialog", Box(PanelLight, Accent, 6, 1, 10));
        return t;
    }

    public static Label Label(string text, int size = 15, Color? color = null, bool wrap = false)
    {
        var l = new Label { Text = text };
        if (size != 15) l.AddThemeFontSizeOverride("font_size", size);
        if (color.HasValue) l.AddThemeColorOverride("font_color", color.Value);
        if (wrap)
        {
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            l.CustomMinimumSize = new Vector2(40, 0);
        }
        return l;
    }

    public static Button Button(string text, Action? onPressed = null, string tooltip = "", int minWidth = 0)
    {
        var b = new Button { Text = text, TooltipText = tooltip, FocusMode = Control.FocusModeEnum.None };
        if (minWidth > 0) b.CustomMinimumSize = new Vector2(minWidth, 0);
        if (onPressed != null) b.Pressed += onPressed;
        return b;
    }

    public static HBoxContainer HBox(int sep = 6, params Control[] children)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", sep);
        foreach (var c in children) h.AddChild(c);
        return h;
    }

    public static VBoxContainer VBox(int sep = 6, params Control[] children)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", sep);
        foreach (var c in children) v.AddChild(c);
        return v;
    }

    public static PanelContainer PanelBox(Control child, Color? bg = null, int margin = 10)
    {
        var p = new PanelContainer();
        p.AddThemeStyleboxOverride("panel", Box(bg ?? Panel, Border, 8, 1, margin));
        p.AddChild(child);
        return p;
    }

    public static Control Spacer(float w = 0, float h = 0, bool expandH = false, bool expandV = false)
    {
        var c = new Control { CustomMinimumSize = new Vector2(w, h), MouseFilter = Control.MouseFilterEnum.Ignore };
        if (expandH) c.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        if (expandV) c.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        return c;
    }

    public static HSeparator Sep() => new() { MouseFilter = Control.MouseFilterEnum.Ignore };

    public static T Expand<T>(this T c, bool h = true, bool v = false) where T : Control
    {
        if (h) c.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        if (v) c.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        return c;
    }

    public static T MinSize<T>(this T c, float w, float h = 0) where T : Control
    {
        c.CustomMinimumSize = new Vector2(w, h);
        return c;
    }

    public static string ColorTag(Color c) => "#" + c.ToHtml(false);

    public static string FormatTime(double seconds)
    {
        int s = (int)Math.Floor(seconds);
        return $"{s / 60:00}:{s % 60:00}.{(int)((seconds - s) * 10)}";
    }

    public static void ClearChildren(Node n)
    {
        foreach (var c in n.GetChildren())
        {
            n.RemoveChild(c);
            c.QueueFree();
        }
    }
}

/// <summary>Horizontal bar with an optional reserved segment (AP reservation) and a centred caption.</summary>
public partial class Bar : Control
{
    public double Value { get; set; }
    public double Max { get; set; } = 1;
    public double Reserved { get; set; }
    public Color FillColor { get; set; } = Ui.HpColor;
    public Color ReserveColor { get; set; } = Ui.ReserveColor;
    public string Caption { get; set; } = "";
    public bool ShowThresholds { get; set; }
    public double[] Thresholds { get; set; } = Array.Empty<double>();

    public Bar()
    {
        CustomMinimumSize = new Vector2(80, 16);
        MouseFilter = MouseFilterEnum.Pass;
    }

    public void Set(double value, double max, string caption, double reserved = 0)
    {
        Value = value;
        Max = max;
        Caption = caption;
        Reserved = reserved;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var r = new Rect2(Vector2.Zero, Size);
        DrawRect(r, new Color(0.06f, 0.065f, 0.075f));
        double frac = Max <= 0 ? 0 : Math.Clamp(Value / Max, 0, 1);
        DrawRect(new Rect2(1, 1, (float)((Size.X - 2) * frac), Size.Y - 2), FillColor);
        if (Reserved > 0 && Max > 0)
        {
            // Reserved part is drawn as a striped segment at the end of the current value.
            double resFrac = Math.Clamp(Reserved / Max, 0, frac);
            float x0 = (float)((Size.X - 2) * (frac - resFrac)) + 1;
            float w = (float)((Size.X - 2) * resFrac);
            DrawRect(new Rect2(x0, 1, w, Size.Y - 2), ReserveColor * new Color(1, 1, 1, 0.85f));
            for (float x = x0; x < x0 + w; x += 6)
                DrawLine(new Vector2(x, Size.Y - 2), new Vector2(Math.Min(x + 4, x0 + w), 2), new Color(0, 0, 0, 0.35f), 2);
            if (Reserved > Value)
            {
                // Over-reserved: red marker shows the deficit beyond current AP.
                double deficit = Math.Clamp((Reserved - Value) / Max, 0, 1);
                DrawRect(new Rect2(1, Size.Y - 4, (float)((Size.X - 2) * deficit), 3), Ui.Bad);
            }
        }
        if (ShowThresholds && Max > 0)
            foreach (var t in Thresholds)
            {
                float x = (float)(Size.X * Math.Clamp(t / Max, 0, 1));
                DrawLine(new Vector2(x, 0), new Vector2(x, Size.Y), new Color(1, 1, 1, 0.6f), 1);
            }
        DrawRect(r, new Color(0.35f, 0.37f, 0.40f), false, 1);
        if (!string.IsNullOrEmpty(Caption))
        {
            var font = GetThemeDefaultFont();
            int fs = Math.Max(10, (int)(Size.Y * 0.72f));
            var ts = font.GetStringSize(Caption, HorizontalAlignment.Left, -1, fs);
            var pos = new Vector2((Size.X - ts.X) / 2, (Size.Y + ts.Y * 0.62f) / 2);
            DrawString(font, pos + new Vector2(1, 1), Caption, HorizontalAlignment.Left, -1, fs, new Color(0, 0, 0, 0.8f));
            DrawString(font, pos, Caption, HorizontalAlignment.Left, -1, fs, Colors.White);
        }
    }
}
