using System;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// «Шкалы» (ТЗ ч. 6, 18.4–18.6; docs/DECISIONS_6.md §104): the fields of a profile, its problems, the chart — the bands over the
/// range and every enabled curve, with the marks −200, 0, +200 and the bounds of the profile — and the forecast of a value chosen
/// on a slider: the bands it is in and the modifiers it gives.
/// </summary>
public partial class ScalePanel : DefListPanel
{
    public ScalePanel(EditorContext ctx) : base(ctx, DefCategory.Scale, "Шкалы (боевой дух, санити…): границы, правила, диапазоны и кривые", null,
        () => new ScaleProfileDef { Name = "Новая шкала" })
    {
    }

    /// <summary>The chart and the forecast shown now (UI tests).</summary>
    public ScaleChart? Chart { get; private set; }
    public Label? ForecastLabel { get; private set; }
    public HSlider? ForecastSlider { get; private set; }

    protected override Control BuildDetail(Def d)
    {
        var p = (ScaleProfileDef)d;
        var box = Ui.VBox(6);
        var problems = Ui.VBox(2);
        problems.Name = "ScaleProblems";
        Chart = new ScaleChart(p) { Name = "ScaleChart", CustomMinimumSize = new Vector2(0, 220) };
        var slider = new HSlider { Name = "ScaleForecastSlider", Step = 1, CustomMinimumSize = new Vector2(260, 0) };
        slider.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        ForecastSlider = slider;
        var forecast = Ui.Label("", 13, Ui.Text, wrap: true);
        forecast.Name = "ScaleForecast";
        ForecastLabel = forecast;
        void Forecast()
        {
            if (!IsInstanceValid(forecast)) return;
            var f = ScaleProfiles.Forecast(p, slider.Value);
            Chart.Marker = slider.Value;
            forecast.Text = $"При {p.Short} = {slider.Value:0}: " +
                            (f.Bands.Count > 0 ? "диапазоны — " + string.Join(", ", f.Bands) : "вне диапазонов") + "; " +
                            (f.Mods.Count > 0 ? string.Join(", ", f.Mods.Select(m => $"{Ctx.Db.Stat(m.Stat)?.DisplayName ?? m.Stat} " +
                                                                                      (m.Add != 0 ? $"{m.Add:+0.###;-0.###}" : "") + (m.Mul != 0 ? $"{m.Mul:+0.#%;-0.#%}" : ""))) : "без модификаторов");
        }
        void Refresh()
        {
            if (!IsInstanceValid(problems)) return;
            Ui.ClearChildren(problems);
            var list = ScaleProfiles.Check(Ctx.Db, p);
            problems.AddChild(Ui.Label(list.Count == 0 ? "✓ Шкала без ошибок" : $"Ошибок: {list.Count(x => x.Error)}, предупреждений: {list.Count(x => !x.Error)}", 13,
                list.Count == 0 ? Ui.Good : list.Any(x => x.Error) ? Ui.Bad : Ui.Warn));
            foreach (var x in list) problems.AddChild(Ui.Label((x.Error ? "⛔ " : "⚠ ") + x.Message, 12, x.Error ? Ui.Bad : Ui.Warn, wrap: true));
            var users = Ctx.Db.All<UnitDef>().Where(u => u.Scale?.Profile == p.Id).Select(u => u.DisplayName).ToList();
            problems.AddChild(Ui.Label(users.Count == 0 ? "Ни у одного юнита нет этой шкалы (поле «Шкала» на вкладке «ИИ» юнита)." : "У юнитов: " + string.Join(", ", users), 12, Ui.TextDim, wrap: true));
            slider.MinValue = Math.Min(p.Lowest, p.Highest);
            slider.MaxValue = Math.Max(p.Lowest, p.Highest);
            Chart.QueueRedraw();
            Forecast();
        }
        slider.ValueChanged += _ => Forecast();
        var grid = new PropertyGrid();
        grid.SetTarget(p, Ctx.Db);
        grid.Changed += () =>
        {
            OnEdited(p);
            Refresh();
        };
        box.AddChild(problems);
        box.AddChild(Chart);
        var row = Ui.HBox(6);
        row.AddChild(Ui.Label("Прогноз:", 13, Ui.TextDim));
        row.AddChild(slider);
        box.AddChild(row);
        box.AddChild(forecast);
        box.AddChild(grid);
        slider.Value = p.Start;
        Refresh();
        return box;
    }
}

/// <summary>The chart of a scale: bands as stripes, curves as lines (each on its own vertical scale), marks and the chosen value.</summary>
public partial class ScaleChart : Control
{
    private readonly ScaleProfileDef _p;
    private double _marker = double.NaN;

    public ScaleChart(ScaleProfileDef p)
    {
        _p = p;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    public double Marker
    {
        get => _marker;
        set
        {
            _marker = value;
            QueueRedraw();
        }
    }

    private static readonly Color[] Palette = { new(0.95f, 0.76f, 0.3f), new(0.45f, 0.8f, 1f), new(0.9f, 0.45f, 0.75f), new(0.5f, 0.9f, 0.5f) };

    public override void _Draw()
    {
        var r = new Rect2(Vector2.Zero, Size);
        DrawRect(r, new Color(0.07f, 0.075f, 0.09f));
        double lo = Math.Min(_p.Lowest, _p.Highest), hi = Math.Max(_p.Lowest, _p.Highest);
        if (hi - lo < 1e-9) return;
        int curves = _p.Curves.Count(c => c.Enabled && c.Points.Count > 0);
        float left = 8, right = Size.X - 8, top = 8 + 14 * Math.Max(1, curves), bottom = Size.Y - 22;
        float X(double v) => left + (float)((v - lo) / (hi - lo)) * (right - left);
        var font = GetThemeDefaultFont();
        // Bands.
        foreach (var b in _p.Bands.Where(b => b.Enabled))
        {
            float x0 = X(Math.Max(lo, b.From)), x1 = X(Math.Min(hi, b.To));
            if (x1 <= x0) continue;
            DrawRect(new Rect2(x0, top, x1 - x0, bottom - top), new Color(0.9f, 0.4f, 0.3f, 0.12f));
            DrawString(font, new Vector2(x0 + 3, bottom - 4), b.Name != "" ? b.Name : b.Status, HorizontalAlignment.Left, Math.Max(10, x1 - x0 - 4), 11, new Color(1, 0.7f, 0.6f));
        }
        // Marks: −200, 0, +200 and the bounds of the profile.
        // One label per value: «−200 мин», «0 старт»…
        var marks = new[] { (-200.0, ""), (0.0, ""), (200.0, ""), (_p.Min, "мин"), (_p.Max, "макс"), (_p.Start, "старт") }
            .Where(m => m.Item1 >= lo && m.Item1 <= hi).GroupBy(m => m.Item1)
            .Select(g => (V: g.Key, Label: $"{g.Key:+0;−0;0}" + string.Concat(g.Where(m => m.Item2 != "").Select(m => " " + m.Item2)), Bound: g.Any(m => m.Item2 is "мин" or "макс")));
        foreach (var (v, label, bound) in marks)
        {
            float x = X(v);
            DrawLine(new Vector2(x, top), new Vector2(x, bottom), new Color(1, 1, 1, bound ? 0.45f : 0.18f), 1);
            var w = font.GetStringSize(label, HorizontalAlignment.Left, -1, 10).X;
            DrawString(font, new Vector2(Math.Clamp(x - w / 2, 2, Size.X - w - 2), Size.Y - 6), label, HorizontalAlignment.Left, -1, 10, Ui.TextDim);
        }
        // Curves: each scaled to its own range of results.
        int ci = 0;
        foreach (var c in _p.Curves.Where(c => c.Enabled && c.Points.Count > 0))
        {
            var col = Palette[ci++ % Palette.Length];
            double ymin = Math.Min(0, c.Points.Min(pt => pt.Y)), ymax = Math.Max(0, c.Points.Max(pt => pt.Y));
            if (ymax - ymin < 1e-9) ymax = ymin + 1;
            float Y(double y) => bottom - (float)((y - ymin) / (ymax - ymin)) * (bottom - top);
            const int Steps = 80;
            var pts = new Vector2[Steps + 1];
            for (int i = 0; i <= Steps; i++)
            {
                double v = lo + (hi - lo) * i / Steps;
                pts[i] = new Vector2(X(v), Y(c.Eval(v)));
            }
            DrawPolyline(pts, col, 2);
            foreach (var pt in c.Points.Where(pt => pt.X >= lo && pt.X <= hi)) DrawCircle(new Vector2(X(pt.X), Y(pt.Y)), 3, col);
            DrawLine(new Vector2(left, Y(0)), new Vector2(right, Y(0)), new Color(col.R, col.G, col.B, 0.25f), 1);
            DrawString(font, new Vector2(left + 4, 6 + 14 * ci), $"{c.Name} ({c.Stat}, {EdUtil.EnumLabel(c.Op).ToLowerInvariant()}): {c.Eval(double.IsNaN(_marker) ? 0 : _marker):+0.###;-0.###}",
                HorizontalAlignment.Left, -1, 11, col);
        }
        if (!double.IsNaN(_marker) && _marker >= lo && _marker <= hi)
            DrawLine(new Vector2(X(_marker), top - 4), new Vector2(X(_marker), bottom + 4), Ui.Accent, 2);
        DrawRect(r, new Color(0.35f, 0.37f, 0.40f), false, 1);
    }
}
