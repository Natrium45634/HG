using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

/// <summary>
/// Content checks before publication beyond fields, ranges and references (TZ §15.3,
/// docs/DECISIONS_2.md §33): cyclic triggers, incompatible effects, unreachable objectives,
/// missing resources and potentially endless reactions. Errors block the publication; warnings
/// are accepted by the admin with a confirmation.
/// </summary>
public static class ContentChecks
{
    private static string Src(Def d) => $"{d.Category}:{d.Id}";
    private static ContentIssue Err(Def d, string path, string msg) => new(IssueSeverity.Error, Src(d), path, msg);
    private static ContentIssue Warn(Def d, string path, string msg) => new(IssueSeverity.Warning, Src(d), path, msg);
    private static ContentIssue Info(Def d, string path, string msg) => new(IssueSeverity.Info, Src(d), path, msg);

    public static IEnumerable<ConditionDef> Flatten(ConditionDef? c) =>
        c == null ? Enumerable.Empty<ConditionDef>() : new[] { c }.Concat(c.Children.SelectMany(Flatten));

    /// <summary>Operations inside actions (the «операции над юнитами» action carries skill operations).</summary>
    private static IEnumerable<SkillEffectDef> OpsOf(IEnumerable<TriggerActionDef> actions) => actions.SelectMany(a => a.Effects);

    /// <summary>What the publication does with the issues: errors block it, warnings need a confirmation,
    /// errors of objects marked «неполный» (drafts) do not block — such objects stay out of battles.</summary>
    public sealed record Gate(List<ContentIssue> Blocking, List<ContentIssue> Warnings, List<ContentIssue> DraftErrors)
    {
        public bool CanPublish => Blocking.Count == 0;
    }

    public static Gate PublishGate(ContentDatabase db, IEnumerable<ContentIssue> issues)
    {
        var drafts = new HashSet<string>(db.AllDefs().Where(d => d.Incomplete).Select(Src));
        var blocking = new List<ContentIssue>();
        var draftErrors = new List<ContentIssue>();
        var warnings = new List<ContentIssue>();
        foreach (var i in issues)
        {
            if (i.Severity == IssueSeverity.Warning) warnings.Add(i);
            else if (i.Severity == IssueSeverity.Error) (drafts.Contains(i.Source) ? draftErrors : blocking).Add(i);
        }
        return new Gate(blocking, warnings, draftErrors);
    }

    public static void Run(ContentDatabase db, List<ContentIssue> issues)
    {
        foreach (var s in db.All<ScenarioDef>()) CheckScenario(db, s, issues);
        foreach (var sk in db.All<SkillDef>()) CheckSkill(sk, issues);
        foreach (var st in db.All<StatusDef>()) CheckStatus(st, issues);
        foreach (var u in db.All<UnitDef>()) CheckUnit(db, u, issues);
        foreach (var p in db.All<PassiveDef>()) CheckPassive(p, issues);
        foreach (var a in db.All<AiProfileDef>()) CheckAiProfile(a, issues);
        CheckSynergyCycles(db, issues);
        // Part 4, stage 6 (docs/DECISIONS_4.md §70): warnings about too strong templates.
        BalanceChecks.Run(db, issues);
        CheckAssets(db, issues);
        foreach (var d in db.AllDefs()) CheckOperations(d, issues);
    }

    // ------------------------------------------------------------------ scenarios: triggers, conditions, reachability

    private static void CheckScenario(ContentDatabase db, ScenarioDef s, List<ContentIssue> issues)
    {
        var map = db.Get<MapDef>(s.Map);
        var names = s.Triggers.Select(t => t.Name).ToList();
        foreach (var dup in names.GroupBy(n => n).Where(g => g.Count() > 1))
            issues.Add(Warn(s, "Triggers", $"Несколько триггеров с названием «{dup.Key}» — «Запустить триггер» выберет первый"));

        // «Запустить триггер»: unknown names and cycles (A → B → A).
        var calls = new Dictionary<int, List<int>>();
        for (int i = 0; i < s.Triggers.Count; i++)
        {
            var tr = s.Triggers[i];
            var targets = tr.Actions.Where(a => a.Type == TriggerActionType.ActivateTrigger).Select(a => a.Text)
                .Concat(OpsOf(tr.Actions).Where(e => e.Type == EffectType.ActivateTrigger).Select(e => e.Text)).ToList();
            calls[i] = new List<int>();
            foreach (var name in targets)
            {
                int j = names.IndexOf(name);
                if (j < 0) issues.Add(Err(s, $"Triggers[{tr.Name}]", $"«Запустить триггер»: триггер «{name}» не найден"));
                else calls[i].Add(j);
            }
        }
        foreach (var cycle in Cycles(calls))
            issues.Add(Err(s, "Triggers", "Циклические триггеры: " + string.Join(" → ", cycle.Append(cycle[0]).Select(i => $"«{s.Triggers[i].Name}»")) +
                                          " — запуск друг друга по кругу (в бою цепочка обрывается на глубине " + 8 + ")"));

        // Scenario events (docs/DECISIONS_4.md §68): «action raises an event that starts a rule» cycles, filters.
        foreach (var cyc in ScenarioEvents.Cycles(s))
        {
            if (cyc.Limited) issues.Add(Warn(s, "Triggers", "Намеренный цикл событий (с пределом): " + cyc.Text));
            else issues.Add(Err(s, "Triggers", "Бесконечный цикл событий: " + cyc.Text + " — задайте «однократный», «не чаще» или «не более срабатываний»"));
        }
        var groups = s.Waves.SelectMany(w => w.Groups).Select(g => g.Id).Where(id => id != "").ToHashSet();
        foreach (var tr in s.Triggers.Where(t => t.Event != ScenarioEventType.None))
        {
            string at = $"Triggers[{tr.Name}]";
            if (tr.Event == ScenarioEventType.Timer && !tr.Once && tr.MaxFires == 0 && tr.Actions.SelectMany(ScenarioEvents.Produces).Contains(ScenarioEventType.UnitSpawned)
                && tr.Condition.Type is ConditionType.Always or ConditionType.TimeElapsed)
                issues.Add(Warn(s, at, $"Таймер «{tr.Name}» каждые {tr.Interval:0.#} с создаёт юнитов без предела — задайте «не более срабатываний» или условие завершения"));
            if (tr.EventObject != "")
            {
                bool known = tr.Event switch
                {
                    ScenarioEventType.CheckpointReached => s.Routes.Any(r => r.Nodes.Any(n => n.Id == tr.EventObject)) || s.Object(tr.EventObject) != null,
                    ScenarioEventType.StatusApplied => db.Status(tr.EventObject) != null,
                    ScenarioEventType.SkillUsed => db.Skill(tr.EventObject) != null,
                    ScenarioEventType.PassiveTriggered => db.Get<PassiveDef>(tr.EventObject) != null,
                    _ => s.Object(tr.EventObject) != null,
                };
                if (!known) issues.Add(Warn(s, at, $"Фильтр события «{tr.EventObject}» не найден — триггер «{tr.Name}» не сработает"));
            }
            if (tr.EventGroup != "" && !groups.Contains(tr.EventGroup))
                issues.Add(Warn(s, at, $"Группы «{tr.EventGroup}» нет ни в одной волне — триггер «{tr.Name}» не сработает"));
            if (tr.EventWave > s.Waves.Count) issues.Add(Warn(s, at, $"Волны {tr.EventWave} нет (волн {s.Waves.Count})"));
        }
        CheckPlacement(db, s, issues);

        // Repeating triggers that keep themselves alive: condition always true, or they set the flag they wait for.
        foreach (var tr in s.Triggers.Where(t => !t.Once && t.Event == ScenarioEventType.None && t.Cooldown <= 0 && t.MaxFires <= 0))
        {
            var leaves = Flatten(tr.Condition).ToList();
            bool always = tr.Condition.Type == ConditionType.Always || (tr.Condition.Type == ConditionType.All && tr.Condition.Children.Count == 0);
            if (always && tr.Actions.Count > 0)
                issues.Add(Warn(s, $"Triggers[{tr.Name}]", $"Повторяемый триггер «{tr.Name}» с условием «всегда» срабатывает каждый тик — потенциально бесконечная реакция"));
            var waits = leaves.Where(c => c.Type is ConditionType.FlagSet or ConditionType.FlagTimer).Select(c => c.Flag).ToHashSet();
            var sets = tr.Actions.Where(a => a.Type == TriggerActionType.SetFlag).Select(a => a.Flag)
                .Concat(OpsOf(tr.Actions).Where(e => e.Type == EffectType.SetFlag).Select(e => e.Text));
            if (!always && sets.Any(waits.Contains))
                issues.Add(Warn(s, $"Triggers[{tr.Name}]", $"Повторяемый триггер «{tr.Name}» сам устанавливает флаг своего условия — будет срабатывать каждый тик"));
        }

        if (map == null) return;
        var tags = new HashSet<string>(s.Units.Select(u => u.Tag).Concat(s.Waves.SelectMany(w => w.Units).Select(u => u.Tag))
            .Concat(s.Waves.SelectMany(w => w.Groups).Select(g => g.Tag)).Concat(s.Objects.Select(o => o.Id)).Where(t => !string.IsNullOrEmpty(t)));
        var unitDefs = s.Units.Select(u => db.Unit(u.Unit)).Concat(s.Waves.SelectMany(w => w.Units).Select(u => db.Unit(u.Unit)))
            .Concat(s.Waves.SelectMany(w => w.Groups).Select(g => db.Unit(g.Unit))).Where(u => u != null).Select(u => u!).Distinct().ToList();
        bool hasBoss = unitDefs.Any(u => u.Boss.IsBoss);

        // Conditions and actions of the scenario, its boss phases and the AI rules of its units.
        var conditions = s.Triggers.Select(t => t.Condition).Concat(unitDefs.SelectMany(u => u.Boss.Phases.Select(p => p.When))).SelectMany(Flatten).ToList();
        var actions = s.Triggers.SelectMany(t => t.Actions).Concat(s.Waves.SelectMany(w => w.OnStart.Concat(w.OnEnd)))
            .Concat(unitDefs.SelectMany(u => u.Boss.Phases.SelectMany(p => p.Actions))).ToList();
        conditions.AddRange(s.Objectives.SelectMany(o => new[] { o.Complete, o.Fail }).Concat(s.Victory).Concat(s.Defeat).SelectMany(Flatten));
        foreach (var c in conditions)
        {
            if (c.Type is ConditionType.UnitLeftArea && map.FindMarker(c.Marker) == null)
                issues.Add(Err(s, "Conditions", $"Метка '{c.Marker}' не найдена на карте"));
            if (c.Type is ConditionType.ObstacleDestroyed or ConditionType.SynergyOccurred && !string.IsNullOrEmpty(c.Marker) && map.FindMarker(c.Marker) == null)
                issues.Add(Err(s, "Conditions", $"Метка '{c.Marker}' не найдена на карте"));
            if (c.Type is ConditionType.BossPhase && !hasBoss)
                issues.Add(Warn(s, "Conditions", "Условие «босс сменил фазу», но в сценарии нет босса"));
            if (c.Type is ConditionType.FlagTimer or ConditionType.FlagSet && !string.IsNullOrEmpty(c.Flag) && !FlagIsSet(db, s, unitDefs, c.Flag))
                issues.Add(Warn(s, "Conditions", $"Флаг «{c.Flag}» нигде не устанавливается — условие не выполнится"));
        }
        foreach (var a in actions)
        {
            if (a.Type == TriggerActionType.SetRoute && map.FindRoute(a.Route) == null && s.Route(a.Route) == null)
                issues.Add(Err(s, "Actions", $"«Сменить маршрут»: маршрута '{a.Route}' нет ни на карте, ни в сценарии"));
            if (a.Type == TriggerActionType.SetBossPhase && !hasBoss)
                issues.Add(Err(s, "Actions", "«Сменить фазу босса»: в сценарии нет босса"));
            if (a.Type is TriggerActionType.ActivateBase or TriggerActionType.DeactivateBase && s.Object(a.Object)?.Spawn == null)
                issues.Add(Err(s, "Actions", $"«{a.Describe()}»: базы спавна «{a.Object}» нет"));
            if (a.Type == TriggerActionType.SetObjectState && s.Object(a.Object) == null)
                issues.Add(Err(s, "Actions", $"«{a.Describe()}»: объекта «{a.Object}» нет"));
            if (a.Type == TriggerActionType.SetTarget && a.TargetTag == "" && s.Object(a.Object) == null)
                issues.Add(Err(s, "Actions", $"«Установить цель»: объекта «{a.Object}» нет"));
            if (a.Type == TriggerActionType.SetGroupRoute)
            {
                if (map.FindRoute(a.Route) == null && s.Route(a.Route) == null)
                    issues.Add(Err(s, "Actions", $"«Изменить маршрут группы»: маршрута '{a.Route}' нет ни на карте, ни в сценарии"));
                if (!s.Waves.SelectMany(w => w.Groups).Any(g => g.Id == a.Group))
                    issues.Add(Warn(s, "Actions", $"«Изменить маршрут группы»: группы «{a.Group}» нет"));
            }
            if (a.Type == TriggerActionType.ActivateTrigger && !names.Contains(a.Text))
                issues.Add(Err(s, "Actions", $"«Запустить триггер»: триггер «{a.Text}» не найден"));
            if (a.Target == ActionTarget.UnitTag && !string.IsNullOrEmpty(a.UnitTag) && !tags.Contains(a.UnitTag) && UsesTarget(a))
                issues.Add(Warn(s, "Actions", $"Нет юнита с меткой '{a.UnitTag}' — действие «{a.Describe()}» ни на кого не подействует"));
        }

        CheckReachability(db, s, map, issues);
    }

    /// <summary>The preparation of «TD с расстановкой» and the defenders of «автотест» (docs/DECISIONS_4.md §64).</summary>
    private static void CheckPlacement(ContentDatabase db, ScenarioDef s, List<ContentIssue> issues)
    {
        var pl = s.Placement;
        if (s.Mode == BattleMode.TdPlacement && pl.Units.Count == 0)
            issues.Add(Err(s, "Placement.Units", "TD с расстановкой: нет доступных для расстановки юнитов"));
        foreach (var id in pl.ZoneObjects.Where(id => s.Object(id) == null))
            issues.Add(Err(s, "Placement.ZoneObjects", $"Зона расстановки: объекта «{id}» нет"));
        if (s.Mode == BattleMode.TdPlacement && pl.Budget > 0 && pl.Units.Count > 0)
        {
            double cheapest = pl.Units.Select(e => e.Cost > 0 ? e.Cost : db.Unit(e.Unit) is { } u ? ThreatCost.Of(db, u) : double.MaxValue).Min();
            if (cheapest > pl.Budget) issues.Add(Warn(s, "Placement.Budget", $"Бюджета {pl.Budget:0.#} не хватает ни на один юнит (самый дешёвый {cheapest:0.#})"));
        }
        if (pl.End == PreparationEnd.Condition && pl.StartCondition == null)
            issues.Add(Err(s, "Placement.StartCondition", "Подготовка заканчивается по условию, но условие не задано"));
        var zone = pl.ZoneHexes(s);
        foreach (var su in pl.Preset)
        {
            if (db.Unit(su.Unit) == null) issues.Add(Err(s, "Placement.Preset", $"Расстановка по умолчанию: шаблона «{su.Unit}» нет"));
            if (zone.Count > 0 && !zone.Contains(su.C)) issues.Add(Info(s, "Placement.Preset", $"Расстановка по умолчанию: «{su.Unit}» в {su.C} вне зоны расстановки (автотест ставит его всё равно)"));
        }
        if (s.Mode == BattleMode.AutoTest && pl.Preset.Count == 0 && !s.Units.Any(u => u.Faction == "" ? db.Unit(u.Unit)?.Faction == "player" : u.Faction == "player"))
            issues.Add(Warn(s, "Placement.Preset", "Автотест без защитников: нет ни расстановки по умолчанию, ни юнитов игрока"));
    }

    private static bool UsesTarget(TriggerActionDef a) => a.Type is TriggerActionType.ApplyStatus or TriggerActionType.RemoveStatus or TriggerActionType.AddSkill
        or TriggerActionType.RemoveSkill or TriggerActionType.SetAutoAttackSkill or TriggerActionType.SetAiProfile or TriggerActionType.HealPercent
        or TriggerActionType.Effects or TriggerActionType.SetRoute or TriggerActionType.SetBossPhase;

    private static bool FlagIsSet(ContentDatabase db, ScenarioDef s, List<UnitDef> units, string flag)
    {
        // Nodes of the routes set their flags when an enemy passes (docs/DECISIONS_4.md §57).
        if (s.Routes.Any(r => r.Nodes.Any(n => n.Event == flag))) return true;
        // Skills, passives and statuses can set flags too (operation «событие сценария»).
        if (db.AllDefs().Where(d => d is not ScenarioDef).SelectMany(Operations).Any(x => x.Effect.Type == EffectType.SetFlag && x.Effect.Text == flag)) return true;
        var actions = s.Triggers.SelectMany(t => t.Actions).Concat(s.Waves.SelectMany(w => w.OnStart.Concat(w.OnEnd)))
            .Concat(units.SelectMany(u => u.Boss.Phases.SelectMany(p => p.Actions))).ToList();
        if (actions.Any(a => a.Type == TriggerActionType.SetFlag && a.Flag == flag)) return true;
        return OpsOf(actions).Any(e => e.Type == EffectType.SetFlag && e.Text == flag);
    }

    /// <summary>Objective cells the player must reach (capture, escort, reach a marker) are reachable from the player's units.</summary>
    private static void CheckReachability(ContentDatabase db, ScenarioDef s, MapDef map, List<ContentIssue> issues)
    {
        var playerFactions = new HashSet<string>(s.Sides.Where(x => x.Controller == SideController.Player).Select(x => x.Faction));
        var starts = new List<(HexCoord C, string Move)>();
        foreach (var su in s.Units)
        {
            var ud = db.Unit(su.Unit);
            if (ud == null) continue;
            string faction = string.IsNullOrEmpty(su.Faction) ? ud.Faction : su.Faction;
            if (playerFactions.Contains(faction)) starts.Add((su.C, ud.MoveCategory));
        }
        if (starts.Count == 0) return;
        var cells = map.Cells.ToDictionary(c => c.C);
        var reach = new HashSet<HexCoord>();
        foreach (var (c, move) in starts.Distinct()) reach.UnionWith(MapAnalysis.Reachable(db, cells, c, move));

        IEnumerable<HexCoord> MarkerCells(string id)
        {
            var m = map.FindMarker(id);
            return m == null ? Enumerable.Empty<HexCoord>() : HexCoord.Spiral(m.C, m.Radius).Where(cells.ContainsKey);
        }
        foreach (var o in s.Objectives)
        {
            if (o.Kind is not (ObjectiveKind.Capture or ObjectiveKind.Escort)) continue;
            var target = o.Cells.Concat(MarkerCells(o.Marker)).ToList();
            if (target.Count == 0 || target.Any(reach.Contains)) continue;
            string msg = $"Цель «{o.Name}» недостижима: юниты игрока не могут дойти до её клеток";
            issues.Add(o.Required ? Err(s, $"Objectives[{o.Id}]", msg) : Warn(s, $"Objectives[{o.Id}]", msg));
        }
        foreach (var c in s.Victory.SelectMany(Flatten).Where(c => c.Type == ConditionType.ReachMarker))
        {
            var target = MarkerCells(c.Marker).ToList();
            if (target.Count > 0 && !target.Any(reach.Contains))
                issues.Add(Err(s, "Victory", $"Условие «{c.Describe()}» недостижимо: юниты игрока не могут дойти до метки"));
        }
    }

    /// <summary>Elementary cycles of a small directed graph (each cycle reported once, from its smallest node).</summary>
    public static List<List<int>> Cycles(Dictionary<int, List<int>> graph)
    {
        var result = new List<List<int>>();
        var seen = new HashSet<string>();
        foreach (var start in graph.Keys.OrderBy(k => k))
        {
            var path = new List<int>();
            void Dfs(int n)
            {
                if (path.Count > 32) return;
                path.Add(n);
                foreach (var m in graph.GetValueOrDefault(n) ?? new List<int>())
                {
                    if (m == start)
                    {
                        var cycle = path.ToList();
                        int min = cycle.Min();
                        int at = cycle.IndexOf(min);
                        var norm = cycle.Skip(at).Concat(cycle.Take(at)).ToList();
                        if (seen.Add(string.Join(",", norm))) result.Add(norm);
                    }
                    else if (m > start && !path.Contains(m)) Dfs(m);
                }
                path.RemoveAt(path.Count - 1);
            }
            Dfs(start);
        }
        return result;
    }

    // ------------------------------------------------------------------ incompatible effects

    private static void CheckSkill(SkillDef sk, List<ContentIssue> issues)
    {
        for (int i = 0; i < sk.Stages.Count; i++)
        {
            var ops = sk.Stages[i].Effects;
            if (ops.Any(e => e.Type == EffectType.Push) && ops.Any(e => e.Type == EffectType.Pull))
                issues.Add(Warn(sk, $"Stages[{i}]", "Несовместимые эффекты: толчок и притяжение в одном этапе гасят друг друга"));
            foreach (var st in ops.Where(e => e.Type == EffectType.ApplyStatus).Select(e => e.Status).Intersect(ops.Where(e => e.Type == EffectType.RemoveStatus).Select(e => e.Status)))
                issues.Add(Warn(sk, $"Stages[{i}]", $"Несовместимые эффекты: состояние «{st}» и накладывается, и снимается в одном этапе"));
            var area = sk.Stages[i].Area;
            if (sk.Targeting == TargetingMode.Unit && area.Shape == AreaShape.Mask && area.Cells.Count > 0 && !area.Cells.Any(c => c.Offset == default))
                issues.Add(Warn(sk, $"Stages[{i}].Area", "Наведение на юнита, а маска области не содержит клетку цели — выбранная цель не будет задета (маска строится от цели)"));
            if (ops.Count(e => e.Type == EffectType.TeleportCaster) > 0 && sk.Targeting == TargetingMode.Self)
                issues.Add(Warn(sk, $"Stages[{i}]", "Перемещение исполнителя в навыке «на себя» ничего не делает"));
            foreach (var t in ops.Where(e => e.Type == EffectType.ChangeTerrain).Select(e => e.Terrain).Intersect(ops.Where(e => e.Type == EffectType.DestroyObstacle).Select(_ => "")))
                _ = t;
        }
    }

    private static void CheckStatus(StatusDef st, List<ContentIssue> issues)
    {
        switch (st.Lasts)
        {
            case StatusLasts.WhileCondition when st.WhileCondition == null:
                issues.Add(Err(st, "WhileCondition", "Режим «пока выполняется условие», но условие не задано"));
                break;
            case StatusLasts.WhileEffectActive when string.IsNullOrEmpty(st.WhileStatus) && string.IsNullOrEmpty(st.WhileCellEffect):
                issues.Add(Err(st, "WhileStatus", "Режим «пока активен другой эффект», но эффект не задан"));
                break;
            case StatusLasts.WhileEffectActive when st.WhileStatus == st.Id:
                issues.Add(Err(st, "WhileStatus", "Состояние действует «пока активно» само себя — оно никогда не закончится"));
                break;
        }
        if (st.Lasts != StatusLasts.Timed && st.Duration > 0)
            issues.Add(new ContentIssue(IssueSeverity.Info, Src(st), "Duration", "Длительность не используется: состояние заканчивается по своему режиму"));
        if (st.Cancels.Contains(st.Id))
            issues.Add(Warn(st, "Cancels", "Состояние снимает само себя при наложении"));
        // A level applying the status itself on each tick never ends.
        for (int l = 0; l < st.Levels.Count; l++)
            if (st.Levels[l].OnTick.Any(e => e.Type == EffectType.ApplyStatus && e.Status == st.Id))
                issues.Add(Warn(st, $"Levels[{l}].OnTick", "Состояние на каждом тике накладывает само себя — потенциально бесконечное действие"));
    }

    // ------------------------------------------------------------------ missing resources

    private static void CheckUnit(ContentDatabase db, UnitDef u, List<ContentIssue> issues)
    {
        var has = new HashSet<string>(u.Resources.Select(r => r.Resource));
        foreach (var sid in u.Skills.Append(u.BasicAttack).Where(x => !string.IsNullOrEmpty(x)))
        {
            var sk = db.Get<SkillDef>(sid);
            if (sk == null) continue;
            foreach (var cost in sk.ResourceCosts.Where(c => !has.Contains(c.Resource)))
                issues.Add(Warn(u, "Skills", $"Отсутствующий ресурс: навык «{sk.DisplayName}» требует «{db.Get<ResourceDef>(cost.Resource)?.DisplayName ?? cost.Resource}», которого у юнита нет — навык не применить"));
        }
        // Several directions of view: the primary line is mandatory (docs/DECISIONS_3.md §40).
        var p = u.Perception;
        if (p.ActiveLines != null)
        {
            int primaries = p.Lines.Count(l => l.Primary && !l.Blind);
            if (primaries == 0)
                issues.Add(Warn(u, "Perception.Lines", $"Не задана основная линия взгляда — используется «{p.PrimaryLine()?.ToString() ?? "восток"}»; отметьте основную линию"));
            else if (primaries > 1)
                issues.Add(Warn(u, "Perception.Lines", "Несколько основных линий взгляда — используется первая"));
            if (p.Lines.All(l => l.Blind))
                issues.Add(Warn(u, "Perception.Lines", "Все линии обзора — слепые зоны: юнит видит только вплотную"));
        }
    }

    // ------------------------------------------------------------------ endless reactions

    private static void CheckPassive(PassiveDef p, List<ContentIssue> issues)
    {
        bool answersHit = p.Trigger is PassiveTrigger.OnDamaged or PassiveTrigger.OnHitEnemy or PassiveTrigger.OnAttackNoticed;
        bool hitsBack = p.Action is PassiveAction.CounterAttack or PassiveAction.UseSkillOnSource
                        || (p.Action == PassiveAction.EffectsOnSource && p.Effects.Any(e => e.Type == EffectType.Damage));
        if (answersHit && hitsBack && p.Cooldown <= 0 && p.ApCost <= 0)
            issues.Add(Warn(p, "Cooldown", "Ответная реакция без перезарядки и стоимости: два таких юнита отвечают друг другу без конца (бой обрывает цепочку только по глубине) — задайте перезарядку"));
        if (p.Trigger == PassiveTrigger.Periodic && p.Interval < 0.1)
            issues.Add(Warn(p, "Interval", "Периодическая пассивка чаще 10 раз в секунду"));
    }

    private static void CheckAiProfile(AiProfileDef a, List<ContentIssue> issues)
    {
        for (int i = 0; i < a.Rules.Count; i++)
        {
            var r = a.Rules[i];
            bool always = r.When.Type == ConditionType.Always || (r.When.Type == ConditionType.All && r.When.Children.Count == 0);
            if (!r.Once && r.Cooldown <= 0)
                issues.Add(Warn(a, $"Rules[{i}]", $"Повторяемое правило «{r.Name}» без перезарядки срабатывает каждый тик, пока условие верно"));
            if (always && r.Once == false && r.Actions.Count > 0 && r.Cooldown < 1)
                issues.Add(Warn(a, $"Rules[{i}]", $"Правило «{r.Name}» с условием «всегда» — потенциально бесконечная реакция"));
            if (r.Actions.Any(x => x.Type == TriggerActionType.SetAiProfile && x.AiProfile == a.Id && x.Target == ActionTarget.Self))
                issues.Add(Warn(a, $"Rules[{i}]", "Правило переключает юнита на этот же профиль"));
        }
    }

    /// <summary>Synergies whose results create their own inputs again (A+B → … → A) chain without end except cooldowns.</summary>
    private static void CheckSynergyCycles(ContentDatabase db, List<ContentIssue> issues)
    {
        var syns = db.All<SynergyDef>().ToList();
        var ids = syns.Select((s, i) => (s, i)).ToList();
        var graph = new Dictionary<int, List<int>>();
        foreach (var (s, i) in ids)
        {
            var produced = new HashSet<string>(s.AddEffects.Concat(s.RadiusCellEffects));
            graph[i] = ids.Where(x => (x.s.A.Kind == SynergyOperandKind.CellEffect && produced.Contains(x.s.A.Value))
                                      || (x.s.B.Kind == SynergyOperandKind.CellEffect && produced.Contains(x.s.B.Value)))
                .Select(x => x.i).ToList();
        }
        foreach (var cycle in Cycles(graph))
        {
            var first = syns[cycle[0]];
            // A chain that consumes something it does not produce again (fire + poison → fire, poison used up) ends by itself.
            var produced = cycle.SelectMany(i => syns[i].AddEffects.Concat(syns[i].RadiusCellEffects)).ToHashSet();
            var consumed = cycle.SelectMany(i => new[]
            {
                syns[i].RemoveA && syns[i].A.Kind == SynergyOperandKind.CellEffect ? syns[i].A.Value : null,
                syns[i].RemoveB && syns[i].B.Kind == SynergyOperandKind.CellEffect ? syns[i].B.Value : null,
            }).Where(x => x != null).ToList();
            if (consumed.Any(c => !produced.Contains(c!))) continue;
            issues.Add(Warn(first, "AddEffects", "Цикл синергий: " + string.Join(" → ", cycle.Append(cycle[0]).Select(i => $"«{syns[i].DisplayName}»")) +
                                                 " — потенциально бесконечная реакция (ограничивается только перезарядкой на клетку)"));
        }
    }

    // ------------------------------------------------------------------ resources (models, textures, sounds, effects)

    private static void CheckAssets(ContentDatabase db, List<ContentIssue> issues)
    {
        foreach (var a in db.All<AssetDef>())
        {
            if (!a.HasFile)
            {
                if (!string.IsNullOrEmpty(a.Texture)) Kind(db, a, "Texture", a.Texture, issues, AssetKind.Texture);
                continue;
            }
            if (string.IsNullOrWhiteSpace(a.Path)) issues.Add(Err(a, "Path", "Не указан файл ресурса"));
            else if (!ContentPackage.IsSafePath(a.Path)) issues.Add(Err(a, "Path", $"Недопустимый путь «{a.Path}»: только относительные пути внутри пакета, без «..»"));
            else if (AssetValidator.KindByExtension(a.Path) is not { } fileKind) issues.Add(Err(a, "Path", $"Формат файла «{a.Path}» не поддерживается"));
            else if (!AssetValidator.Compatible(fileKind, a.Kind)) issues.Add(Err(a, "Kind", $"Файл «{a.Path}» не подходит для вида «{a.Kind}»"));
            if (a.Kind == AssetKind.Model && a.Clips.Count > 0)
                foreach (var m in a.Animations.Where(m => !string.IsNullOrEmpty(m.Clip) && !a.Clips.Contains(m.Clip)))
                    issues.Add(Warn(a, "Animations", $"Клипа «{m.Clip}» нет в модели (есть: {string.Join(", ", a.Clips)}) — роль «{m.Role}» будет анимирована процедурно"));
            foreach (var dup in a.Animations.GroupBy(m => m.Role).Where(g => g.Count() > 1))
                issues.Add(Warn(a, "Animations", $"Роль «{dup.Key}» указана несколько раз — используется первая"));
        }
        foreach (var u in db.All<UnitDef>())
        {
            Kind(db, u, "Visual.Model", u.Visual.Model, issues, AssetKind.Model);
            Kind(db, u, "Visual.Icon", u.Visual.Icon, issues, AssetKind.Icon, AssetKind.Texture, AssetKind.Sprite);
            Kind(db, u, "Visual.AttackSound", u.Visual.AttackSound, issues, AssetKind.Sound);
            Kind(db, u, "Visual.HitSound", u.Visual.HitSound, issues, AssetKind.Sound);
            Kind(db, u, "Visual.DeathSound", u.Visual.DeathSound, issues, AssetKind.Sound);
        }
        foreach (var sk in db.All<SkillDef>()) Kind(db, sk, "Sound", sk.Sound, issues, AssetKind.Sound);
        foreach (var v in db.All<VfxDef>())
        {
            Kind(db, v, "Sound", v.Sound, issues, AssetKind.Sound);
            Kind(db, v, "Sprite", v.Sprite, issues, AssetKind.Texture, AssetKind.Sprite);
        }
        foreach (var t in db.All<TerrainDef>())
        {
            Kind(db, t, "Look.TopMaterial", t.Look.TopMaterial, issues, AssetKind.Material);
            Kind(db, t, "Look.PropModel", t.Look.PropModel, issues, AssetKind.Model);
        }
        // «Визуальный эффект» / «Звук» operations name a resource by ID.
        foreach (var d in db.AllDefs())
        foreach (var (e, path) in Operations(d))
        {
            if (e.Type == EffectType.PlayVfx && !string.IsNullOrWhiteSpace(e.Text) && db.Get<VfxDef>(e.Text) == null)
                issues.Add(Warn(d, path, $"Визуальный эффект «{e.Text}» не найден — будет показан эффект по умолчанию"));
            if (e.Type == EffectType.PlaySound && !string.IsNullOrWhiteSpace(e.Text) && db.Get<AssetDef>(e.Text) is not { Kind: AssetKind.Sound })
                issues.Add(Warn(d, path, $"Звук «{e.Text}» не найден — ничего не прозвучит"));
        }
    }

    private static void Kind(ContentDatabase db, Def owner, string path, string id, List<ContentIssue> issues, params AssetKind[] allowed)
    {
        if (string.IsNullOrEmpty(id)) return;
        var a = db.Get<AssetDef>(id);
        if (a != null && !allowed.Contains(a.Kind))
            issues.Add(Err(owner, path, $"Ресурс «{a.DisplayName}» — {a.Kind}, а здесь нужен {string.Join(" или ", allowed)}"));
    }

    // ------------------------------------------------------------------ operations anywhere

    private static void CheckOperations(Def owner, List<ContentIssue> issues)
    {
        foreach (var (e, path) in Operations(owner))
        {
            switch (e.Type)
            {
                case EffectType.ModifyStat when string.IsNullOrEmpty(e.Stat):
                    issues.Add(Err(owner, path, "«Изменить характеристику»: не выбрана характеристика"));
                    break;
                case EffectType.ModifyStat when Math.Abs(e.StatAdd) < 1e-12 && Math.Abs(e.StatMul) < 1e-12:
                    issues.Add(Warn(owner, path, "«Изменить характеристику»: прибавка и множитель равны 0 — ничего не меняется"));
                    break;
                case EffectType.ModifyAccumulation when string.IsNullOrEmpty(e.Status):
                    issues.Add(Err(owner, path, "«Изменить накопление»: не выбрано состояние"));
                    break;
                case EffectType.SetRoute when string.IsNullOrEmpty(e.Route):
                case EffectType.SetAiProfile when string.IsNullOrEmpty(e.AiProfile):
                    issues.Add(Err(owner, path, $"«{e.Describe()}»: не задано, на что менять"));
                    break;
                case EffectType.Message or EffectType.PlayVfx or EffectType.PlaySound or EffectType.SetFlag or EffectType.ActivateTrigger when string.IsNullOrWhiteSpace(e.Text):
                    issues.Add(Err(owner, path, $"Операция «{e.Type}»: не задан текст / ID"));
                    break;
            }
        }
    }

    /// <summary>All skill operations inside a definition, with a readable path.</summary>
    public static IEnumerable<(SkillEffectDef Effect, string Path)> Operations(Def d)
    {
        switch (d)
        {
            case SkillDef sk:
                for (int i = 0; i < sk.Stages.Count; i++)
                for (int j = 0; j < sk.Stages[i].Effects.Count; j++)
                    yield return (sk.Stages[i].Effects[j], $"Stages[{i}].Effects[{j}]");
                for (int j = 0; j < sk.CritEffects.Count; j++) yield return (sk.CritEffects[j], $"CritEffects[{j}]");
                break;
            case PassiveDef p:
                for (int j = 0; j < p.Effects.Count; j++) yield return (p.Effects[j], $"Effects[{j}]");
                break;
            case StatusDef st:
                for (int l = 0; l < st.Levels.Count; l++)
                for (int j = 0; j < st.Levels[l].OnTick.Count; j++)
                    yield return (st.Levels[l].OnTick[j], $"Levels[{l}].OnTick[{j}]");
                break;
            case ScenarioDef s:
                foreach (var (a, i) in s.Triggers.SelectMany(t => t.Actions).Select((a, i) => (a, i)))
                for (int j = 0; j < a.Effects.Count; j++)
                    yield return (a.Effects[j], $"Triggers.Actions[{i}].Effects[{j}]");
                break;
        }
    }
}
