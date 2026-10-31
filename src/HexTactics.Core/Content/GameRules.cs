using System;
using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>One value of the rules of a mode as a scenario gets it: the value and where it came from.</summary>
public sealed record GameRuleValue(string Field, string Label, string Value, string Source);

/// <summary>
/// The rules of the mode a scenario plays by (ТЗ ч. 6, 1.4–1.5; docs/DECISIONS_6.md §96): the preset of the scenario, then
/// the scenario's own values. Every value says its source — «пресет «…»», «сценарий» or «по умолчанию».
/// </summary>
public sealed class EffectiveGameRules
{
    public GameRulesDef? Preset { get; init; }
    /// <summary>The preset of the battle rules in force ("" — the global rules).</summary>
    public string RulesPreset { get; init; } = "";
    public SquadStart SquadStart { get; init; } = SquadStart.AllOnField;
    public bool Blocking { get; init; } = true;
    // ТЗ ч. 6, 14.1 (§107): the subsystems switched on.
    public bool Objects { get; init; } = true;
    public bool Modifiers { get; init; } = true;
    public bool Graphs { get; init; } = true;
    public bool Scales { get; init; } = true;
    public bool Summons { get; init; } = true;
    /// <summary>Null — as the battle rules say.</summary>
    public bool? FriendlyFire { get; init; }
    /// <summary>The deployment during the battle (part 6, §97): the scenario's own block, else the preset's.</summary>
    public DeploymentRulesDef Deployment { get; init; } = new();
    public List<GameRuleValue> Values { get; } = new();
    /// <summary>Problems of the choice: a missing preset, contradictions.</summary>
    public List<string> Notes { get; } = new();
    /// <summary>Hints, not problems: own values equal to the preset's (they stop inheriting), settings that act only with a squad.</summary>
    public List<string> Hints { get; } = new();

    public static EffectiveGameRules Resolve(ContentDatabase db, ScenarioDef sc)
    {
        var preset = sc.GameRules == "" ? null : db.Get<GameRulesDef>(sc.GameRules);
        var o = sc.GameRulesOverride;
        string fromPreset = preset == null ? "по умолчанию" : $"пресет «{preset.DisplayName}»";
        T Pick<T>(T? own, T? fromRules, T fallback, out string source) where T : struct
        {
            if (own.HasValue)
            {
                source = "сценарий";
                return own.Value;
            }
            source = preset != null && fromRules.HasValue ? fromPreset : "по умолчанию";
            return fromRules ?? fallback;
        }
        var start = Pick(o?.SquadStart, preset?.SquadStart, SquadStart.AllOnField, out var startSrc);
        var blocking = Pick(o?.Blocking, preset?.Blocking, true, out var blockSrc);
        var objects = Pick(o?.Objects, preset?.Objects, true, out var objectsSrc);
        var modifiers = Pick(o?.Modifiers, preset?.Modifiers, true, out var modsSrc);
        var graphs = Pick(o?.Graphs, preset?.Graphs, true, out var graphsSrc);
        var scales = Pick(o?.Scales, preset?.Scales, true, out var scalesSrc);
        var summons = Pick(o?.Summons, preset?.Summons, true, out var summonsSrc);
        bool? ff;
        string ffSrc;
        if (o?.FriendlyFire is { } ownFf)
        {
            ff = ownFf;
            ffSrc = "сценарий";
        }
        else if (preset?.FriendlyFire is { } presetFf)
        {
            ff = presetFf;
            ffSrc = fromPreset;
        }
        else
        {
            ff = null;
            ffSrc = "правила боя";
        }
        string rulesPreset = sc.RulesPreset != "" ? sc.RulesPreset : preset?.RulesPreset ?? "";
        string rulesSrc = sc.RulesPreset != "" ? "сценарий" : preset != null && preset.RulesPreset != "" ? fromPreset : "по умолчанию";
        var deployment = sc.DeploymentOverride ?? preset?.Deployment ?? new DeploymentRulesDef();
        string deploySrc = sc.DeploymentOverride != null ? "сценарий" : preset != null ? fromPreset : "по умолчанию";
        var e = new EffectiveGameRules
        {
            Preset = preset, RulesPreset = rulesPreset, SquadStart = start, Blocking = blocking, FriendlyFire = ff, Deployment = deployment,
            Objects = objects, Modifiers = modifiers, Graphs = graphs, Scales = scales, Summons = summons,
        };
        e.Values.Add(new GameRuleValue("rules", "Правила боя", rulesPreset == "" ? "глобальные" : db.Get<RulesPresetDef>(rulesPreset)?.DisplayName ?? rulesPreset + " (нет)", rulesSrc));
        e.Values.Add(new GameRuleValue("squadStart", "Отряд на поле в начале", EdUtil.EnumLabel(start), startSrc));
        e.Values.Add(new GameRuleValue("blocking", "Блокирование", blocking ? "включено" : "выключено", blockSrc));
        bool ffNow = ff ?? (db.Get<RulesPresetDef>(rulesPreset)?.Rules ?? db.Rules).Combat.FriendlyFire;
        e.Values.Add(new GameRuleValue("friendlyFire", "Урон по союзникам в области", ffNow ? "включён" : "выключен", ffSrc));
        e.Values.Add(new GameRuleValue("deployment", "Вызов за очки развёртывания", Describe(deployment), deploySrc));
        string On(bool v) => v ? "включены" : "выключены";
        e.Values.Add(new GameRuleValue("objects", "Интерактивные объекты", On(objects), objectsSrc));
        e.Values.Add(new GameRuleValue("modifiers", "Модификаторы миссии", On(modifiers), modsSrc));
        e.Values.Add(new GameRuleValue("graphs", "Граф событий", graphs ? "включён" : "выключен", graphsSrc));
        e.Values.Add(new GameRuleValue("scales", "Шкалы", On(scales), scalesSrc));
        e.Values.Add(new GameRuleValue("summons", "Призывы по описаниям", On(summons), summonsSrc));
        // A switched-off subsystem the mission uses explicitly: the mission still runs, the part is inert (ТЗ 14.1: explicit dependencies).
        if (!graphs && sc.Graphs.Count > 0) e.Hints.Add($"граф событий выключен — {sc.Graphs.Count} граф(ов) сценария не действуют");
        if (!objects && sc.Objects.Any(x => x.States is { States.Count: > 0 })) e.Hints.Add("интерактивные объекты выключены — объекты сценария не переключаются");
        if (!modifiers && (sc.ModifierSets.Count > 0 || sc.AppliedModifiers.Count > 0)) e.Hints.Add("модификаторы миссии выключены — наборы сценария не применяются");
        if (sc.GameRules != "" && preset == null) e.Notes.Add($"правила режима «{sc.GameRules}» не найдены — действуют значения по умолчанию");
        if (rulesPreset != "" && db.Get<RulesPresetDef>(rulesPreset) == null) e.Notes.Add($"пресет правил боя «{rulesPreset}» не найден — действуют глобальные правила");
        if (preset != null && o != null)
        {
            if (o.SquadStart == preset.SquadStart) e.Hints.Add("своё «Отряд на поле в начале» совпадает с пресетом");
            if (o.Blocking == preset.Blocking) e.Hints.Add("своё «Блокирование» совпадает с пресетом");
            if (o.FriendlyFire.HasValue && o.FriendlyFire == preset.FriendlyFire) e.Hints.Add("своё «Урон по союзникам» совпадает с пресетом");
        }
        if (start == SquadStart.StartDeployed && sc.Squad is { Enabled: true, StartDeployed.Count: 0 })
            e.Notes.Add("«Отряд на поле: стартовый состав», но стартовый состав не задан — на поле никого из отряда");
        if (deployment.Enabled && sc.Squad is not { Enabled: true })
            e.Hints.Add("вызов за очки развёртывания действует только при включённом «Отряд игрока»");
        if (deployment.Enabled && deployment.MaxPoints > 0 && deployment.StartingPoints > deployment.MaxPoints)
            e.Notes.Add($"очков в начале ({deployment.StartingPoints:0.#}) больше предела ({deployment.MaxPoints:0.#})");
        if (sc.Squad is not { Enabled: true } && start != SquadStart.AllOnField)
            e.Hints.Add("«Отряд на поле в начале» действует только при включённом «Отряд игрока»");
        return e;
    }

    /// <summary>The deployment in one line: off, or the start, the gain, the limit, the retreat.</summary>
    public static string Describe(DeploymentRulesDef d) => !d.Enabled ? "выключен" :
        $"старт {d.StartingPoints:0.#}" + (d.GainAmount > 0 ? $", +{d.GainAmount:0.#}/{d.GainInterval:0.#} с" : "") + (d.GainPerKill > 0 ? $", +{d.GainPerKill:0.#} за врага" : "") +
        (d.MaxDeployed > 0 ? $", на поле ≤{d.MaxDeployed}" : "") + (d.AllowRetreat ? $", отзыв {d.RetreatRefund:P0}" : ", без отзыва") +
        (d.AfterDeath == AfterDeathPolicy.Cooldown ? $", павший вернётся через {d.DeathCooldown:0.#} с" : "") + (d.Leash > 0 ? $", поводок {d.Leash}" : "");

    /// <summary>What changes between two sets of rules (ТЗ ч. 6, 1.4: the change of a preset shows the difference).</summary>
    public static List<string> Diff(EffectiveGameRules a, EffectiveGameRules b)
    {
        var list = new List<string>();
        foreach (var va in a.Values)
            if (b.Values.FirstOrDefault(v => v.Field == va.Field) is { } vb && vb.Value != va.Value)
                list.Add($"{va.Label}: {va.Value} → {vb.Value}");
        return list;
    }
}

/// <summary>
/// The squad of a scenario (ТЗ ч. 6, 2.2–2.5; docs/DECISIONS_6.md §96): who may be taken, why someone may not, whether a
/// squad is complete. The interface and the battle use the same checks — nothing unavailable gets in by drag and drop.
/// </summary>
public static class SquadRules
{
    /// <summary>A template matches a token: its ID, creature category, category, «tag:x» or a bare tag.</summary>
    public static bool Has(ContentDatabase db, UnitDef def, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        token = token.Trim();
        return CategoryTree.Matches(db, def, token) || !token.Contains(':') && CategoryTree.Matches(db, def, "tag:" + token);
    }

    /// <summary>The templates a squad is made of: the pool of the scenario, or every template of the player's factions.</summary>
    public static List<UnitDef> PoolOf(ContentDatabase db, ScenarioDef sc)
    {
        var squad = sc.Squad ?? new ScenarioSquadDef();
        if (squad.Pool.Count > 0) return squad.Pool.Select(db.Unit).Where(u => u != null).Select(u => u!).Distinct().ToList();
        var player = sc.Sides.Where(s => s.Controller == SideController.Player).Select(s => s.Faction).ToHashSet();
        return db.All<UnitDef>().Where(u => player.Contains(u.Faction)).ToList();
    }

    /// <summary>Why a template is never allowed in this squad (tags, pool); null — it is.</summary>
    public static string? Forbidden(ContentDatabase db, ScenarioDef sc, UnitDef def)
    {
        var squad = sc.Squad ?? new ScenarioSquadDef();
        if (!PoolOf(db, sc).Contains(def)) return "нет среди доступных бойцов сценария";
        if (squad.ForbiddenTags.FirstOrDefault(t => Has(db, def, t)) is { } bad) return $"запрещён тег «{bad}»";
        if (squad.AllowedTags.Count > 0 && !squad.AllowedTags.Any(t => Has(db, def, t))) return "нет разрешённого тега (" + string.Join(", ", squad.AllowedTags) + ")";
        return null;
    }

    /// <summary>Why <paramref name="template"/> cannot be added to <paramref name="current"/> now; null — it can.</summary>
    public static string? WhyNot(ContentDatabase db, ScenarioDef sc, IReadOnlyList<string> current, string template)
    {
        var squad = sc.Squad ?? new ScenarioSquadDef();
        var def = db.Unit(template);
        if (def == null) return $"шаблон «{template}» не найден";
        if (Forbidden(db, sc, def) is { } why) return why;
        if (squad.MaxSize > 0 && current.Count >= squad.MaxSize) return $"отряд полон ({squad.MaxSize})";
        if (!squad.DuplicatesAllowed && current.Contains(template)) return "уже в отряде (повторы запрещены)";
        foreach (var l in squad.Limits.Where(l => Has(db, def, l.Match)))
        {
            int n = current.Count(id => db.Unit(id) is { } u && Has(db, u, l.Match));
            if (n >= l.Max) return l.Max == 0 ? $"«{l.Match}» не берётся" : $"не более {l.Max} «{l.Match}»";
        }
        return null;
    }

    /// <summary>Why a template cannot be removed (it is required); null — it can.</summary>
    public static string? WhyKeep(ScenarioDef sc, IReadOnlyList<string> current, int index)
    {
        var squad = sc.Squad ?? new ScenarioSquadDef();
        if (index < 0 || index >= current.Count) return "нет такого бойца";
        string id = current[index];
        int required = squad.StartingRoster.Count(r => r == id);
        return required > 0 && current.Count(c => c == id) <= required ? "обязательный боец сценария" : null;
    }

    /// <summary>Every problem of a squad as a whole; empty — it may go into battle.</summary>
    public static List<string> Check(ContentDatabase db, ScenarioDef sc, IReadOnlyList<string> squadList)
    {
        var squad = sc.Squad ?? new ScenarioSquadDef();
        var problems = new List<string>();
        var defs = new List<UnitDef>();
        foreach (var id in squadList)
        {
            var def = db.Unit(id);
            if (def == null)
            {
                problems.Add($"шаблон «{id}» не найден");
                continue;
            }
            if (Forbidden(db, sc, def) is { } why) problems.Add($"«{def.DisplayName}»: {why}");
            defs.Add(def);
        }
        if (squadList.Count < squad.MinSize) problems.Add($"бойцов {squadList.Count}, нужно не меньше {squad.MinSize}");
        if (squad.MaxSize > 0 && squadList.Count > squad.MaxSize) problems.Add($"бойцов {squadList.Count}, можно не больше {squad.MaxSize}");
        if (!squad.DuplicatesAllowed)
            foreach (var g in squadList.GroupBy(x => x).Where(g => g.Count() > 1))
                problems.Add($"«{db.Unit(g.Key)?.DisplayName ?? g.Key}» ×{g.Count()} — повторы запрещены");
        foreach (var l in squad.Limits)
        {
            int n = defs.Count(d => Has(db, d, l.Match));
            if (n > l.Max) problems.Add($"«{l.Match}»: {n}, можно не больше {l.Max}");
        }
        foreach (var t in squad.RequiredTags.Where(t => !defs.Any(d => Has(db, d, t))))
            problems.Add($"нужен боец с тегом «{t}»");
        foreach (var g in squad.StartingRoster.GroupBy(x => x))
            if (squadList.Count(x => x == g.Key) < g.Count())
                problems.Add($"нет обязательного бойца «{db.Unit(g.Key)?.DisplayName ?? g.Key}»");
        return problems;
    }

    /// <summary>
    /// The squad without the screen: the required fighters, then the default preset (what fits), up to the size. Fixed
    /// squads, autobattles and tests go into battle with it.
    /// </summary>
    public static List<string> DefaultRoster(ContentDatabase db, ScenarioDef sc)
    {
        var squad = sc.Squad ?? new ScenarioSquadDef();
        var list = new List<string>(squad.StartingRoster.Where(id => db.Unit(id) != null));
        // A required fighter the preset names as well is the same fighter, not a second one.
        var unclaimed = new List<string>(list);
        if (db.Get<SquadPresetDef>(squad.DefaultPreset) is { } preset)
            foreach (var id in preset.Units)
            {
                if (unclaimed.Remove(id)) continue;
                if (WhyNot(db, sc, list, id) == null) list.Add(id);
            }
        return list;
    }

    /// <summary>The squad by role (for the summary of the screen): role → count.</summary>
    public static List<(string Role, int Count)> Roles(ContentDatabase db, IEnumerable<string> squadList) =>
        squadList.Select(db.Unit).Where(u => u != null).GroupBy(u => EdUtil.EnumLabel(u!.Role)).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();

    /// <summary>The scenario's own problems of the squad rules (the check of the content).</summary>
    public static List<string> Validate(ContentDatabase db, ScenarioDef sc)
    {
        var list = new List<string>();
        if (sc.Squad is not { Enabled: true } squad) return list;
        if (squad.MaxSize > 0 && squad.MinSize > squad.MaxSize) list.Add($"«Не меньше бойцов» ({squad.MinSize}) больше «Не больше» ({squad.MaxSize})");
        var pool = PoolOf(db, sc);
        if (pool.Count == 0) list.Add("нет доступных бойцов: задайте «Доступные бойцы» или юнитов фракции игрока");
        foreach (var t in squad.RequiredTags.Where(t => !pool.Any(u => Forbidden(db, sc, u) == null && Has(db, u, t))))
            list.Add($"обязательный тег «{t}» не найти ни у одного доступного бойца");
        foreach (var id in squad.StartingRoster.Concat(squad.StartDeployed).Concat(squad.Pool).Distinct().Where(id => db.Unit(id) == null))
            list.Add($"шаблон «{id}» не найден");
        foreach (var id in squad.StartingRoster.Distinct())
            if (db.Unit(id) is { } u && Forbidden(db, sc, u) is { } why)
                list.Add($"обязательный боец «{u.DisplayName}»: {why}");
        if (squad.MaxSize > 0 && squad.StartingRoster.Count > squad.MaxSize) list.Add($"обязательных бойцов {squad.StartingRoster.Count} — больше размера отряда {squad.MaxSize}");
        if (squad.DefaultPreset != "" && db.Get<SquadPresetDef>(squad.DefaultPreset) == null) list.Add($"пресет отряда «{squad.DefaultPreset}» не найден");
        if (squad.Fixed)
            foreach (var p in Check(db, sc, DefaultRoster(db, sc)))
                list.Add("фиксированный отряд: " + p);
        return list;
    }
}
