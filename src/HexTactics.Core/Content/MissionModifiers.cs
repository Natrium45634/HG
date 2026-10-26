using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Sim;

namespace HexTactics.Core.Content;

/// <summary>One effective value of a variant: what it was, what it is, which modifiers made it (ТЗ ч. 6, 5.5, T18).</summary>
public sealed record ModifiedValue(string Field, string Base, string Effective, List<string> Sources);

/// <summary>A problem of a variant found before the battle (ТЗ ч. 6, 5.8).</summary>
public sealed record ModifierProblem(bool Error, string Message);

/// <summary>
/// Modifiers of a mission (ТЗ ч. 6, 5; docs/DECISIONS_6.md §99). A variant is a pure change of the content: the scenario is
/// copied into a copy of the database, the sets are applied in order — the scenario's own sets, then the variant's — and the
/// rest of the content stays shared. The rules of the mode, the deployment, the squad and the waves are written into the
/// copy; the changes of units go to <see cref="ScenarioDef.AppliedModifiers"/> for <see cref="MissionModifierSystem"/>.
/// No second system of damage, buffs or waves: a stat goes through the engine of modifiers, a status through the statuses.
/// </summary>
public static class MissionModifiers
{
    /// <summary>The sets in force: the scenario's own, then the variant's (missing ones skipped — the check tells).</summary>
    public static List<ModifierSetDef> SetsOf(ContentDatabase db, ScenarioDef sc, DifficultyVariantDef? variant)
    {
        var ids = sc.ModifierSets.Concat(variant?.ModifierSets ?? new List<string>());
        return ids.Select(id => db.Get<ModifierSetDef>(id)).Where(s => s != null).Select(s => s!).ToList();
    }

    /// <summary>Every modifier in force, in order, with the set it comes from; «Последний заменяет» drops the earlier ones of its field.</summary>
    public static List<(MissionModifierDef Mod, ModifierSetDef Set)> Active(ContentDatabase db, ScenarioDef sc, DifficultyVariantDef? variant)
    {
        var all = SetsOf(db, sc, variant).SelectMany(s => s.Modifiers.Select(m => (Mod: m, Set: s))).ToList();
        var result = new List<(MissionModifierDef, ModifierSetDef)>();
        for (int i = 0; i < all.Count; i++)
        {
            var later = all.Skip(i + 1).FirstOrDefault(x => x.Mod.Stacking == ModStacking.Replace && FieldOf(x.Mod) == FieldOf(all[i].Mod));
            if (later.Mod != null) continue; // replaced by a later one of the same field
            result.Add(all[i]);
        }
        return result;
    }

    /// <summary>The field a modifier touches (two modifiers of one field — a collision to show, ТЗ 5.5).</summary>
    public static string FieldOf(MissionModifierDef m) => m.Op switch
    {
        MissionModOp.Stat => $"stat:{m.Stat.Stat}@{m.Selector}",
        MissionModOp.Status => $"status:{m.Status}@{m.Selector}",
        MissionModOp.Rule => "rule:" + m.Rule,
        MissionModOp.Deployment => "deploy:" + m.DeployField,
        MissionModOp.Squad => "squad:" + m.SquadField,
        MissionModOp.Wave => "wave:" + m.WaveIndex,
        _ => m.Op.ToString(),
    };

    /// <summary>
    /// The scenario with the variant: a copy of the database (the definitions are shared, only the scenario is replaced)
    /// and the copy of the scenario changed. The scenario of <paramref name="db"/> is never touched.
    /// </summary>
    public static (ContentDatabase Db, ScenarioDef Scenario) Prepare(ContentDatabase db, ScenarioDef sc, DifficultyVariantDef? variant)
    {
        var copy = ContentJson.Clone(sc);
        var mods = Active(db, sc, variant);
        copy.AppliedVariant = variant?.Id ?? "";
        copy.AppliedModifiers.Clear();
        copy.AppliedNotes.Clear();
        if (mods.Count == 0)
        {
            var same = db.Clone();
            same.Table(DefCategory.Scenario).Replace(copy);
            return (same, copy);
        }
        var eff = EffectiveGameRules.Resolve(db, sc);
        foreach (var (m, _) in mods)
        {
            if (m.ShowInBattle) copy.AppliedNotes.Add(m.Title + (m.Description != "" ? " — " + m.Description : ""));
            switch (m.Op)
            {
                case MissionModOp.Stat:
                case MissionModOp.Status:
                    copy.AppliedModifiers.Add(ContentJson.Clone(m));
                    break;
                case MissionModOp.Rule:
                    copy.GameRulesOverride ??= new GameRulesOverrideDef();
                    if (m.Rule == ModRuleField.Blocking) copy.GameRulesOverride.Blocking = m.RuleOn;
                    else copy.GameRulesOverride.FriendlyFire = m.RuleOn;
                    break;
                case MissionModOp.Deployment:
                {
                    var d = copy.DeploymentOverride ??= ContentJson.Clone(eff.Deployment);
                    switch (m.DeployField)
                    {
                        case ModDeployField.StartingPoints: d.StartingPoints += m.Delta; break;
                        case ModDeployField.MaxPoints: d.MaxPoints += m.Delta; break;
                        case ModDeployField.MaxDeployed: d.MaxDeployed = (int)Math.Round(d.MaxDeployed + m.Delta); break;
                        case ModDeployField.GainAmount: d.GainAmount += m.Delta; break;
                        case ModDeployField.GainInterval: d.GainInterval += m.Delta; break;
                    }
                    break;
                }
                case MissionModOp.Squad when copy.Squad != null:
                    switch (m.SquadField)
                    {
                        case ModSquadField.MaxSize: copy.Squad.MaxSize = (int)Math.Round(copy.Squad.MaxSize + m.Delta); break;
                        case ModSquadField.ForbidTags: copy.Squad.ForbiddenTags.AddRange(m.Tags.Where(t => !copy.Squad.ForbiddenTags.Contains(t))); break;
                        case ModSquadField.AllowOnlyTags: copy.Squad.AllowedTags = m.Tags.ToList(); break;
                    }
                    break;
                case MissionModOp.Wave when m.WaveIndex >= 1 && m.WaveIndex <= copy.Waves.Count:
                    ApplyWave(copy.Waves[m.WaveIndex - 1], m);
                    break;
            }
        }
        var result = db.Clone();
        result.Table(DefCategory.Scenario).Replace(copy);
        return (result, copy);
    }

    private static int Count(int n, MissionModifierDef m) => Math.Max(0, (int)Math.Round(n * m.CountMul) + m.CountAdd);

    private static void ApplyWave(WaveDef w, MissionModifierDef m)
    {
        if (m.CountMul != 1 || m.CountAdd != 0)
        {
            foreach (var g in w.Groups) g.Count = Math.Max(1, Count(g.Count, m));
            foreach (var u in w.Units) u.Count = Math.Max(1, Count(u.Count, m));
        }
        if (m.ExtraUnit != "")
        {
            if (w.Groups.Count > 0)
            {
                // A copy of the first group: the same base, route and target — another unit.
                var g = ContentJson.Clone(w.Groups[0]);
                g.Id = "mod_" + (m.Id != "" ? m.Id : m.ExtraUnit);
                g.Name = "";
                g.Unit = m.ExtraUnit;
                g.Count = m.ExtraCount;
                g.Preset = "";
                g.StatMods.Clear();
                w.Groups.Add(g);
            }
            else w.Units.Add(new WaveUnitDef { Unit = m.ExtraUnit, Count = m.ExtraCount });
        }
        if (m.DelayAdd != 0)
        {
            if (w.Start == WaveStart.Time) w.Time = Math.Max(0, w.Time + m.DelayAdd);
            else w.Delay = Math.Max(0, w.Delay + m.DelayAdd);
        }
    }

    /// <summary>The threat of the enemies of the waves (the sum of the threat cost of every enemy) — for the cards of variants.</summary>
    public static double Threat(ContentDatabase db, ScenarioDef sc)
    {
        double sum = 0;
        foreach (var w in sc.Waves)
        {
            foreach (var g in w.Groups.Where(g => g.Enabled))
                if (db.Unit(g.Unit) is { } d)
                    sum += ThreatCost.Of(db, d) * g.Count * (1 + g.Repeats);
            foreach (var u in w.Units)
                if (db.Unit(u.Unit) is { } d)
                    sum += ThreatCost.Of(db, d) * u.Count;
        }
        return Math.Round(sum, 1);
    }

    /// <summary>Does a selector take a unit (a template, its faction, its wave, group and base)?</summary>
    public static bool Matches(ContentDatabase db, MissionSelectorDef s, UnitDef def, string faction, int wave, string group, string spawnBase)
    {
        if (s.Faction != "" && s.Faction != faction) return false;
        if (s.Units.Count > 0 && !s.Units.Any(x => SquadRules.Has(db, def, x))) return false;
        if (s.Wave > 0 && wave + 1 != s.Wave) return false;
        if (s.Group != "" && s.Group != group) return false;
        if (s.SpawnBase != "" && s.SpawnBase != spawnBase) return false;
        return true;
    }

    /// <summary>The templates of the scenario a selector may reach (its units and the units of its waves), with their faction.</summary>
    private static IEnumerable<(UnitDef Def, string Faction, int Wave, string Group, string Base)> Reachable(ContentDatabase db, ScenarioDef sc)
    {
        foreach (var u in sc.Units)
            if (db.Unit(u.Unit) is { } d)
                yield return (d, string.IsNullOrEmpty(u.Faction) ? d.Faction : u.Faction, -1, "", "");
        if (sc.Squad is { Enabled: true })
            foreach (var d in SquadRules.PoolOf(db, sc))
                yield return (d, "player", -1, "", "");
        for (int i = 0; i < sc.Waves.Count; i++)
        {
            var w = sc.Waves[i];
            foreach (var u in w.Units)
                if (db.Unit(u.Unit) is { } d)
                    yield return (d, w.Faction, i, "", "");
            foreach (var g in w.Groups)
                if (db.Unit(g.Unit) is { } d)
                    yield return (d, g.Faction != "" ? g.Faction : w.Faction, i, g.Id, g.SpawnBase);
        }
    }

    /// <summary>
    /// The effective values of a variant against the scenario as it is (ТЗ 5.5, 12.3, T18): the rules, the deployment, the
    /// squad, the waves; for the stats — per template the modifiers reach, through the engine of modifiers itself.
    /// </summary>
    public static List<ModifiedValue> Preview(ContentDatabase db, ScenarioDef sc, DifficultyVariantDef? variant)
    {
        var rows = new List<ModifiedValue>();
        var mods = Active(db, sc, variant);
        if (mods.Count == 0) return rows;
        var (vdb, vsc) = Prepare(db, sc, variant);
        string Src(Func<MissionModifierDef, bool> f) => string.Join("; ", mods.Where(x => f(x.Mod)).Select(x => $"«{x.Set.DisplayName}» → {x.Mod.Title}"));
        List<string> Srcs(Func<MissionModifierDef, bool> f) => mods.Where(x => f(x.Mod)).Select(x => $"«{x.Set.DisplayName}» → {x.Mod.Title}").ToList();

        var a = EffectiveGameRules.Resolve(db, sc);
        var b = EffectiveGameRules.Resolve(vdb, vsc);
        foreach (var va in a.Values)
            if (b.Values.FirstOrDefault(v => v.Field == va.Field) is { } vb && vb.Value != va.Value)
                rows.Add(new ModifiedValue(va.Label, va.Value, vb.Value, Srcs(m => m.Op is MissionModOp.Rule or MissionModOp.Deployment)));
        if (sc.Squad != null && vsc.Squad != null)
        {
            if (sc.Squad.MaxSize != vsc.Squad.MaxSize)
                rows.Add(new ModifiedValue("Размер отряда (не больше)", sc.Squad.MaxSize.ToString(), vsc.Squad.MaxSize.ToString(), Srcs(m => m.Op == MissionModOp.Squad && m.SquadField == ModSquadField.MaxSize)));
            if (!sc.Squad.ForbiddenTags.SequenceEqual(vsc.Squad.ForbiddenTags))
                rows.Add(new ModifiedValue("Запрещённые теги отряда", string.Join(", ", sc.Squad.ForbiddenTags), string.Join(", ", vsc.Squad.ForbiddenTags), Srcs(m => m.Op == MissionModOp.Squad && m.SquadField == ModSquadField.ForbidTags)));
            if (!sc.Squad.AllowedTags.SequenceEqual(vsc.Squad.AllowedTags))
                rows.Add(new ModifiedValue("Разрешённые теги отряда", string.Join(", ", sc.Squad.AllowedTags), string.Join(", ", vsc.Squad.AllowedTags), Srcs(m => m.Op == MissionModOp.Squad && m.SquadField == ModSquadField.AllowOnlyTags)));
        }
        for (int i = 0; i < Math.Min(sc.Waves.Count, vsc.Waves.Count); i++)
        {
            string W(WaveDef w) => $"{w.Groups.Sum(g => g.Count) + w.Units.Sum(u => u.Count)} врагов, групп {w.Groups.Count + w.Units.Count}" +
                                   (w.Start == WaveStart.Time ? $", с {w.Time:0.#} с" : $", задержка {w.Delay:0.#} с");
            string before = W(sc.Waves[i]), after = W(vsc.Waves[i]);
            int idx = i + 1;
            if (before != after) rows.Add(new ModifiedValue($"Волна {idx} «{sc.Waves[i].Name}»", before, after, Srcs(m => m.Op == MissionModOp.Wave && m.WaveIndex == idx)));
        }
        // Stats: per reachable template, through the same engine as the battle.
        var statMods = vsc.AppliedModifiers.Where(m => m.Op == MissionModOp.Stat && m.Stat.Stat != "").ToList();
        foreach (var (def, faction, wave, group, @base) in Reachable(db, sc).DistinctBy(x => (x.Def.Id, x.Faction, x.Wave, x.Group)))
        {
            var hit = statMods.Where(m => Matches(db, m.Selector, def, faction, wave, group, @base)).ToList();
            foreach (var stat in hit.Select(m => m.Stat.Stat).Distinct())
            {
                var block = new StatBlock(db, def);
                double before = block.Get(stat);
                foreach (var m in hit.Where(m => m.Stat.Stat == stat)) block.AddModifier(m.Stat, 1, m.Title);
                double after = block.Get(stat);
                string label = $"{db.Stat(stat)?.DisplayName ?? stat} — {def.DisplayName}" + (wave >= 0 ? $" (волна {wave + 1}{(group != "" ? ", " + group : "")})" : "");
                if (rows.All(r => r.Field != label))
                    rows.Add(new ModifiedValue(label, before.ToString("0.##"), after.ToString("0.##"), hit.Where(m => m.Stat.Stat == stat).Select(m => m.Title + (m.ActiveFrom > 0 || m.ActiveUntil > 0 ? " (по времени)" : "")).ToList()));
            }
        }
        foreach (var m in vsc.AppliedModifiers.Where(m => m.Op == MissionModOp.Status))
            rows.Add(new ModifiedValue($"Состояние «{db.Status(m.Status)?.DisplayName ?? m.Status}» — {m.Selector}", "нет", $"{m.StatusPoints:0.#} очк.", new List<string> { m.Title }));
        return rows;
    }

    /// <summary>
    /// What is wrong with a variant before the battle (ТЗ 5.8): incompatible sets and modifiers, references to waves,
    /// units, statuses and stats that are not there, a result that is not valid (a speed of zero, a limit below the
    /// required fighters); two modifiers of one field — a notice (the order says which acts).
    /// </summary>
    public static List<ModifierProblem> Check(ContentDatabase db, ScenarioDef sc, DifficultyVariantDef? variant)
    {
        var list = new List<ModifierProblem>();
        var ids = sc.ModifierSets.Concat(variant?.ModifierSets ?? new List<string>()).ToList();
        foreach (var id in ids.Where(id => db.Get<ModifierSetDef>(id) == null)) list.Add(new(true, $"набора модификаторов «{id}» нет"));
        var sets = SetsOf(db, sc, variant);
        for (int i = 0; i < sets.Count; i++)
            for (int j = 0; j < sets.Count; j++)
                if (i != j && sets[i].IncompatibleWith.Any(t => t == sets[j].Id || sets[j].Tags.Contains(t)))
                    list.Add(new(true, $"наборы «{sets[i].DisplayName}» и «{sets[j].DisplayName}» несовместимы"));
        var all = sets.SelectMany(s => s.Modifiers.Select(m => (Mod: m, Set: s))).ToList();
        for (int i = 0; i < all.Count; i++)
            for (int j = 0; j < all.Count; j++)
                if (i != j && all[i].Mod.IncompatibleTags.Any(t => t == all[j].Mod.Id || all[j].Mod.OwnTags.Contains(t)))
                    list.Add(new(true, $"«{all[i].Mod.Title}» несовместим с «{all[j].Mod.Title}» (набор «{all[j].Set.DisplayName}»)"));
        foreach (var g in all.GroupBy(x => FieldOf(x.Mod)).Where(g => g.Count() > 1))
        {
            bool replace = g.Any(x => x.Mod.Stacking == ModStacking.Replace);
            list.Add(new(false, $"одно поле меняют {g.Count()} модификатора: {string.Join(", ", g.Select(x => "«" + x.Mod.Title + "»"))} — " +
                                (replace ? "действует последний («Последний заменяет»)" : "складываются по порядку: сценарий → наборы по порядку")));
        }
        foreach (var (m, s) in all)
        {
            string at = $"«{m.Title}» (набор «{s.DisplayName}»)";
            switch (m.Op)
            {
                case MissionModOp.Stat when m.Stat.Stat == "" || db.Stat(m.Stat.Stat) == null:
                    list.Add(new(true, $"{at}: характеристики «{m.Stat.Stat}» нет")); break;
                case MissionModOp.Status when db.Status(m.Status) == null:
                    list.Add(new(true, $"{at}: состояния «{m.Status}» нет")); break;
                case MissionModOp.Wave when m.WaveIndex < 1 || m.WaveIndex > sc.Waves.Count:
                    list.Add(new(true, $"{at}: волны {m.WaveIndex} в сценарии нет (волн {sc.Waves.Count})")); break;
                case MissionModOp.Wave when m.ExtraUnit != "" && db.Unit(m.ExtraUnit) == null:
                    list.Add(new(true, $"{at}: юнита «{m.ExtraUnit}» нет")); break;
                case MissionModOp.Squad when sc.Squad is not { Enabled: true }:
                    list.Add(new(false, $"{at}: у сценария нет отряда игрока — модификатор не действует")); break;
                case MissionModOp.Deployment when !EffectiveGameRules.Resolve(db, sc).Deployment.Enabled:
                    list.Add(new(false, $"{at}: вызов за очки в правилах режима выключен — модификатор не действует")); break;
            }
            if (m.Op is MissionModOp.Stat or MissionModOp.Status && m.Selector.Group != "" && sc.Waves.All(w => w.Groups.All(g => g.Id != m.Selector.Group)))
                list.Add(new(true, $"{at}: группы «{m.Selector.Group}» в волнах нет"));
            if (m.Op is MissionModOp.Stat or MissionModOp.Status && m.Selector.Wave > sc.Waves.Count)
                list.Add(new(true, $"{at}: волны {m.Selector.Wave} нет"));
            if (m.ActiveUntil > 0 && m.ActiveUntil <= m.ActiveFrom) list.Add(new(true, $"{at}: «до» не позже «с»"));
        }
        // The result.
        var (vdb, vsc) = Prepare(db, sc, variant);
        var dep = EffectiveGameRules.Resolve(vdb, vsc).Deployment;
        if (dep.Enabled)
        {
            if (dep.StartingPoints < 0) list.Add(new(true, $"стартовые очки развёртывания отрицательные ({dep.StartingPoints:0.#})"));
            if (dep.GainInterval <= 0 && dep.GainAmount > 0) list.Add(new(true, "начисление очков: интервал не больше 0"));
            if (dep.MaxDeployed < 0) list.Add(new(true, $"«на поле одновременно» меньше 0 ({dep.MaxDeployed})"));
            int fixedOnField = vsc.Squad?.StartDeployed.Count ?? 0;
            if (dep.MaxDeployed > 0 && fixedOnField > dep.MaxDeployed)
                list.Add(new(true, $"«на поле одновременно» {dep.MaxDeployed} меньше стартового состава ({fixedOnField})"));
        }
        if (vsc.Squad is { Enabled: true } sq)
        {
            int required = sq.StartingRoster.Count;
            if (sq.MaxSize > 0 && sq.MaxSize < required) list.Add(new(true, $"размер отряда {sq.MaxSize} меньше обязательных бойцов ({required})"));
            if (sq.MaxSize < 0 || sq.MaxSize > 0 && sq.MaxSize < sq.MinSize) list.Add(new(true, $"размер отряда {sq.MaxSize} меньше минимума {sq.MinSize}"));
        }
        foreach (var row in Preview(db, sc, variant))
            if (double.TryParse(row.Effective, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v <= 0
                && (row.Field.StartsWith(db.Stat(Stats.MoveSpeed)?.DisplayName ?? "move_speed", StringComparison.Ordinal) || row.Field.StartsWith(db.Stat(Stats.HpMax)?.DisplayName ?? "hp_max", StringComparison.Ordinal)))
                list.Add(new(true, $"{row.Field}: {row.Base} → {row.Effective} — юнит не сможет действовать"));
        return list.Distinct().ToList();
    }
}
