using System;
using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>A problem of a profile of a scale.</summary>
public sealed record ScaleProblem(bool Error, string Path, string Message);

/// <summary>What a value of a scale gives: the bands it is in, the modifiers of the curves.</summary>
public sealed record ScaleForecast(double Value, List<string> Bands, List<StatModDef> Mods);

/// <summary>
/// Profiles of scales in the editor (ТЗ ч. 6, 18.2–18.7; docs/DECISIONS_6.md §104): the check of the bounds, rules, bands and
/// curves; the forecast of a chosen value; the reachable range from the rules and the recovery; the contribution to the estimate
/// of power — told, not a second formula of the level.
/// </summary>
public static class ScaleProfiles
{
    private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    public static List<ScaleProblem> Check(ContentDatabase db, ScaleProfileDef p)
    {
        var list = new List<ScaleProblem>();
        foreach (var (name, v) in new[] { ("Min", p.Min), ("Max", p.Max), ("HardMin", p.HardMin), ("HardMax", p.HardMax), ("Start", p.Start), ("SpawnValue", p.SpawnValue), ("RecoverTarget", p.RecoverTarget) })
            if (!Finite(v)) list.Add(new(true, name, "значение не число или бесконечность"));
        if (p.Min >= p.Max) list.Add(new(true, "Min", $"минимум {p.Min:0.##} должен быть меньше максимума {p.Max:0.##}"));
        if (p.Mode == ScaleLimitMode.SoftCap && !(p.HardMin < p.Min && p.Max < p.HardMax))
            list.Add(new(true, "HardMin", $"мягкие границы: нужно жёсткий мин. < мин. < макс. < жёсткий макс. ({p.HardMin:0.##} < {p.Min:0.##} < {p.Max:0.##} < {p.HardMax:0.##})"));
        if (p.Mode == ScaleLimitMode.Overflow && !(p.HardMin <= p.Min && p.Max <= p.HardMax && p.HardMin < p.HardMax))
            list.Add(new(true, "HardMin", "переход границ: жёсткие пределы должны охватывать мин. и макс."));
        foreach (var (name, v) in new[] { ("Start", p.Start), ("SpawnValue", p.SpawnValue), ("RecoverTarget", p.RecoverTarget), ("Base", p.Base) })
            if (Finite(v) && (v < p.Lowest || v > p.Highest))
                list.Add(new(false, name, $"{v:0.##} вне границ [{p.Lowest:0.##}; {p.Highest:0.##}] — будет ограничено"));
        if (p.Mode == ScaleLimitMode.SoftCap && p.SoftCurve.Any(c => c.X < 0 || c.X > 1 || c.Y < 0 || c.Y > 1))
            list.Add(new(false, "SoftCurve", "кривая мягких границ: X и Y — от 0 до 1"));
        for (int i = 0; i < p.Rules.Count; i++)
        {
            var r = p.Rules[i];
            if (!Finite(r.Amount)) list.Add(new(true, $"Rules[{i}]", "изменение не число"));
            if (r.Event is ScaleEvent.Timer or ScaleEvent.Near && r.Interval <= 0) list.Add(new(true, $"Rules[{i}]", $"«{r}»: нужен интервал больше 0"));
            if (r.Event == ScaleEvent.Near && r.Filter == "") list.Add(new(false, $"Rules[{i}]", $"«{r}»: «рядом» без фильтра — рядом с любым юнитом"));
        }
        for (int i = 0; i < p.Bands.Count; i++)
        {
            var b = p.Bands[i];
            if (b.From > b.To) list.Add(new(true, $"Bands[{i}]", $"«{b}»: «от» больше «до»"));
            if (b.To < p.Lowest || b.From > p.Highest) list.Add(new(false, $"Bands[{i}]", $"«{b}»: вне границ шкалы — не наступит"));
            if (db.Status(b.Status) is { } sd && b.Duration <= 0 && sd.Lasts == StatusLasts.Timed)
                list.Add(new(false, $"Bands[{i}]", $"«{b}»: «пока в диапазоне», но состояние «{sd.DisplayName}» само кончается через {sd.Duration:0.#} с — нужна длительность «до конца боя» или «длительность, с» диапазона"));
        }
        for (int i = 0; i < p.Curves.Count; i++)
        {
            var c = p.Curves[i];
            if (c.Points.Count == 0) list.Add(new(true, $"Curves[{i}]", $"«{c}»: нет точек"));
            if (c.Points.GroupBy(x => x.X).Any(g => g.Count() > 1)) list.Add(new(false, $"Curves[{i}]", $"«{c}»: повторяется X точки"));
            if (c.Points.Any(x => !Finite(x.X) || !Finite(x.Y))) list.Add(new(true, $"Curves[{i}]", $"«{c}»: точка не число"));
        }
        return list;
    }

    /// <summary>The bands a value is in and the modifiers of the curves at it (the editor's forecast, ТЗ 18.6).</summary>
    public static ScaleForecast Forecast(ScaleProfileDef p, double value) => new(value,
        p.Bands.Where(b => b.Enabled && value >= b.From && value <= b.To).Select(b => b.ToString()).ToList(),
        Sim.ScaleSystem.CurveMods(p, value));

    /// <summary>
    /// The range the value can reach in a battle: the start and the target of recovery always; beyond them only if a rule moves
    /// it that way (or a script does — counted as both ways).
    /// </summary>
    public static (double Low, double High) Reachable(ScaleProfileDef p, double start)
    {
        double low = Math.Min(start, p.RecoverTarget), high = Math.Max(start, p.RecoverTarget);
        var rules = p.Rules.Where(r => r.Enabled).ToList();
        bool up = rules.Any(r => r.Amount > 0 || r.Event == ScaleEvent.Script), down = rules.Any(r => r.Amount < 0 || r.Event == ScaleEvent.Script);
        if (up) high = p.Highest;
        if (down) low = p.Lowest;
        return (low, high);
    }

    /// <summary>The contribution of the scale of a unit to its estimate (ТЗ 18.7): at the expected value, and what it cannot reach.</summary>
    public static List<string> PowerNotes(ContentDatabase db, UnitDef def)
    {
        var notes = new List<string>();
        if (def.Scale is not { } use || db.Get<ScaleProfileDef>(use.Profile) is not { } p) return notes;
        double start = use.OwnStart ? use.Start : p.Start;
        double expected = p.Recovery > 0 ? p.RecoverTarget : start;
        var f = Forecast(p, expected);
        string mods = f.Mods.Count == 0 ? "без модификаторов" : string.Join(", ", f.Mods.Select(m => $"{m.Stat} {(m.Add != 0 ? $"{m.Add:+0.##;-0.##}" : "")}{(m.Mul != 0 ? $"{m.Mul:+0%;-0%}" : "")}"));
        notes.Add($"шкала «{p.DisplayName}»: ожидаемо {expected:0.#} ({(p.Recovery > 0 ? "цель восстановления" : "начальное значение")}) — {mods}" +
                  (f.Bands.Count > 0 ? "; диапазоны: " + string.Join(", ", f.Bands) : "") + (use.Mode == ScaleUseMode.Immune ? " (иммунитет)" : ""));
        var (low, high) = use.Mode == ScaleUseMode.Immune ? (start, start) : Reachable(p, start);
        foreach (var b in p.Bands.Where(b => b.Enabled && (b.From > high || b.To < low)))
            notes.Add($"шкала «{p.DisplayName}»: диапазон «{b}» недостижим (значение от {low:0.#} до {high:0.#}) — его состояние не наступит");
        return notes;
    }
}
