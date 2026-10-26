using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;

namespace HexTactics.Core.Sim;

/// <summary>
/// Scenario events (docs/DECISIONS_4.md §68): event → conditions → actions. Events are queued as they happen and
/// processed by the scenario step; an action that raises an event makes a chain one step deeper, a chain deeper
/// than <see cref="MaxTriggerDepth"/> is cut. Every trigger may limit itself (once, rate limit, maximum fires).
/// The preparation phase of «TD с расстановкой» (§64) holds the waves and the outcome until it ends.
/// </summary>
public sealed partial class ScenarioSystem
{
    private readonly List<ScenarioEvent> _events = new();
    private readonly Dictionary<int, int> _triggerFires = new();
    private readonly Dictionary<int, long> _triggerNext = new();
    private readonly Dictionary<int, bool> _conditionWas = new();
    private readonly Dictionary<int, long> _timerNext = new();
    private readonly Dictionary<string, bool> _objectActive = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _groupRoutes = new(StringComparer.Ordinal);
    private bool _started;
    private bool _hasEventTriggers;
    private int _eventDepth = -1;
    private bool _processing;

    /// <summary>Events processed in one step at most (a runaway chain is cut, not hung).</summary>
    public const int MaxEventsPerStep = 2000;

    /// <summary>Chains of events cut by the depth limit or the per-step limit.</summary>
    public long EventChainCuts { get; private set; }

    /// <summary>How many times each trigger fired (by index).</summary>
    public int FiresOf(int trigger) => _triggerFires.GetValueOrDefault(trigger);

    /// <summary>The triggers that fired and how many times (part 6, T28: a saved battle must not fire a «once» again).</summary>
    public IEnumerable<(int Trigger, int Fires)> TriggerFires => _triggerFires.OrderBy(k => k.Key).Select(k => (k.Key, k.Value));

    /// <summary>The boss phases entered, as «unit:phase» (part 6, T28).</summary>
    public IEnumerable<string> BossPhasesEntered => _bossPhases.OrderBy(k => k.Key).SelectMany(k => k.Value.OrderBy(i => i).Select(i => $"{k.Key}:{i}"));

    /// <summary>The battle mode of the loaded scenario.</summary>
    public BattleMode Mode => Def?.Mode ?? BattleMode.Normal;

    /// <summary>The preparation phase (placement) is going on.</summary>
    public bool Preparing { get; private set; }

    /// <summary>Waves, objectives and the outcome wait for the end of the preparation.</summary>
    public bool WavesHeld => Preparing && Def?.Placement.EnemiesDuringPreparation != true;

    /// <summary>The tick the battle started (the end of the preparation, 0 without one).</summary>
    public long BattleStartTick { get; private set; }

    /// <summary>Seconds of battle: «прошло N с» and the time limit count from the end of the preparation.</summary>
    public double BattleTime => WavesHeld ? 0 : Math.Max(0, (_sim.Tick - BattleStartTick) * _sim.Dt);

    /// <summary>Seconds of preparation so far.</summary>
    public double PreparationTime => Preparing ? _sim.Tick * _sim.Dt : 0;

    private void LoadEvents(ScenarioDef sc)
    {
        _hasEventTriggers = sc.Triggers.Any(t => t.Event != ScenarioEventType.None);
        Preparing = sc.Mode == BattleMode.TdPlacement;
        if (!WavesHeld) StartTimers();
    }

    private void StartTimers()
    {
        if (Def == null) return;
        for (int i = 0; i < Def.Triggers.Count; i++)
            if (Def.Triggers[i].Event == ScenarioEventType.Timer)
                _timerNext[i] = _sim.Tick + _sim.TicksFor(Math.Max(0.05, Def.Triggers[i].Interval));
    }

    /// <summary>
    /// Ends the preparation (the player confirmed, the time ran out, the condition held): the waves on a timer are
    /// planned from now, the battle time starts, «завершение подготовки» is raised.
    /// </summary>
    public void EndPreparation(string reason)
    {
        if (!Preparing || Def == null) return;
        bool held = WavesHeld;
        Preparing = false;
        if (held)
        {
            BattleStartTick = _sim.Tick;
            foreach (var w in _waves)
                if (w.Def.Start == WaveStart.Time && w.State == WaveState.Pending)
                    Schedule(w, _sim.Tick + _sim.TicksFor(w.Def.Time), lead: false);
            foreach (var o in _objectives) o.ActivatedTick = -1;
            StartTimers();
        }
        _hadPlayerUnits = _sim.Units.Any(u => _sim.IsPlayerFaction(u.FactionId) && !u.IsStructure) || _hadPlayerUnits;
        _sim.Emit(SimEventType.PreparationDone, LogLevel.Normal, "Подготовка завершена — " + reason);
        Raise(ScenarioEventType.PreparationDone);
    }

    private void UpdatePreparation()
    {
        var pl = Def!.Placement;
        switch (pl.End)
        {
            case PreparationEnd.Timer:
            case PreparationEnd.ConfirmOrTimer:
                if (pl.Duration > 0 && _sim.Tick >= _sim.TicksFor(pl.Duration)) EndPreparation($"время подготовки ({pl.Duration:0} с) истекло");
                break;
            case PreparationEnd.Condition:
                if (pl.StartCondition != null && Evaluate(pl.StartCondition)) EndPreparation("выполнено условие начала");
                break;
        }
    }

    // ------------------------------------------------------------------ objects and groups changed by events

    /// <summary>An object switched on or off by an event (overrides «Активна» of its spawn); null — not changed.</summary>
    public bool? ObjectActive(string id) => _objectActive.TryGetValue(id, out var on) ? on : null;

    public void SetObjectActive(string id, bool on, string source)
    {
        var o = Def?.Object(id);
        if (o == null)
        {
            _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"{source}: объект «{id}» не найден");
            return;
        }
        bool was = ObjectActive(id) ?? o.Spawn?.Active ?? true;
        _objectActive[id] = on;
        if (was == on) return;
        _sim.Emit(SimEventType.Info, LogLevel.Normal, $"«{o.DisplayName}» {(on ? "включена" : "отключена")} ({source})");
        if (on) Raise(ScenarioEventType.ObjectActivated, null, id);
    }

    /// <summary>Opens or closes a zone of deployment (part 6, §97); the fighters already on the field stay.</summary>
    public void SetZoneOpen(string id, bool open, string source)
    {
        var z = Def?.DeploymentZones.FirstOrDefault(x => x.Id == id);
        if (z == null)
        {
            _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"{source}: зоны развёртывания «{id}» нет");
            return;
        }
        bool was = ZoneOpen(z);
        _objectActive["zone:" + id] = open;
        if (was != open) _sim.Emit(SimEventType.Info, LogLevel.Normal, $"Зона «{(z.Name != "" ? z.Name : z.Id)}» {(open ? "открыта" : "закрыта")} ({source})");
    }

    /// <summary>Bases and zones switched by events, as «id=on» (the fingerprint: a loaded battle keeps them).</summary>
    public IEnumerable<string> SwitchedObjects => _objectActive.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}");

    /// <summary>Whether a zone of deployment is open now: as switched by an event, or as drawn.</summary>
    public bool ZoneOpen(DeploymentZoneDef z) => _objectActive.TryGetValue("zone:" + z.Id, out var on) ? on : z.Active;

    /// <summary>The route a group was switched to by an event ("" — its own).</summary>
    public string GroupRoute(string group) => _groupRoutes.GetValueOrDefault(group, "");

    private void SetGroupRoute(string group, string route, string source)
    {
        if (group == "" || RoutePoints(route) == null)
        {
            _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"{source}: маршрут «{route}» для группы «{group}» не найден");
            return;
        }
        _groupRoutes[group] = route;
        int n = 0;
        foreach (var u in _sim.Units.Where(u => u.Alive && u.Group == group).ToList())
        {
            _sim.Routes.Assign(u, route, fromStart: false);
            if (Def?.Route(route) is { Target: not "" } r) u.TargetObject = r.Target;
            n++;
        }
        _sim.Emit(SimEventType.Info, LogLevel.Normal, $"Группа «{group}» идёт по маршруту «{Def?.Route(route)?.DisplayName ?? route}»" + (n > 0 ? $" ({n} в бою)" : ""));
    }

    // ------------------------------------------------------------------ raising

    /// <summary>Queues an event of the scenario (processed by the scenario step, or at once at the end of the battle).</summary>
    public void Raise(ScenarioEventType type, Unit? unit = null, string obj = "", int wave = -1, string group = "")
    {
        if (!_hasEventTriggers || Def == null) return;
        _events.Add(new ScenarioEvent { Type = type, Unit = unit?.Id ?? -1, Object = obj, Wave = wave, Group = group, Depth = _eventDepth + 1 });
    }

    /// <summary>Simulation events that are scenario events (appearance, death, status, skill, passive).</summary>
    internal void OnSimEvent(SimEvent e)
    {
        if (!_hasEventTriggers) return;
        switch (e.Type)
        {
            case SimEventType.UnitSpawned:
                Raise(ScenarioEventType.UnitSpawned, _sim.GetUnit(e.SourceUnit));
                break;
            case SimEventType.Death:
                Raise(ScenarioEventType.UnitDied, _sim.GetUnit(e.TargetUnit));
                break;
            case SimEventType.StatusApplied when e.DamageType != "":
                Raise(ScenarioEventType.StatusApplied, _sim.GetUnit(e.TargetUnit), e.DamageType);
                break;
            case SimEventType.CastReleased:
                Raise(ScenarioEventType.SkillUsed, _sim.GetUnit(e.SourceUnit), e.DamageType);
                break;
            case SimEventType.PassiveTriggered:
                Raise(ScenarioEventType.PassiveTriggered, _sim.GetUnit(e.SourceUnit), e.DamageType);
                break;
        }
    }

    // ------------------------------------------------------------------ processing

    private bool CanFire(int i, TriggerDef tr)
    {
        if (tr.Once && _firedTriggers.Contains(i)) return false;
        if (tr.MaxFires > 0 && FiresOf(i) >= tr.MaxFires) return false;
        return !_triggerNext.TryGetValue(i, out var next) || _sim.Tick >= next;
    }

    private void Fire(int i, TriggerDef tr, Unit? actor, string why)
    {
        _triggerFires[i] = FiresOf(i) + 1;
        if (tr.Once) _firedTriggers.Add(i);
        if (!tr.Once && tr.Cooldown > 0) _triggerNext[i] = _sim.Tick + _sim.TicksFor(tr.Cooldown);
        if (why != "") _sim.Emit(SimEventType.Info, LogLevel.Detail, $"Триггер «{tr.Name}»: {why}", actor?.Id ?? -1);
        foreach (var a in tr.Actions) Execute(a, tr.Name, actor);
    }

    /// <summary>Triggers without an event (the condition every tick), on a timer, on «условие выполнено».</summary>
    private void UpdateTriggers()
    {
        for (int i = 0; i < Def!.Triggers.Count; i++)
        {
            var tr = Def.Triggers[i];
            switch (tr.Event)
            {
                case ScenarioEventType.None:
                    if (!CanFire(i, tr) || !Evaluate(tr.Condition)) continue;
                    Fire(i, tr, null, "");
                    break;
                case ScenarioEventType.Timer:
                {
                    if (!_timerNext.TryGetValue(i, out var at) || _sim.Tick < at) continue;
                    // Once — one time; repeating — every interval (a missed occurrence is not caught up).
                    if (tr.Once) _timerNext.Remove(i);
                    else _timerNext[i] = at + _sim.TicksFor(Math.Max(0.05, tr.Interval));
                    if (CanFire(i, tr) && Evaluate(tr.Condition)) Fire(i, tr, null, $"таймер {tr.Interval:0.#} с");
                    break;
                }
                case ScenarioEventType.ConditionMet:
                {
                    bool now = Evaluate(tr.Condition);
                    bool was = _conditionWas.GetValueOrDefault(i);
                    _conditionWas[i] = now;
                    if (now && !was && CanFire(i, tr)) Fire(i, tr, null, "условие выполнено");
                    break;
                }
            }
        }
    }

    /// <summary>The queued events, in order; events raised meanwhile are processed in the same pass, one step deeper.</summary>
    private void ProcessEvents()
    {
        if (_processing || _events.Count == 0 || Def == null) return;
        _processing = true;
        int handled = 0;
        try
        {
            while (_events.Count > 0)
            {
                var e = _events[0];
                _events.RemoveAt(0);
                if (++handled > MaxEventsPerStep)
                {
                    EventChainCuts++;
                    _sim.Emit(SimEventType.Diagnostic, LogLevel.Normal, $"За тик больше {MaxEventsPerStep} событий сценария — остальные отброшены (цикл событий?)");
                    _events.Clear();
                    break;
                }
                if (e.Depth > MaxTriggerDepth)
                {
                    EventChainCuts++;
                    _sim.Emit(SimEventType.Diagnostic, LogLevel.Normal, $"Цепочка событий глубже {MaxTriggerDepth}: «{e.Type}» не обработано (цикл событий?)");
                    continue;
                }
                var subject = e.Unit >= 0 ? _sim.GetUnit(e.Unit) : null;
                for (int i = 0; i < Def.Triggers.Count; i++)
                {
                    var tr = Def.Triggers[i];
                    if (tr.Event != e.Type || !Matches(tr, e, subject) || !CanFire(i, tr)) continue;
                    if (!Evaluate(tr.Condition, subject)) continue;
                    _eventDepth = e.Depth;
                    try
                    {
                        Fire(i, tr, subject, $"событие «{Caption(e.Type)}»" + (subject != null ? $" ({subject.Name})" : e.Object != "" ? $" ({e.Object})" : ""));
                    }
                    finally
                    {
                        _eventDepth = -1;
                    }
                }
            }
        }
        finally
        {
            _processing = false;
        }
    }

    private static string Caption(ScenarioEventType t) => EdUtil.EnumLabel(t);

    /// <summary>The filters of a trigger: object (or ID), wave, group, unit.</summary>
    private bool Matches(TriggerDef tr, ScenarioEvent e, Unit? u)
    {
        if (tr.EventObject != "")
        {
            string obj = e.Object != "" ? e.Object : u == null ? "" : u.IsStructure ? u.ObjectId : u.SpawnBase;
            if (obj != tr.EventObject) return false;
        }
        if (tr.EventWave > 0 && (e.Wave >= 0 ? e.Wave : u?.Wave ?? -1) != tr.EventWave - 1) return false;
        if (tr.EventGroup != "" && (e.Group != "" ? e.Group : u?.Group ?? "") != tr.EventGroup) return false;
        if (tr.EventUnit != "" && (u == null || (u.Tag != tr.EventUnit && !_sim.Conditions.TypeMatches(u, tr.EventUnit)))) return false;
        return true;
    }
}
