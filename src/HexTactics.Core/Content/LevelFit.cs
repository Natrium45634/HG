using System;
using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>What «Подогнать под уровень» may change (docs/DECISIONS_4.md §66, TZ §14.6).</summary>
public enum FitVariant
{
    [Ed("Безопасное увеличение базовых характеристик")] Stats,
    [Ed("Усиление существующих навыков")] Skills,
    [Ed("Комбинированное масштабирование")] Combined,
}

/// <summary>One changed parameter: what, from, to.</summary>
public sealed record FitChange(string Key, string Label, double From, double To, string Note = "");

public sealed class FitResult
{
    public FitVariant Variant { get; init; }
    /// <summary>The fitted copy (the source template is never changed).</summary>
    public UnitDef Unit { get; init; } = null!;
    public List<FitChange> Changes { get; } = new();
    public double TargetLevel { get; init; }
    public double TargetPower { get; init; }
    public double PowerBefore { get; init; }
    public double LevelBefore { get; init; }
    public double PowerAfter { get; set; }
    public double LevelAfter { get; set; }
    /// <summary>The common multiplier of the profile's parameters (proportions are kept).</summary>
    public double Factor { get; set; } = 1;
    public bool Reached { get; set; }
    public string Message { get; set; } = "";
    public string Profile { get; init; } = "";
    /// <summary>«Подгонка характеристик» or «Автосборка по бюджету» (ТЗ ч. 5, 21).</summary>
    public BuildMode Mode { get; init; } = BuildMode.ScaleStats;
    public ulong Seed { get; init; }

    public string Title => Mode == BuildMode.AutoBuild ? $"Автосборка по бюджету (seed {Seed})" : EdUtil.EnumLabel(Variant);

    public override string ToString() =>
        $"{Title}: уровень {LevelBefore:0.0} → {LevelAfter:0.0} (цель {TargetLevel:0.#})" +
        (Changes.Any(c => c.Key != "role") ? "; " + string.Join(", ", Changes.Where(c => c.Key != "role").Select(c => $"{c.Label} {c.From:0.##} → {c.To:0.##}")) : "") + (Message != "" ? " — " + Message : "");
}

/// <summary>
/// «Подогнать под уровень» (docs/DECISIONS_4.md §66): the source build is kept; the parameters of the scaling
/// profile are multiplied by one common factor (each by factor ^ weight — the proportions of the role stay), with
/// their limits, ranges and steps; the factor is found by bisection on the power model; a target out of reach is
/// reported with the nearest level and what to allow. The result is a copy — the author applies it as a new
/// version or a copy of the template.
/// </summary>
public static class LevelFit
{
    /// <summary>The profile of a template: its own, the one of its role, «равномерный», else a built-in one.</summary>
    public static ScalingProfileDef ProfileFor(ContentDatabase db, UnitDef def, PowerRole role = PowerRole.Auto)
    {
        if (def.ScalingProfile != "" && db.Get<ScalingProfileDef>(def.ScalingProfile) is { } own) return own;
        if (role == PowerRole.Auto) role = def.PowerRole != PowerRole.Auto ? def.PowerRole : PowerModel.Evaluate(db, def).Role;
        return db.All<ScalingProfileDef>().FirstOrDefault(p => p.Role == role)
               ?? db.Get<ScalingProfileDef>("scale_uniform")
               ?? Uniform;
    }

    /// <summary>The built-in «равномерное усиление»: health, attack, magic, armor.</summary>
    public static ScalingProfileDef Uniform { get; } = new()
    {
        Id = "__uniform", Name = "Равномерное усиление", Role = PowerRole.Universal,
        Params =
        {
            new ScaleParamDef { Stat = Stats.HpMax, Weight = 1, Step = 1 },
            new ScaleParamDef { Stat = Stats.Power, Weight = 1, Step = 0.5 },
            new ScaleParamDef { Stat = Stats.MagicPower, Weight = 1, Step = 0.5 },
            new ScaleParamDef { Stat = Stats.Armor, Weight = 0.5, Step = 0.5 },
            new ScaleParamDef { Target = ScaleTarget.SkillDamage, Weight = 0.6, Step = 1, MaxChange = 5 },
        },
    };

    private static IEnumerable<ScaleParamDef> ParamsOf(ScalingProfileDef profile, FitVariant v)
    {
        var list = profile.Params.Where(p => !p.Locked && p.Weight > 0 && v switch
        {
            FitVariant.Stats => p.Target is ScaleTarget.Stat or ScaleTarget.Resistances,
            FitVariant.Skills => p.Target == ScaleTarget.SkillDamage,
            _ => true,
        }).ToList();
        // «Усиление навыков» chosen explicitly works even if the profile does not list it.
        if (v == FitVariant.Skills && list.Count == 0 && !profile.Params.Any(p => p.Target == ScaleTarget.SkillDamage && p.Locked))
            list.Add(new ScaleParamDef { Target = ScaleTarget.SkillDamage, Weight = 1, Step = 1, MaxChange = 10 });
        return list;
    }

    private static double StatOf(ContentDatabase db, UnitDef d, string id) => d.Stats.TryGetValue(id, out var v) ? v : db.Stat(id)?.Default ?? 0;

    private static double AnyBonus(UnitDef d) => d.Offense.Bonuses.Where(b => b.Match == "*").Sum(b => b.Percent);

    private static string Label(ContentDatabase db, ScaleParamDef p) => p.Target switch
    {
        ScaleTarget.Stat => db.Stat(p.Stat)?.DisplayName ?? p.Stat,
        ScaleTarget.SkillDamage => "сила навыков, %",
        _ => "сопротивления",
    };

    /// <summary>The copy of the template with the parameters at a factor (limits kept; steps when rounding).</summary>
    private static UnitDef At(ContentDatabase db, UnitDef src, List<ScaleParamDef> ps, double f, bool round, List<FitChange>? changes, HashSet<string>? capped, PowerRole role)
    {
        var d = ContentJson.Clone(src);
        // The role stays the one of the source (an «авто» role is pinned): scaling never turns an assassin into a tank.
        d.PowerRole = role;
        foreach (var p in ps)
        {
            double m = Math.Pow(f, p.Weight);
            switch (p.Target)
            {
                case ScaleTarget.Stat:
                {
                    double from = StatOf(db, src, p.Stat);
                    if (from <= 0) break; // nothing to scale: a missing stat is not invented
                    double to = Limit(from, from * m, p, capped);
                    if (round && p.Step > 0) to = Math.Max(p.Step, Math.Round(to / p.Step, MidpointRounding.AwayFromZero) * p.Step);
                    d.Stats[p.Stat] = to;
                    if (Math.Abs(to - from) > 1e-9) changes?.Add(new FitChange(p.Key, Label(db, p), from, to));
                    break;
                }
                case ScaleTarget.SkillDamage:
                {
                    double from = AnyBonus(src);
                    double factor = (1 + from / 100) * Math.Min(m, p.MaxChange > 0 ? p.MaxChange : double.MaxValue);
                    if (p.MaxChange > 0 && m > p.MaxChange) capped?.Add(p.Key);
                    double to = (factor - 1) * 100;
                    if (p.Max > 0 && to > p.Max) { to = p.Max; capped?.Add(p.Key); }
                    if (round && p.Step > 0) to = Math.Round(to / p.Step, MidpointRounding.AwayFromZero) * p.Step;
                    d.Offense.Bonuses.RemoveAll(b => b.Match == "*");
                    if (Math.Abs(to) > 1e-9) d.Offense.Bonuses.Add(new DamageModDef { Match = "*", Percent = to });
                    if (Math.Abs(to - from) > 1e-9) changes?.Add(new FitChange(p.Key, Label(db, p), from, to, "бонус наносимого урона шаблона; навыки не меняются"));
                    break;
                }
                case ScaleTarget.Resistances:
                    for (int i = 0; i < d.Defense.Modifiers.Count; i++)
                    {
                        var mod = d.Defense.Modifiers[i];
                        if (mod.Percent >= 0) continue;
                        double from = mod.Percent;
                        double to = -Math.Min(90, Math.Abs(from) * m);
                        if (Math.Abs(from) * m > 90) capped?.Add(p.Key);
                        if (round && p.Step > 0) to = Math.Round(to / p.Step, MidpointRounding.AwayFromZero) * p.Step;
                        d.Defense.Modifiers[i] = new DamageModDef { Match = mod.Match, Percent = to };
                        if (Math.Abs(to - from) > 1e-9) changes?.Add(new FitChange(p.Key + ":" + mod.Match, $"сопротивление «{mod.Match}», %", from, to));
                    }
                    break;
            }
        }
        return d;
    }

    private static double Limit(double from, double to, ScaleParamDef p, HashSet<string>? capped)
    {
        double lo = 0, hi = double.MaxValue;
        if (p.MaxChange > 0)
        {
            lo = from / p.MaxChange;
            hi = from * p.MaxChange;
        }
        if (p.Min > 0) lo = Math.Max(lo, p.Min);
        if (p.Max > 0) hi = Math.Min(hi, p.Max);
        if (to > hi + 1e-9) { capped?.Add(p.Key); return hi; }
        if (to < lo - 1e-9) { capped?.Add(p.Key); return lo; }
        return to;
    }

    public static FitResult Fit(ContentDatabase db, UnitDef def, double targetLevel, ScalingProfileDef? profile = null, FitVariant variant = FitVariant.Combined, PowerContext? ctx = null)
    {
        var before = PowerModel.Evaluate(db, def, ctx);
        profile ??= ProfileFor(db, def, before.Role);
        double target = PowerModel.PowerOf(db, targetLevel, before.Role, def.Rank);
        var ps = ParamsOf(profile, variant).ToList();
        var result = new FitResult
        {
            Variant = variant, TargetLevel = targetLevel, TargetPower = target, PowerBefore = before.Power, LevelBefore = before.Level, Profile = profile.DisplayName,
            Unit = ContentJson.Clone(def),
        };
        if (ps.Count == 0)
        {
            result.PowerAfter = before.Power;
            result.LevelAfter = before.Level;
            result.Message = $"в профиле «{profile.DisplayName}» нет разрешённых параметров для варианта «{EdUtil.EnumLabel(variant)}»";
            return result;
        }
        double P(double f) => PowerModel.Evaluate(db, At(db, def, ps, f, false, null, null, before.Role), ctx).Power;
        double lo = Math.Log(1 / 64.0), hi = Math.Log(64);
        double pLo = P(Math.Exp(lo)), pHi = P(Math.Exp(hi));
        double x;
        if (target >= pHi) x = hi;
        else if (target <= pLo) x = lo;
        else
        {
            for (int i = 0; i < 48 && hi - lo > 1e-5; i++)
            {
                double mid = (lo + hi) / 2;
                if (P(Math.Exp(mid)) < target) lo = mid;
                else hi = mid;
            }
            x = (lo + hi) / 2;
        }
        double f = Math.Exp(x);
        var capped = new HashSet<string>(StringComparer.Ordinal);
        var changes = new List<FitChange>();
        var unit = At(db, def, ps, f, true, changes, capped, before.Role);
        if (def.PowerRole == PowerRole.Auto) changes.Add(new FitChange("role", "роль в оценке силы", 0, 0, $"закреплена: {EdUtil.EnumLabel(before.Role)}"));
        var after = PowerModel.Evaluate(db, unit, ctx);
        var res = new FitResult
        {
            Variant = variant, TargetLevel = targetLevel, TargetPower = target, PowerBefore = before.Power, LevelBefore = before.Level, Profile = profile.DisplayName,
            Unit = unit, PowerAfter = after.Power, LevelAfter = after.Level, Factor = f,
            Reached = Math.Abs(after.Level - targetLevel) <= 0.5 || Math.Abs(after.Power - target) <= 0.03 * Math.Max(1, target),
        };
        res.Changes.AddRange(changes);
        Record(db, unit, BuildMode.ScaleStats, targetLevel, after.Level, 0, profile, changes.Where(c => c.Key != "role").Select(c => $"{c.Label} {c.From:0.##} → {c.To:0.##}"));
        if (!res.Reached)
        {
            bool up = targetLevel > after.Level;
            var allow = new List<string>();
            foreach (var p in ps.Where(p => capped.Contains(p.Key))) allow.Add($"{(up ? "увеличение" : "уменьшение")} «{Label(db, p)}» (предел)");
            foreach (var p in profile.Params.Where(p => p.Locked)) allow.Add($"изменение «{Label(db, p)}» (запрещено)");
            if (variant == FitVariant.Stats) allow.Add("изменение навыков");
            if (up) allow.Add("добавление способностей");
            res.Message = $"Целевой уровень {targetLevel:0.#} недостижим с текущими ограничениями. {(up ? "Максимальная" : "Минимальная")} оценка — {after.Level:0.0}." +
                          (allow.Count > 0 ? " Для продолжения разрешите: " + string.Join(", ", allow.Distinct()) + "." : "");
        }
        else res.Message = $"пропорции роли сохранены: все параметры профиля × {f:0.###} ^ вес";
        return res;
    }

    /// <summary>The three variants of TZ §14.6 for the author to choose from; with a pool of upgrades — the budget build too.</summary>
    public static List<FitResult> Variants(ContentDatabase db, UnitDef def, double targetLevel, ScalingProfileDef? profile = null, PowerContext? ctx = null, ulong seed = 1)
    {
        var list = Enum.GetValues<FitVariant>().Select(v => Fit(db, def, targetLevel, profile, v, ctx)).ToList();
        var prof = profile ?? ProfileFor(db, def);
        if (prof.Upgrades.Count > 0) list.Add(AutoBuild(db, def, targetLevel, prof, seed, ctx));
        return list;
    }

    /// <summary>The record of a build on the copy (ТЗ ч. 5, 21.5).</summary>
    private static void Record(ContentDatabase db, UnitDef unit, BuildMode mode, double target, double computed, ulong seed, ScalingProfileDef profile, IEnumerable<string> choices) =>
        unit.Build = new BuildRecordDef
        {
            Mode = mode, TargetLevel = target, ComputedLevel = Math.Round(computed, 3), Seed = seed, Profile = profile.Id, ProfileVersion = profile.Version,
            RatingHash = RatingHash(db), Choices = choices.ToList(),
        };

    /// <summary>The fingerprint of the rules of the power estimate (a change re-levels everything).</summary>
    public static string RatingHash(ContentDatabase db) =>
        HexTactics.Core.Sim.Simulation.StableHash(ContentJson.Serialize(db.Rules.Power) + (db.Unit(db.Rules.Power.ReferenceUnit) is { } r ? ContentJson.Serialize(r) : "")).ToString("X");

    /// <summary>
    /// «Автосборка по бюджету» (ТЗ ч. 5, 21.3, T19): upgrades of the profile's pool only, chosen by the weights of the
    /// archetype with the random stream of <paramref name="seed"/>, one by one until the power of the target level; an
    /// upgrade not available (its level, its limit, a missing or disabled skill, one already known, a locked stat) is
    /// never chosen. The same template, seed and profile version give the same build. The result is a copy.
    /// </summary>
    public static FitResult AutoBuild(ContentDatabase db, UnitDef def, double targetLevel, ScalingProfileDef profile, ulong seed, PowerContext? ctx = null)
    {
        var before = PowerModel.Evaluate(db, def, ctx);
        double target = PowerModel.PowerOf(db, targetLevel, before.Role, def.Rank);
        var unit = ContentJson.Clone(def);
        if (def.PowerRole == PowerRole.Auto) unit.PowerRole = before.Role; // the role of the estimate stays the same while it grows
        var rng = new HexTactics.Core.Sim.SimRng(seed);
        var pool = profile.Upgrades;
        var picks = new int[pool.Count];
        var locked = profile.Params.Where(p => p.Locked && p.Target == ScaleTarget.Stat).Select(p => p.Stat).ToHashSet(StringComparer.Ordinal);
        var chosen = new List<string>();
        var power = before;
        string why = "";
        bool Available(int i)
        {
            var o = pool[i];
            if (o.Weight <= 0 || o.MinLevel > targetLevel + 1e-9) return false;
            return o.Kind switch
            {
                UpgradeKind.Stat => o.Step != 0 && db.Stat(o.Stat) != null && !locked.Contains(o.Stat) && (o.MaxPicks <= 0 || picks[i] < o.MaxPicks),
                UpgradeKind.Skill => picks[i] == 0 && db.Skill(o.Skill) != null && !db.DisabledSkills.ContainsKey(o.Skill) && !unit.Skills.Contains(o.Skill) && unit.BasicAttack != o.Skill,
                _ => picks[i] == 0 && db.Get<PassiveDef>(o.Passive) != null && !unit.Passives.Contains(o.Passive),
            };
        }
        for (int step = 0; step < 400; step++)
        {
            if (power.Power >= target * 0.995) break;
            var avail = Enumerable.Range(0, pool.Count).Where(Available).ToList();
            if (avail.Count == 0)
            {
                why = "пул исчерпан";
                break;
            }
            double roll = rng.NextDouble() * avail.Sum(i => pool[i].Weight);
            int pick = avail[^1];
            foreach (var i in avail)
            {
                roll -= pool[i].Weight;
                if (roll < 0)
                {
                    pick = i;
                    break;
                }
            }
            var o = pool[pick];
            picks[pick]++;
            switch (o.Kind)
            {
                case UpgradeKind.Stat:
                    unit.Stats[o.Stat] = (unit.Stats.TryGetValue(o.Stat, out var v) ? v : db.StatDefault(o.Stat)) + o.Step;
                    break;
                case UpgradeKind.Skill:
                    unit.Skills.Add(o.Skill);
                    chosen.Add($"навык «{db.Skill(o.Skill)!.DisplayName}»");
                    break;
                default:
                    unit.Passives.Add(o.Passive);
                    chosen.Add($"пассив «{db.Get<PassiveDef>(o.Passive)!.DisplayName}»");
                    break;
            }
            power = PowerModel.Evaluate(db, unit, ctx);
        }
        var res = new FitResult
        {
            Variant = FitVariant.Combined, Mode = BuildMode.AutoBuild, Seed = seed, TargetLevel = targetLevel, TargetPower = target, PowerBefore = before.Power,
            LevelBefore = before.Level, Profile = profile.DisplayName, Unit = unit, PowerAfter = power.Power, LevelAfter = power.Level,
            Reached = power.Power >= target * 0.995 || Math.Abs(power.Level - targetLevel) <= 0.5,
        };
        for (int i = 0; i < pool.Count; i++)
        {
            if (picks[i] == 0) continue;
            var o = pool[i];
            if (o.Kind == UpgradeKind.Stat)
            {
                double from = def.Stats.TryGetValue(o.Stat, out var f) ? f : db.StatDefault(o.Stat);
                string label = db.Stat(o.Stat)?.DisplayName ?? o.Stat;
                res.Changes.Add(new FitChange("stat:" + o.Stat, label, from, unit.Stats[o.Stat], $"выбрано {picks[i]} раз(а)"));
                chosen.Insert(0, $"{label} {from:0.##} → {unit.Stats[o.Stat]:0.##}");
            }
            else res.Changes.Add(new FitChange(o.Kind == UpgradeKind.Skill ? "skill:" + o.Skill : "passive:" + o.Passive,
                o.Kind == UpgradeKind.Skill ? $"навык «{db.Skill(o.Skill)?.DisplayName}»" : $"пассив «{db.Get<PassiveDef>(o.Passive)?.DisplayName}»", 0, 1, "из пула"));
        }
        Record(db, unit, BuildMode.AutoBuild, targetLevel, power.Level, seed, profile, chosen);
        res.Message = res.Reached ? $"выборов из пула: {picks.Sum()}" :
            $"Целевой уровень {targetLevel:0.#} не достигнут: {why}{(why != "" ? "; " : "")}достигнуто {power.Level:0.0}. Добавьте улучшения в пул профиля «{profile.DisplayName}» или поднимите пределы.";
        return res;
    }

    /// <summary>What the panel says about the build record (ТЗ ч. 5, 21.5): the mismatch of target and computed is shown.</summary>
    public static string? BuildNote(ContentDatabase db, UnitDef def, double levelNow)
    {
        if (def.Build is not { } b || b.Mode == BuildMode.Manual) return null;
        string text = $"{EdUtil.EnumLabel(b.Mode)}: цель {b.TargetLevel:0.#}, при сборке {b.ComputedLevel:0.00}, сейчас {levelNow:0.00}" +
                      (b.Seed != 0 ? $", seed {b.Seed}" : "") + (b.Profile != "" ? $", профиль «{db.Get<ScalingProfileDef>(b.Profile)?.DisplayName ?? b.Profile}» v{b.ProfileVersion}" : "");
        var notes = new List<string>();
        if (Math.Abs(levelNow - b.TargetLevel) > 0.5) notes.Add($"уровень не совпадает с целевым ({levelNow:0.0} ≠ {b.TargetLevel:0.#})");
        if (db.Get<ScalingProfileDef>(b.Profile) is { } p && p.Version != b.ProfileVersion) notes.Add($"профиль изменён (v{b.ProfileVersion} → v{p.Version})");
        if (b.RatingHash != "" && b.RatingHash != RatingHash(db)) notes.Add("изменились правила оценки силы");
        return text + (notes.Count > 0 ? ". ⚠ " + string.Join("; ", notes) : "");
    }

    /// <summary>
    /// The level of an instance (docs/DECISIONS_4.md §65): multipliers of the stats of the template so it fits the
    /// level; the template is not changed (stats only — the instance's modifiers carry them).
    /// </summary>
    public static Dictionary<string, double> InstanceMultipliers(ContentDatabase db, UnitDef def, double level)
    {
        var fit = Fit(db, def, level, null, FitVariant.Stats);
        var mults = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var c in fit.Changes.Where(c => c.Key.StartsWith("stat:", StringComparison.Ordinal) && c.From > 0))
            mults[c.Key[5..]] = c.To / c.From;
        return mults;
    }
}
