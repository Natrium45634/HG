using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>Readable texts of skills as sequences (tooltips, the editor's series tab, docs/DECISIONS_3.md §45).</summary>
public static class SkillText
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Hits of the series: damage stages × repetitions.</summary>
    public static int TotalHits(SkillDef s) => s.Stages.Where(st => st.HasDamage).Sum(st => Math.Max(1, st.Repeat));

    /// <summary>Time from the release to the last stage, seconds.</summary>
    public static double Duration(SkillDef s) =>
        s.Stages.Count == 0 ? 0 : s.Stages.Max(st => st.Delay + (Math.Max(1, st.Repeat) - 1) * st.RepeatInterval);

    /// <summary>Is the skill a sequence worth describing hit by hit.</summary>
    public static bool IsSeries(SkillDef s) => TotalHits(s) > 1 || s.Stages.Count > 1 || s.Stages.Any(st =>
        st.DamageMultiplier != 1 || st.FlatBonus != 0 || st.Costs.Count > 0 || st.Condition != null || st.Move != StageMove.None || st.Repeat > 1);

    public static string Summary(SkillDef s)
    {
        int hits = TotalHits(s);
        var parts = new List<string>();
        if (hits > 1) parts.Add($"серия из {hits} {Plural(hits, "удара", "ударов", "ударов")} за {Duration(s).ToString("0.##", Ru)} с");
        if (hits > 1)
        {
            parts.Add(s.SeriesCrit == SeriesCritMode.PerHit ? "крит — для каждого удара" : "крит — один бросок на серию");
            parts.Add(s.Targeting != TargetingMode.Unit ? "по области каждого этапа"
                : s.RetargetOnDeath is RetargetRule.Nearest ? "цель погибла — следующие удары по ближайшей"
                : s.RetargetOnDeath is RetargetRule.ByRules ? "цель погибла — новая цель по правилам выбора"
                : s.StopOnTargetDeath ? "цель погибла — оставшиеся удары отменяются" : "удары продолжаются по клетке цели");
        }
        if (s.Stages.Any(st => st.Costs.Count > 0))
            parts.Add("ресурс кончился — " + s.OnResourceOut switch
            {
                ResourceOutPolicy.FinishStage => "завершить текущий этап",
                ResourceOutPolicy.SafeStop => "безопасно прекратить",
                ResourceOutPolicy.Debt => "в долг",
                _ => "отменить оставшиеся",
            });
        if (s.HoldDuringSeries && hits > 1) parts.Add("исполнитель занят до конца серии");
        if (s.FreeIf != null) parts.Add("бесплатно, если " + s.FreeIf.Describe());
        return parts.Count == 0 ? "" : "Серия: " + string.Join("; ", parts);
    }

    /// <summary>One line per stage: number, name, moment, hit parameters, condition, costs, movement.</summary>
    public static List<string> StageLines(SkillDef s)
    {
        var lines = new List<string>();
        int hit = 0;
        for (int i = 0; i < s.Stages.Count; i++)
        {
            var st = s.Stages[i];
            var bits = new List<string>();
            string when = st.Delay > 0 ? $"+{st.Delay.ToString("0.##", Ru)} с" : "сразу";
            if (st.Repeat > 1) when += $", ×{st.Repeat} через {st.RepeatInterval.ToString("0.##", Ru)} с";
            if (st.Move != StageMove.None) bits.Add((st.Move == StageMove.DashToTarget ? "рывок до " : "отступление на ") + $"{st.MoveDistance} гекс." + (st.MoveApPerHex > 0 ? $" ({st.MoveApPerHex.ToString("0.##", Ru)} ОД/гекс)" : ""));
            if (st.HasDamage)
            {
                string mult = $"{(st.DamageMultiplier * 100).ToString("0.#", Ru)} %";
                if (st.FlatBonus != 0) mult += $" {st.FlatBonus.ToString("+0.#;-0.#", Ru)}";
                bits.Add(mult + (string.IsNullOrEmpty(st.DamageType) ? "" : $" ({st.DamageType})"));
                if (st.HitMode == StageHitMode.AlwaysHit) bits.Add("всегда попадает");
                if (st.AccuracyBonus != 0) bits.Add($"точность {st.AccuracyBonus.ToString("+0%;-0%", Ru)}");
                if (st.Crit == CritRule.Never) bits.Add("без крита");
                if (st.CritChanceBonus != 0) bits.Add($"крит {st.CritChanceBonus.ToString("+0%;-0%", Ru)}");
                if (st.CritMultiplier > 0) bits.Add($"крит ×{st.CritMultiplier.ToString("0.##", Ru)}");
            }
            foreach (var e in st.Effects.Where(e => e.Type != EffectType.Damage)) bits.Add(e.Describe());
            if (st.StatusChanceMultiplier != 1 && st.Effects.Any(e => e.Type == EffectType.ApplyStatus)) bits.Add($"шанс состояний ×{st.StatusChanceMultiplier.ToString("0.##", Ru)}");
            if (st.Condition != null) bits.Add($"если {st.Condition.Describe()}" + (st.OnConditionFail == ConditionFailAction.StopSeries ? ", иначе серия прекращается" : ""));
            foreach (var c in st.Costs) bits.Add(Cost(c));
            if (!string.IsNullOrEmpty(st.Tag)) bits.Add($"тег «{st.Tag}»");
            if (st.Final) bits.Add("завершающий");
            string head;
            if (st.HasDamage)
            {
                int n = Math.Max(1, st.Repeat);
                head = n == 1 ? $"Удар {hit + 1}" : $"Удары {hit + 1}–{hit + n}";
                hit += n;
            }
            else head = $"Этап {i + 1}";
            if (!string.IsNullOrEmpty(st.Name)) head += $" «{st.Name}»";
            lines.Add($"{head} · {when}" + (bits.Count > 0 ? ": " + string.Join(", ", bits) : ""));
        }
        return lines;
    }

    public static string Cost(StageCostDef c)
    {
        string what = $"{(string.IsNullOrEmpty(c.Resource) ? "ОД" : c.Resource)} {c.Amount.ToString("0.#", Ru)}";
        return c.When switch
        {
            StageCostWhen.OnHit => what + " при попадании",
            StageCostWhen.OnMiss => what + " при промахе",
            StageCostWhen.RefundOnKill => "возврат " + what + " при убийстве",
            StageCostWhen.RefundOnMiss => "возврат " + what + " при промахе",
            _ => what + " за удар",
        };
    }

    private static string Plural(int n, string one, string few, string many)
    {
        int m10 = n % 10, m100 = n % 100;
        if (m10 == 1 && m100 != 11) return one;
        if (m10 is >= 2 and <= 4 && m100 is < 12 or > 14) return few;
        return many;
    }
}
