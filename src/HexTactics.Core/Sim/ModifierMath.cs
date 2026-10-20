using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;

namespace HexTactics.Core.Sim;

/// <summary>One contribution to a parameter: mode, value, source (for the breakdown).</summary>
public readonly record struct Contribution(ModifierMode Mode, double Value, int Priority, string Category, string Source, int Stacks = 1);

/// <summary>Result of summing the contributions of one parameter, with what acted and what did not.</summary>
public sealed class Combined
{
    public double FlatBefore;
    public double Percent;
    public double Multiplier = 1;
    public double FlatAfter;
    public List<(Contribution C, bool Applied, string Note)> Items { get; } = new();
}

/// <summary>
/// The single summing of modifiers (docs/DECISIONS_3.md §46): 2 flat additions before percents, 3 additive
/// percents, 4 multipliers, 5 the largest/smallest of their groups, 6 the priority source, 7 one per exclusive
/// category, 8 flat additions after percents; 9 the limits are the caller's (stat definition).
/// </summary>
public static class ModifierMath
{
    public static Combined Combine(IEnumerable<(ModifierMode Mode, double Value, int Priority, string Category, string Source, int Stacks)> items) =>
        Combine(items.Select(x => new Contribution(x.Mode, x.Value, x.Priority, x.Category, x.Source, x.Stacks)));

    public static Combined Combine(IEnumerable<Contribution> items)
    {
        var c = new Combined();
        var all = items.ToList();
        foreach (var x in all)
        {
            switch (x.Mode)
            {
                case ModifierMode.FlatBefore:
                    c.FlatBefore += x.Value;
                    c.Items.Add((x, true, ""));
                    break;
                case ModifierMode.Percent:
                    c.Percent += x.Value;
                    c.Items.Add((x, true, ""));
                    break;
                case ModifierMode.Multiplier:
                    double m = Math.Pow(x.Value, Math.Max(1, x.Stacks));
                    c.Multiplier *= m;
                    c.Items.Add((x, true, ""));
                    break;
                case ModifierMode.FlatAfter:
                    c.FlatAfter += x.Value;
                    c.Items.Add((x, true, ""));
                    break;
            }
        }
        // 5. Largest / smallest of the group.
        var max = all.Where(x => x.Mode == ModifierMode.Max).ToList();
        if (max.Count > 0)
        {
            var best = max.OrderByDescending(x => x.Value).First();
            c.Percent += best.Value;
            foreach (var x in max) c.Items.Add((x, x.Equals(best), x.Equals(best) ? "наибольший" : "не действует: есть больший"));
        }
        var min = all.Where(x => x.Mode == ModifierMode.Min).ToList();
        if (min.Count > 0)
        {
            var best = min.OrderBy(x => x.Value).First();
            c.Percent += best.Value;
            foreach (var x in min) c.Items.Add((x, x.Equals(best), x.Equals(best) ? "наименьший" : "не действует: есть меньший"));
        }
        // 6. Priority source: only the highest priority (ties — the first).
        var pri = all.Where(x => x.Mode == ModifierMode.Priority).ToList();
        if (pri.Count > 0)
        {
            var top = pri.OrderByDescending(x => x.Priority).First();
            c.Percent += top.Value;
            foreach (var x in pri) c.Items.Add((x, x.Equals(top), x.Equals(top) ? $"приоритет {x.Priority}" : $"не действует: приоритет {x.Priority} < {top.Priority}"));
        }
        // 7. Exclusive categories: one per category — the largest by magnitude (ties — the first).
        foreach (var g in all.Where(x => x.Mode == ModifierMode.Exclusive).GroupBy(x => x.Category))
        {
            var one = g.OrderByDescending(x => Math.Abs(x.Value)).First();
            c.Percent += one.Value;
            foreach (var x in g) c.Items.Add((x, x.Equals(one), x.Equals(one) ? $"категория «{g.Key}»" : $"не действует: в категории «{g.Key}» есть сильнее"));
        }
        return c;
    }

    /// <summary>Applies the combined modifiers to the base by the stat's order of calculation.</summary>
    public static double Apply(double b, Combined c, StatCalcOrder order, IReadOnlyList<double>? diminishingPercents = null) => order switch
    {
        StatCalcOrder.PercentOfBase => b * (1 + c.Percent / 100.0) * c.Multiplier + c.FlatBefore + c.FlatAfter,
        StatCalcOrder.Diminishing => (b + c.FlatBefore) * (1 + Diminished(c, diminishingPercents)) * c.Multiplier + c.FlatAfter,
        StatCalcOrder.FlatOnly => b + c.FlatBefore + c.FlatAfter,
        _ => (b + c.FlatBefore) * (1 + c.Percent / 100.0) * c.Multiplier + c.FlatAfter,
    };

    /// <summary>Diminishing percents: bonuses as 1 − Π(1 − p), penalties added.</summary>
    private static double Diminished(Combined c, IReadOnlyList<double>? percents)
    {
        if (percents == null || percents.Count == 0) return c.Percent / 100.0;
        double keep = 1, neg = 0;
        foreach (var p in percents)
        {
            if (p >= 0) keep *= 1 - Math.Clamp(p / 100.0, 0, 1);
            else neg += p / 100.0;
        }
        return 1 - keep + neg;
    }

    public static double Round(double v, StatRounding r) => r switch
    {
        StatRounding.Round => Math.Round(v, MidpointRounding.AwayFromZero),
        StatRounding.Floor => Math.Floor(v + 1e-9),
        StatRounding.Ceiling => Math.Ceiling(v - 1e-9),
        _ => v,
    };
}
