using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

public enum WaveState
{
    Pending,
    Scheduled,
    Active,
    Cleared,
    Cancelled,
}

/// <summary>One planned appearance of a wave enemy (its warning line is planned for the same tick).</summary>
public sealed class PlannedSpawn
{
    public int Id { get; init; }
    public int Wave { get; init; }
    public UnitDef Def { get; init; } = null!;
    public string Marker { get; set; } = "";
    public HexCoord From { get; set; }
    public long Tick { get; set; }
    public string Tag { get; init; } = "";
    public string AiProfile { get; init; } = "";
    public bool Postponed { get; set; }
    public bool Done { get; set; }
}

public sealed class WaveRuntime
{
    public int Index { get; init; }
    public WaveDef Def { get; init; } = null!;
    public WaveState State { get; set; } = WaveState.Pending;
    public List<PlannedSpawn> Planned { get; } = new();
    public List<int> UnitIds { get; } = new();
    public long StartedTick { get; set; } = -1;
    public long ClearedTick { get; set; } = -1;
    public int Total => Planned.Count;
    public int Spawned => Planned.Count(p => p.Done);
}

public enum ObjectiveState
{
    Locked,
    Active,
    Completed,
    Failed,
}

public sealed class ObjectiveRuntime
{
    public ObjectiveDef Def { get; init; } = null!;
    public ObjectiveState State { get; set; } = ObjectiveState.Locked;
    public long ActivatedTick { get; set; } = -1;
    public long FinishedTick { get; set; } = -1;
    public int Leaks { get; set; }
    public double HoldSeconds { get; set; }
    public string Progress { get; set; } = "";
    public string Reason { get; set; } = "";
    public HashSet<int> LeakedUnits { get; } = new();
}

/// <summary>
/// Scenario runtime (docs/DECISIONS_2.md §37): initial units; waves scheduled by time, after the previous
/// wave, by a condition or by an action — with spawn points, intervals, routes, an alive limit, end
/// conditions and start/end actions; spawn warnings planned for every appearance; a condition tree
/// (and/or/not/in order and leaves); actions; objectives of every scenario type with dependencies,
/// timers and logic; boss phases through the same conditions and actions.
/// </summary>
public sealed class ScenarioSystem
{
    private readonly Simulation _sim;
    private readonly List<WaveRuntime> _waves = new();
    private readonly List<ObjectiveRuntime> _objectives = new();
    private readonly HashSet<int> _firedTriggers = new();
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly Dictionary<ConditionDef, long> _areaSince = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ConditionDef, int> _orderProgress = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, HashSet<int>> _bossPhases = new();
    private readonly HashSet<string> _taggedSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _flagTicks = new(StringComparer.Ordinal);
    private readonly Dictionary<ConditionDef, HashSet<int>> _areaInside = new(ReferenceEqualityComparer.Instance);
    /// <summary>AI rules: (unit, profile, rule index) → next allowed tick (long.MaxValue — fired once).</summary>
    private readonly Dictionary<(int Unit, AiProfileDef Profile, int Rule), long> _aiRules = new();
    private int _triggerDepth;

    /// <summary>Nested trigger activations deeper than this are cut (a cycle of «запустить триггер»).</summary>
    public const int MaxTriggerDepth = 8;
    public long TriggerCycleCuts { get; private set; }
    private bool _hadPlayerUnits;
    private int _nextSpawnId = 1;

    public ScenarioSystem(Simulation sim)
    {
        _sim = sim;
        Warnings = new SpawnWarningSystem(sim);
    }

    public ScenarioDef? Def { get; private set; }
    public bool ContinueAfterOutcome { get; set; }
    public SpawnWarningSystem Warnings { get; }
    public IReadOnlyList<WaveRuntime> Waves => _waves;
    public IReadOnlyList<ObjectiveRuntime> Objectives => _objectives;
    public IReadOnlyCollection<string> Flags => _flags;

    /// <summary>Waves that have started (at least one enemy appeared).</summary>
    public IReadOnlyCollection<int> SpawnedWaves => _waves.Where(w => w.StartedTick >= 0).Select(w => w.Index).ToList();

    public ObjectiveRuntime? Objective(string id) => _objectives.FirstOrDefault(o => o.Def.Id == id);

    /// <summary>Creates a simulation for a scenario (sides → player/AI factions, units placed).</summary>
    public static Simulation Create(ContentDatabase db, ScenarioDef sc, ulong? seed = null, bool admin = false)
    {
        var map = db.Get<MapDef>(sc.Map) ?? throw new InvalidOperationException($"Карта '{sc.Map}' не найдена");
        var player = new HashSet<string>(sc.Sides.Where(s => s.Controller == SideController.Player).Select(s => s.Faction));
        var ai = new HashSet<string>(sc.Sides.Where(s => s.Controller == SideController.Ai).Select(s => s.Faction));
        ulong s0 = seed ?? (sc.Seed != 0 ? sc.Seed : (ulong)DateTime.UtcNow.Ticks);
        var rules = db.Get<RulesPresetDef>(sc.RulesPreset)?.Rules ?? db.Rules;
        var sim = new Simulation(db, map, new SimOptions
        {
            Rules = rules,
            Seed = s0,
            PlayerFactions = player,
            AiFactions = ai,
            Stacking = sc.Limits.Stacking,
            FogOfWar = sc.Limits.FogOfWar && rules.Vision.FogOfWar,
            AdminMode = admin,
        });
        sim.Scenario.Load(sc);
        return sim;
    }

    public void Load(ScenarioDef sc)
    {
        Def = sc;
        foreach (var su in sc.Units)
        {
            var def = _sim.Content.Unit(su.Unit);
            if (def == null)
            {
                _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"Сценарий: шаблон юнита '{su.Unit}' не найден — пропущен");
                continue;
            }
            var cell = _sim.Map.Get(su.C);
            if (cell == null || !Occupancy.CanEnter(MoveContext.For(_sim, def, string.IsNullOrEmpty(su.Faction) ? null : su.Faction), cell))
            {
                _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"Сценарий: клетка {su.C} недоступна для '{su.Unit}' — пропущен");
                continue;
            }
            var u = _sim.Spawn(def, string.IsNullOrEmpty(su.Faction) ? null : su.Faction, su.C, su.Facing,
                string.IsNullOrEmpty(su.Name) ? null : su.Name, su.Tag, string.IsNullOrEmpty(su.AiProfile) ? null : su.AiProfile);
            if (!string.IsNullOrEmpty(u.Tag)) _taggedSeen.Add(u.Tag);
        }
        _hadPlayerUnits = _sim.Units.Any(u => _sim.IsPlayerFaction(u.FactionId));
        for (int i = 0; i < sc.Waves.Count; i++) _waves.Add(new WaveRuntime { Index = i, Def = sc.Waves[i] });
        foreach (var o in sc.Objectives.Select((d, i) => (d, i)).OrderBy(x => x.d.Priority).ThenBy(x => x.i))
            _objectives.Add(new ObjectiveRuntime { Def = o.d });
        // Waves by time are known in advance: their warning lines start before the first enemy appears.
        foreach (var w in _waves)
            if (w.Def.Start == WaveStart.Time)
                Schedule(w, _sim.TicksFor(w.Def.Time), lead: false);
        if (!string.IsNullOrWhiteSpace(sc.Intro)) _sim.Emit(SimEventType.Message, LogLevel.Normal, sc.Intro);
        _sim.Vision.Recompute();
    }

    // ------------------------------------------------------------------ waves

    private IEnumerable<(WaveUnitDef Unit, UnitDef Def)> WaveUnits(WaveDef w)
    {
        foreach (var wu in w.Units)
        {
            var def = _sim.Content.Unit(wu.Unit);
            if (def == null) continue;
            for (int k = 0; k < wu.Count; k++) yield return (wu, def);
        }
    }

    /// <summary>
    /// Plans every appearance of a wave from a base tick: interval between enemies, spawn points in turn.
    /// Event-driven waves (lead = true) are delayed by the warning time so the line can travel its path.
    /// </summary>
    private void Schedule(WaveRuntime w, long baseTick, bool lead)
    {
        if (w.State is WaveState.Cancelled or WaveState.Cleared || w.Planned.Count > 0) return;
        var markers = w.Def.AllSpawnMarkers().Select(id => _sim.Map.Def.FindMarker(id)).Where(m => m != null).Select(m => m!).ToList();
        if (markers.Count == 0)
        {
            _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"Волна «{w.Def.Name}»: нет точки появления");
            w.State = WaveState.Cancelled;
            return;
        }
        var list = WaveUnits(w.Def).ToList();
        if (lead && list.Count > 0)
        {
            double maxLead = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var m = markers[i % markers.Count];
                maxLead = Math.Max(maxLead, Warnings.LeadSeconds(w.Def, list[i].Def, w.Def.Faction, m.C, m.Id));
            }
            baseTick += _sim.TicksFor(maxLead);
        }
        for (int i = 0; i < list.Count; i++)
        {
            var m = markers[i % markers.Count];
            var p = new PlannedSpawn
            {
                Id = _nextSpawnId++, Wave = w.Index, Def = list[i].Def, Marker = m.Id, From = m.C,
                Tick = baseTick + _sim.TicksFor(w.Def.Interval * i), Tag = list[i].Unit.Tag, AiProfile = list[i].Unit.AiProfile,
            };
            w.Planned.Add(p);
            Warnings.Plan(p.Id, w.Index, w.Def, p.Def, w.Def.Faction, p.Marker, p.From, p.Tick);
        }
        w.State = WaveState.Scheduled;
        _sim.Emit(SimEventType.Info, LogLevel.Detail, $"Волна «{w.Def.Name}» запланирована: {list.Count} врагов через {(baseTick - _sim.Tick) * _sim.Dt:0.#} с");
    }

    /// <summary>Starts a wave by an action: with a warning delay (event waves).</summary>
    public void StartWave(int index)
    {
        if (index < 0 || index >= _waves.Count) return;
        var w = _waves[index];
        if (w.State != WaveState.Pending) return;
        Schedule(w, _sim.Tick + _sim.TicksFor(w.Def.Delay), lead: true);
    }

    /// <summary>Spawns the whole wave right now (immediate API: tests, admin tools).</summary>
    public void SpawnWave(int index)
    {
        if (index < 0 || index >= _waves.Count) return;
        var w = _waves[index];
        if (w.State == WaveState.Pending) Schedule(w, _sim.Tick, lead: false);
        foreach (var p in w.Planned.Where(p => !p.Done)) p.Tick = Math.Min(p.Tick, _sim.Tick);
        ProcessSpawns(w, ignoreLimit: true);
    }

    public void CancelWave(int index)
    {
        if (index < 0 || index >= _waves.Count) return;
        var w = _waves[index];
        if (w.State is WaveState.Cleared or WaveState.Cancelled) return;
        foreach (var p in w.Planned.Where(p => !p.Done)) p.Done = true;
        Warnings.RemoveWave(index);
        w.State = WaveState.Cancelled;
        _sim.Emit(SimEventType.Info, LogLevel.Normal, $"Волна «{w.Def.Name}» отменена");
    }

    private int AliveOf(WaveRuntime w) => w.UnitIds.Count(id => _sim.GetUnit(id)?.Alive == true);

    private void ProcessSpawns(WaveRuntime w, bool ignoreLimit = false)
    {
        foreach (var p in w.Planned)
        {
            if (p.Done || _sim.Tick < p.Tick) continue;
            if (!ignoreLimit && w.Def.MaxAlive > 0 && AliveOf(w) >= w.Def.MaxAlive)
            {
                // No free slot: the appearance waits; its line disappears and is planned again when a slot frees.
                if (!p.Postponed)
                {
                    p.Postponed = true;
                    Warnings.Remove(p.Id);
                }
                continue;
            }
            SpawnOne(w, p);
        }
    }

    private void SpawnOne(WaveRuntime w, PlannedSpawn p)
    {
        p.Done = true;
        Warnings.Remove(p.Id);
        var cell = FindFreeCell(p.From, p.Def, 4);
        if (cell == null)
        {
            _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"Волна «{w.Def.Name}»: нет свободной клетки у {p.From}");
            return;
        }
        var route = string.IsNullOrEmpty(w.Def.Route) ? null : _sim.Map.Def.FindRoute(w.Def.Route);
        var first = route != null && route.Points.Count > 0 ? route.Points[0] : MapCenter();
        var facing = cell.Value.DirectionTo(first == cell.Value && route != null && route.Points.Count > 1 ? route.Points[1] : first) ?? HexDirection.W;
        Unit u;
        try
        {
            u = _sim.Spawn(p.Def, w.Def.Faction, cell.Value, facing, null, string.IsNullOrEmpty(p.Tag) ? null : p.Tag,
                string.IsNullOrEmpty(p.AiProfile) ? null : p.AiProfile);
        }
        catch (InvalidOperationException e)
        {
            _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, e.Message);
            return;
        }
        u.Wave = w.Index;
        if (!string.IsNullOrEmpty(u.Tag)) _taggedSeen.Add(u.Tag);
        if (route != null && route.Points.Count > 0)
        {
            u.Route = new List<HexCoord>(route.Points);
            u.RouteIndex = route.Points[0] == u.Pos ? 1 : 0;
        }
        w.UnitIds.Add(u.Id);
        if (w.StartedTick < 0)
        {
            w.StartedTick = _sim.Tick;
            w.State = WaveState.Active;
            string msg = string.IsNullOrWhiteSpace(w.Def.Message) ? $"Волна «{w.Def.Name}»: {w.Total} противников" : w.Def.Message;
            _sim.Emit(SimEventType.WaveStarted, LogLevel.Normal, msg, cell: p.From);
            if (_sim.Rules.Autopause.WaveStarted) _sim.RequestAutopause("началась волна");
            foreach (var a in w.Def.OnStart) Execute(a, w.Def.Name, null);
        }
    }

    private void UpdateWaves()
    {
        foreach (var w in _waves)
        {
            switch (w.State)
            {
                case WaveState.Pending:
                    if (w.Def.Start == WaveStart.AfterPrevious && w.Index > 0 && _waves[w.Index - 1].State is WaveState.Cleared or WaveState.Cancelled)
                        Schedule(w, _sim.Tick + _sim.TicksFor(w.Def.Delay), lead: true);
                    else if (w.Def.Start == WaveStart.AfterPrevious && w.Index == 0)
                        Schedule(w, _sim.Tick + _sim.TicksFor(w.Def.Delay), lead: true);
                    else if (w.Def.Start == WaveStart.OnCondition && Evaluate(w.Def.StartCondition))
                        Schedule(w, _sim.Tick + _sim.TicksFor(w.Def.Delay), lead: true);
                    break;
                case WaveState.Scheduled:
                case WaveState.Active:
                {
                    // Postponed appearances get a new time (and a new line) as soon as a slot frees.
                    if (w.Def.MaxAlive > 0 && AliveOf(w) < w.Def.MaxAlive)
                        foreach (var p in w.Planned.Where(p => !p.Done && p.Postponed).Take(w.Def.MaxAlive - AliveOf(w)))
                        {
                            p.Postponed = false;
                            p.Tick = _sim.Tick + _sim.TicksFor(Warnings.LeadSeconds(w.Def, p.Def, w.Def.Faction, p.From, p.Marker));
                            Warnings.Plan(p.Id, w.Index, w.Def, p.Def, w.Def.Faction, p.Marker, p.From, p.Tick);
                        }
                    ProcessSpawns(w);
                    if (w.State == WaveState.Active && w.Planned.All(p => p.Done))
                    {
                        bool cleared = w.Def.CustomEnd ? Evaluate(w.Def.EndCondition) : AliveOf(w) == 0;
                        if (cleared)
                        {
                            w.State = WaveState.Cleared;
                            w.ClearedTick = _sim.Tick;
                            _sim.Emit(SimEventType.Info, LogLevel.Normal, $"Волна «{w.Def.Name}» отражена");
                            foreach (var a in w.Def.OnEnd) Execute(a, w.Def.Name, null);
                        }
                    }
                    break;
                }
            }
        }
    }

    private HexCoord MapCenter()
    {
        if (_sim.Map.Count == 0) return HexCoord.Zero;
        double q = 0, r = 0;
        foreach (var c in _sim.Map.Cells)
        {
            q += c.Coord.Q;
            r += c.Coord.R;
        }
        return HexCoord.Round(q / _sim.Map.Count, r / _sim.Map.Count);
    }

    private HexCoord? FindFreeCell(HexCoord center, UnitDef def, int radius)
    {
        var mc = MoveContext.For(_sim, def, null);
        foreach (var c in HexCoord.Spiral(center, radius))
        {
            var cell = _sim.Map.Get(c);
            if (cell == null || !cell.AllowsPlacement) continue;
            if (MovementRules.Check(mc, null, cell, false, out _) != StepCheck.Ok) continue;
            return c;
        }
        return null;
    }

    // ------------------------------------------------------------------ tick

    public void Update()
    {
        if (Def == null) return;
        UpdateWaves();
        Warnings.Update();
        for (int i = 0; i < Def.Triggers.Count; i++)
        {
            var tr = Def.Triggers[i];
            if (tr.Once && _firedTriggers.Contains(i)) continue;
            if (!Evaluate(tr.Condition)) continue;
            _firedTriggers.Add(i);
            foreach (var a in tr.Actions) Execute(a, tr.Name, null);
            if (!tr.Once) _firedTriggers.Remove(i);
        }
        UpdateLeaks();
        UpdateObjectives();
        UpdateBosses();
        UpdateAiRules();
    }

    /// <summary>Sets a scenario flag (event); flags remember when they were set (for «через N с после события»).</summary>
    public void SetFlag(string flag)
    {
        if (string.IsNullOrEmpty(flag) || !_flags.Add(flag)) return;
        _flagTicks[flag] = _sim.Tick;
        _sim.Emit(SimEventType.Info, LogLevel.Detail, $"Событие «{flag}»");
    }

    /// <summary>
    /// «Запустить триггер»: runs the actions of the named trigger now (a once-only trigger runs once).
    /// Nested activations are depth-limited, so a cycle of triggers cannot hang the battle.
    /// </summary>
    public bool ActivateTrigger(string name, Unit? actor)
    {
        if (Def == null) return false;
        int i = Def.Triggers.FindIndex(t => t.Name == name);
        if (i < 0)
        {
            _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"Триггер «{name}» не найден");
            return false;
        }
        var tr = Def.Triggers[i];
        if (tr.Once && _firedTriggers.Contains(i)) return false;
        if (_triggerDepth >= MaxTriggerDepth)
        {
            TriggerCycleCuts++;
            _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"Цепочка триггеров глубже {MaxTriggerDepth} — «{name}» не запущен (цикл?)");
            return false;
        }
        _triggerDepth++;
        try
        {
            if (tr.Once) _firedTriggers.Add(i);
            foreach (var a in tr.Actions) Execute(a, tr.Name, actor);
        }
        finally
        {
            _triggerDepth--;
        }
        return true;
    }

    /// <summary>Behaviour rules of AI profiles: condition (self = the unit) → actions (actor = the unit).</summary>
    private void UpdateAiRules()
    {
        foreach (var u in _sim.Units.ToList())
        {
            var p = u.AiProfile;
            if (!u.Alive || p == null || p.Rules.Count == 0) continue;
            for (int i = 0; i < p.Rules.Count; i++)
            {
                var r = p.Rules[i];
                var key = (u.Id, p, i);
                if (_aiRules.TryGetValue(key, out var next) && next > _sim.Tick) continue;
                if (!Evaluate(r.When, u)) continue;
                _aiRules[key] = r.Once ? long.MaxValue : _sim.Tick + Math.Max(1, _sim.TicksFor(r.Cooldown));
                _sim.Emit(SimEventType.AiDecision, LogLevel.Detail, $"{u.Name}: правило «{r.Name}»", u.Id);
                foreach (var a in r.Actions) Execute(a, $"{u.Name}: {r.Name}", u);
                if (!u.Alive || u.AiProfile != p) break;
            }
        }
    }

    // ------------------------------------------------------------------ actions

    private IEnumerable<Unit> Targets(TriggerActionDef a, Unit? actor)
    {
        switch (a.Target)
        {
            case ActionTarget.Self:
                if (actor != null && actor.Alive) yield return actor;
                break;
            case ActionTarget.UnitTag:
                foreach (var u in _sim.Units.Where(u => u.Alive && u.Tag == a.UnitTag).ToList()) yield return u;
                break;
            case ActionTarget.Enemies:
                foreach (var u in _sim.Units.Where(u => u.Alive && IsPlayerEnemy(u)).ToList()) yield return u;
                break;
            case ActionTarget.PlayerUnits:
                foreach (var u in _sim.Units.Where(u => u.Alive && _sim.IsPlayerFaction(u.FactionId)).ToList()) yield return u;
                break;
        }
    }

    private IEnumerable<Cell> AreaCells(TriggerActionDef a, Unit? actor)
    {
        var m = string.IsNullOrEmpty(a.Marker) ? null : _sim.Map.Def.FindMarker(a.Marker);
        HexCoord? center = m?.C ?? actor?.Pos;
        if (center == null) return Enumerable.Empty<Cell>();
        return _sim.Map.InRadius(center.Value, Math.Max(m?.Radius ?? 0, a.Radius));
    }

    /// <summary>Executes an action of a trigger, a wave or a boss phase (actor — the boss or null).</summary>
    public void Execute(TriggerActionDef a, string source, Unit? actor)
    {
        switch (a.Type)
        {
            case TriggerActionType.Message:
                _sim.Emit(SimEventType.Message, LogLevel.Normal, string.IsNullOrEmpty(a.Text) ? source : a.Text);
                break;
            case TriggerActionType.SpawnWave:
                StartWave(a.Wave);
                break;
            case TriggerActionType.CancelWave:
                CancelWave(a.Wave);
                break;
            case TriggerActionType.Autopause:
                _sim.RequestAutopause(string.IsNullOrEmpty(a.Text) ? source : a.Text);
                break;
            case TriggerActionType.Victory:
                SetOutcome(BattleOutcome.Victory, string.IsNullOrEmpty(a.Text) ? source : a.Text);
                break;
            case TriggerActionType.Defeat:
                SetOutcome(BattleOutcome.Defeat, string.IsNullOrEmpty(a.Text) ? source : a.Text);
                break;
            case TriggerActionType.AddCellEffect:
            {
                var def = _sim.Content.CellEffect(a.CellEffect);
                if (def == null) break;
                var m = _sim.Map.Def.FindMarker(a.Marker);
                var cells = m != null ? _sim.Map.InRadius(m.C, m.Radius) : AreaCells(a, actor);
                foreach (var cell in cells.ToList()) _sim.CellEffects.Add(cell, def, 1, 0, actor?.Id ?? -1, 0, 0, EffectOrigin.Scenario);
                break;
            }
            case TriggerActionType.SetFlag:
                SetFlag(a.Flag);
                break;
            case TriggerActionType.CompleteObjective:
                Finish(Objective(a.Objective), ObjectiveState.Completed, source);
                break;
            case TriggerActionType.FailObjective:
                Finish(Objective(a.Objective), ObjectiveState.Failed, source);
                break;
            case TriggerActionType.ChangeTerrain:
            {
                var t = _sim.Content.Terrain(a.Terrain);
                if (t == null) break;
                foreach (var cell in AreaCells(a, actor).ToList())
                    if (actor == null || cell.Coord != actor.Pos || t.IsPassableFor(actor.Def.MoveCategory))
                        _sim.CellEffects.ChangeTerrain(cell, t, source);
                break;
            }
            case TriggerActionType.SummonUnits:
            {
                var def = _sim.Content.Unit(a.Unit);
                var center = (string.IsNullOrEmpty(a.Marker) ? null : _sim.Map.Def.FindMarker(a.Marker))?.C ?? actor?.Pos;
                if (def == null || center == null) break;
                for (int i = 0; i < Math.Max(1, a.Count); i++)
                {
                    var c = FindFreeCell(center.Value, def, Math.Max(2, a.Radius + 2));
                    if (c == null) break;
                    var u = _sim.Spawn(def, actor?.FactionId ?? def.Faction, c.Value, actor?.Facing ?? HexDirection.W);
                    if (actor != null) u.SummonerId = actor.Id;
                }
                _sim.Emit(SimEventType.Info, LogLevel.Normal, $"{actor?.Name ?? source}: призыв — {def.DisplayName} ×{a.Count}", actor?.Id ?? -1);
                break;
            }
            case TriggerActionType.ApplyStatus:
            {
                var sd = _sim.Content.Status(a.Status);
                if (sd == null) break;
                foreach (var u in Targets(a, actor)) _sim.Statuses.Apply(u, sd, a.Amount, 0, actor?.Id ?? -1, force: true);
                break;
            }
            case TriggerActionType.RemoveStatus:
                foreach (var u in Targets(a, actor)) _sim.Statuses.RemoveById(u, a.Status, source);
                break;
            case TriggerActionType.AddSkill:
            {
                var s = _sim.Content.Skill(a.Skill);
                if (s == null) break;
                foreach (var u in Targets(a, actor))
                    if (!u.Skills.Contains(s))
                    {
                        u.Skills.Add(s);
                        _sim.Emit(SimEventType.Info, LogLevel.Normal, $"{u.Name}: новый навык «{s.DisplayName}»", u.Id);
                    }
                break;
            }
            case TriggerActionType.RemoveSkill:
                foreach (var u in Targets(a, actor)) u.Skills.RemoveAll(s => s.Id == a.Skill);
                break;
            case TriggerActionType.SetAutoAttackSkill:
                foreach (var u in Targets(a, actor)) _sim.AutoAttacks.SetSkill(u, a.AutoAttack, a.Skill);
                break;
            case TriggerActionType.SetAiProfile:
            {
                var p = _sim.Content.Get<AiProfileDef>(a.AiProfile);
                if (p == null) break;
                foreach (var u in Targets(a, actor)) u.AiProfile = p;
                break;
            }
            case TriggerActionType.HealPercent:
                foreach (var u in Targets(a, actor)) _sim.Combat.Heal(null, u, a.Amount, source, HealMode.PercentMax, applyPower: false);
                break;
            case TriggerActionType.Effects:
            {
                // The whole catalog of skill operations, applied to the chosen units (global ones once).
                var unitOps = a.Effects.Where(e => !e.IsGlobal).ToList();
                foreach (var e in a.Effects.Where(e => e.IsGlobal)) _sim.Combat.ApplyGlobal(e, actor, actor?.Pos ?? default, source);
                if (unitOps.Count > 0)
                    foreach (var u in Targets(a, actor)) _sim.Combat.ApplyDirect(actor, u, unitOps, 1.0, source, DamageSource.Scenario);
                break;
            }
            case TriggerActionType.SetRoute:
            {
                var route = _sim.Map.Def.Routes.FirstOrDefault(r => r.Id == a.Route);
                if (route == null) break;
                foreach (var u in Targets(a, actor))
                {
                    u.Route = new List<HexCoord>(route.Points);
                    u.RouteIndex = 0;
                }
                break;
            }
            case TriggerActionType.DestroyObstacle:
                foreach (var cell in AreaCells(a, actor).ToList())
                    if (cell.Terrain.DestructibleHp > 0)
                        _sim.CellEffects.DamageObstacle(cell, int.MaxValue / 2, actor, source);
                break;
            case TriggerActionType.ActivateTrigger:
                ActivateTrigger(a.Text, actor);
                break;
            case TriggerActionType.SetBossPhase:
                foreach (var u in Targets(a, actor)) ForcePhase(u, a.Phase - 1, source);
                break;
            case TriggerActionType.PlayVfx:
            case TriggerActionType.PlaySound:
            {
                var at = (string.IsNullOrEmpty(a.Marker) ? null : _sim.Map.Def.FindMarker(a.Marker))?.C ?? actor?.Pos;
                _sim.Emit(a.Type == TriggerActionType.PlayVfx ? SimEventType.Vfx : SimEventType.Sound, LogLevel.Diagnostic, a.Text, actor?.Id ?? -1, cell: at);
                break;
            }
        }
    }

    /// <summary>«Сменить фазу босса»: the phase starts now (its actions run once), regardless of its condition.</summary>
    private void ForcePhase(Unit u, int index, string source)
    {
        var phases = u.Def.Boss.Phases;
        if (!u.Def.Boss.IsBoss || index < 0 || index >= phases.Count) return;
        if (!_bossPhases.TryGetValue(u.Id, out var fired)) _bossPhases[u.Id] = fired = new HashSet<int>();
        if (!fired.Add(index)) return;
        var ph = phases[index];
        _sim.Emit(SimEventType.Message, LogLevel.Normal, string.IsNullOrEmpty(ph.Message) ? $"{u.Name}: фаза «{ph.Name}»" : ph.Message, u.Id);
        foreach (var act in ph.Actions) Execute(act, $"{u.Name}: {ph.Name}", u);
    }

    /// <summary>Units a condition is about: the tagged ones, or the owner (self) without a tag.</summary>
    private IEnumerable<Unit> Subjects(ConditionDef c, Unit? self)
    {
        if (string.IsNullOrEmpty(c.UnitTag))
        {
            if (self != null && self.Alive) yield return self;
            yield break;
        }
        foreach (var u in _sim.Units)
            if (u.Alive && u.Tag == c.UnitTag)
                yield return u;
    }

    private bool SideMatches(AreaSide side, Unit u) =>
        side == AreaSide.Any || (side == AreaSide.Player ? _sim.IsPlayerFaction(u.FactionId) : IsPlayerEnemy(u));

    private static bool InMarker(MapMarkerDef? m, HexCoord c) => m == null || HexCoord.Distance(c, m.C) <= m.Radius;

    // ------------------------------------------------------------------ conditions

    private bool IsPlayerEnemy(Unit u)
    {
        foreach (var pf in _sim.Options.PlayerFactions)
            if (_sim.Content.RelationOf(pf, u.FactionId) == Relation.Enemy)
                return true;
        return false;
    }

    private IEnumerable<HexCoord> MarkerCells(string id)
    {
        var m = _sim.Map.Def.FindMarker(id);
        if (m == null) return Enumerable.Empty<HexCoord>();
        return HexCoord.Spiral(m.C, m.Radius).Where(_sim.Map.Contains);
    }

    private static bool CategoryMatch(List<string> cats, Unit u)
    {
        if (cats.Count == 0) return true;
        foreach (var c in cats)
        {
            if (c == u.Def.CreatureCategory || c == u.Def.Id || c == u.Tag) return true;
            if (c.StartsWith("tag:", StringComparison.Ordinal) && (u.Def.Tags.Contains(c[4..]) || u.Tag == c[4..])) return true;
        }
        return false;
    }

    /// <summary>All waves have started (or are cancelled) — the field cannot get new enemies by waves.</summary>
    private bool WavesDone => _waves.All(w => w.State is WaveState.Cancelled or WaveState.Cleared || w.State == WaveState.Active && w.Planned.All(p => p.Done));

    public bool Evaluate(ConditionDef c) => Evaluate(c, null);

    /// <summary>Evaluates a condition; self — the owner for boss phases ("здоровье ниже" without a tag).</summary>
    public bool Evaluate(ConditionDef c, Unit? self)
    {
        switch (c.Type)
        {
            case ConditionType.All:
                return c.Children.Count == 0 || c.Children.All(x => Evaluate(x, self));
            case ConditionType.Any:
                return c.Children.Any(x => Evaluate(x, self));
            case ConditionType.Not:
                return !(c.Children.Count > 0 && c.Children.All(x => Evaluate(x, self)));
            case ConditionType.InOrder:
            {
                // Each child latches in turn: B only counts after A has been met.
                _orderProgress.TryGetValue(c, out int i);
                while (i < c.Children.Count && Evaluate(c.Children[i], self)) i++;
                _orderProgress[c] = i;
                return i >= c.Children.Count;
            }
            case ConditionType.Always:
                return true;
            case ConditionType.AllEnemiesDead:
                return WavesDone && !_sim.Units.Any(u => u.Alive && IsPlayerEnemy(u));
            case ConditionType.AllPlayerUnitsDead:
                return _hadPlayerUnits && !_sim.Units.Any(u => u.Alive && _sim.IsPlayerFaction(u.FactionId));
            case ConditionType.SurviveTime:
            case ConditionType.TimeElapsed:
                return _sim.Time >= c.Seconds;
            case ConditionType.ReachMarker:
            {
                var m = _sim.Map.Def.FindMarker(c.Marker);
                if (m == null) return false;
                return _sim.Units.Any(u => u.Alive && _sim.IsPlayerFaction(u.FactionId) && (string.IsNullOrEmpty(c.UnitTag) || u.Tag == c.UnitTag)
                                           && HexCoord.Distance(u.Pos, m.C) <= m.Radius);
            }
            case ConditionType.UnitInArea:
            {
                var m = _sim.Map.Def.FindMarker(c.Marker);
                bool inside = m != null && _sim.Units.Any(u => u.Alive && HexCoord.Distance(u.Pos, m.C) <= m.Radius
                    && (string.IsNullOrEmpty(c.UnitTag) || u.Tag == c.UnitTag)
                    && (c.Side == AreaSide.Any || (c.Side == AreaSide.Player ? _sim.IsPlayerFaction(u.FactionId) : IsPlayerEnemy(u))));
                if (!inside)
                {
                    _areaSince.Remove(c);
                    return false;
                }
                if (!_areaSince.TryGetValue(c, out var since)) _areaSince[c] = since = _sim.Tick;
                return (_sim.Tick - since) * _sim.Dt + 1e-9 >= c.Seconds;
            }
            case ConditionType.UnitDead:
                return _sim.Units.Any(u => u.Tag == c.UnitTag && !u.Alive);
            case ConditionType.UnitAlive:
                return _sim.Units.Any(u => u.Tag == c.UnitTag && u.Alive);
            case ConditionType.KillCount:
            {
                int kills = 0;
                foreach (var (faction, victim) in _sim.Combat.KillLog)
                    if (faction.Length > 0 && (_sim.IsPlayerFaction(faction) || _sim.Options.PlayerFactions.Any(pf => _sim.Content.RelationOf(pf, faction) == Relation.Ally))
                        && CategoryMatch(c.Categories, victim))
                        kills++;
                return kills >= c.Count;
            }
            case ConditionType.WaveSpawned:
                return c.Wave >= 0 && c.Wave < _waves.Count && _waves[c.Wave].StartedTick >= 0;
            case ConditionType.WaveCleared:
                return c.Wave >= 0 && c.Wave < _waves.Count && _waves[c.Wave].State == WaveState.Cleared;
            case ConditionType.WavesCleared:
                return _waves.Count(w => w.State == WaveState.Cleared) >= c.Count;
            case ConditionType.ObjectiveCompleted:
                return Objective(c.Objective)?.State == ObjectiveState.Completed;
            case ConditionType.ObjectiveFailed:
                return Objective(c.Objective)?.State == ObjectiveState.Failed;
            case ConditionType.FlagSet:
                return _flags.Contains(c.Flag);
            case ConditionType.HpBelow:
            {
                if (string.IsNullOrEmpty(c.UnitTag)) return self != null && self.Alive && self.HpFraction < c.Fraction;
                return _sim.Units.Any(u => u.Alive && u.Tag == c.UnitTag && u.HpFraction < c.Fraction);
            }
            case ConditionType.EnemiesLeaked:
            {
                int leaks = string.IsNullOrEmpty(c.Objective) ? _objectives.Sum(o => o.Leaks) : Objective(c.Objective)?.Leaks ?? 0;
                return leaks >= c.Count;
            }
            case ConditionType.TargetInRange:
                return Subjects(c, self).Any(s => _sim.Units.Any(o => o.Alive && o != s && _sim.Content.RelationOf(s.FactionId, o.FactionId) == Relation.Enemy
                                                                       && HexCoord.Distance(s.Pos, o.Pos) <= Math.Max(0, c.Count)));
            case ConditionType.EnemySpotted:
            {
                bool playerSees = _sim.Units.Any(u => u.Alive && IsPlayerEnemy(u) && _sim.Vision.IsVisibleToPlayer(u));
                bool enemiesSee = _sim.Units.Any(u => u.Alive && _sim.IsPlayerFaction(u.FactionId)
                                                      && _sim.Units.Any(e => e.Alive && IsPlayerEnemy(e) && _sim.Vision.CanSee(e, u)));
                return c.Side switch
                {
                    AreaSide.Enemy => enemiesSee,
                    AreaSide.Any => playerSees || enemiesSee,
                    _ => playerSees,
                };
            }
            case ConditionType.UnitLeftArea:
            {
                // True on the tick a unit that was inside has left (moved out, not died).
                var m = _sim.Map.Def.FindMarker(c.Marker);
                if (m == null) return false;
                var now = new HashSet<int>(_sim.Units.Where(u => u.Alive && HexCoord.Distance(u.Pos, m.C) <= m.Radius && SideMatches(c.Side, u)
                                                                  && (string.IsNullOrEmpty(c.UnitTag) || u.Tag == c.UnitTag)).Select(u => u.Id));
                bool left = _areaInside.TryGetValue(c, out var before) && before.Any(id => !now.Contains(id) && _sim.GetUnit(id)?.Alive == true);
                _areaInside[c] = now;
                return left;
            }
            case ConditionType.ResourceReached:
                return Subjects(c, self).Any(u => u.Resources.TryGetValue(c.Resource, out var r)
                                                  && (c.Compare == CompareMode.AtLeast ? r.Current + 1e-9 >= c.Value : r.Current <= c.Value + 1e-9));
            case ConditionType.HasStatus:
                return Subjects(c, self).Any(u => u.FindStatus(c.Status) is { } st && (st.Def.Kind == StatusKind.Accumulating ? st.Points : st.Stacks) + 1e-9 >= c.Value);
            case ConditionType.FlagTimer:
                return _flagTicks.TryGetValue(c.Flag, out var at) && (_sim.Tick - at) * _sim.Dt + 1e-9 >= c.Seconds;
            case ConditionType.ObstacleDestroyed:
            {
                var m = string.IsNullOrEmpty(c.Marker) ? null : _sim.Map.Def.FindMarker(c.Marker);
                return _sim.CellEffects.DestroyedObstacles.Count(x => InMarker(m, x)) >= Math.Max(1, c.Count);
            }
            case ConditionType.BossPhase:
            {
                IEnumerable<Unit> bosses = string.IsNullOrEmpty(c.UnitTag)
                    ? self != null && self.Def.Boss.IsBoss ? new[] { self } : _sim.Units.Where(u => u.Def.Boss.IsBoss)
                    : _sim.Units.Where(u => u.Tag == c.UnitTag);
                int need = Math.Max(1, c.Count) - 1;
                return bosses.Any(b => _bossPhases.TryGetValue(b.Id, out var f) && f.Any(i => i >= need));
            }
            case ConditionType.SynergyOccurred:
            {
                var m = string.IsNullOrEmpty(c.Marker) ? null : _sim.Map.Def.FindMarker(c.Marker);
                return _sim.CellEffects.SynergyLog.Count(x => (string.IsNullOrEmpty(c.Synergy) || x.Id == c.Synergy) && InMarker(m, x.Cell)) >= Math.Max(1, c.Count);
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ objectives

    private IEnumerable<HexCoord> ObjectiveCells(ObjectiveDef d)
    {
        foreach (var c in d.Cells) yield return c;
        if (!string.IsNullOrEmpty(d.Marker))
            foreach (var c in MarkerCells(d.Marker))
                yield return c;
    }

    /// <summary>Cells to show on the map for an objective (defence hexes, capture zone, escort goal).</summary>
    public List<HexCoord> CellsOf(ObjectiveRuntime o) => ObjectiveCells(o.Def).Distinct().ToList();

    /// <summary>Enemies entering defended hexes count as leaks (only the given categories), and may be removed.</summary>
    private void UpdateLeaks()
    {
        foreach (var o in _objectives)
        {
            if (o.Def.Kind != ObjectiveKind.Defend || o.State != ObjectiveState.Active) continue;
            var cells = new HashSet<HexCoord>(ObjectiveCells(o.Def));
            if (cells.Count == 0) continue;
            foreach (var u in _sim.Units.ToList())
            {
                if (!u.Alive || !IsPlayerEnemy(u) || u.StepTo.HasValue || !cells.Contains(u.Pos) || o.LeakedUnits.Contains(u.Id)) continue;
                if (!CategoryMatch(o.Def.CountCategories, u)) continue;
                o.LeakedUnits.Add(u.Id);
                o.Leaks++;
                _sim.Emit(SimEventType.Info, LogLevel.Normal, $"{u.Name} прорвался к «{o.Def.Name}» ({o.Leaks}/{o.Def.AllowedLeaks} допустимо)", u.Id, cell: u.Pos);
                if (o.Def.RemoveLeaked)
                {
                    u.DeathCause = DeathCause.Removed;
                    _sim.Combat.Kill(u, null, "прорвался");
                }
            }
        }
    }

    private bool Deps(ObjectiveRuntime o)
    {
        foreach (var d in o.Def.DependsOn)
            if (Objective(d)?.State != ObjectiveState.Completed)
                return false;
        if (Def?.ObjectiveLogic == ObjectiveLogic.InOrder && o.Def.Required)
        {
            // Required objectives in order: the previous required one must be completed.
            var req = _objectives.Where(x => x.Def.Required).ToList();
            int i = req.IndexOf(o);
            if (i > 0 && req[i - 1].State != ObjectiveState.Completed) return false;
        }
        return true;
    }

    private void Finish(ObjectiveRuntime? o, ObjectiveState state, string reason)
    {
        if (o == null || o.State is ObjectiveState.Completed or ObjectiveState.Failed) return;
        if (o.ActivatedTick < 0) o.ActivatedTick = _sim.Tick;
        o.State = state;
        o.FinishedTick = _sim.Tick;
        o.Reason = reason;
        _sim.Emit(SimEventType.Info, LogLevel.Normal, state == ObjectiveState.Completed ? $"Цель выполнена: «{o.Def.Name}»" : $"Цель провалена: «{o.Def.Name}» — {reason}");
        if (_sim.Rules.Autopause.WaveStarted && o.Def.Required) _sim.RequestAutopause(state == ObjectiveState.Completed ? "цель выполнена" : "цель провалена");
    }

    private void UpdateObjectives()
    {
        foreach (var o in _objectives)
        {
            if (o.State == ObjectiveState.Locked)
            {
                if (!Deps(o)) continue;
                o.State = ObjectiveState.Active;
                o.ActivatedTick = _sim.Tick;
                if (o.Def.DependsOn.Count > 0 || Def?.ObjectiveLogic == ObjectiveLogic.InOrder)
                    _sim.Emit(SimEventType.Info, LogLevel.Normal, $"Новая цель: «{o.Def.Name}»");
            }
            if (o.State != ObjectiveState.Active) continue;
            var d = o.Def;
            double elapsed = (_sim.Tick - o.ActivatedTick) * _sim.Dt;
            bool timerSuccess = d.Kind is ObjectiveKind.Survive or ObjectiveKind.Prevent or ObjectiveKind.Defend;

            if (Evaluate(d.Fail))
            {
                Finish(o, ObjectiveState.Failed, d.Fail.Describe());
                continue;
            }
            bool extra = Evaluate(d.Complete);
            bool done = false;
            string fail = "";
            switch (d.Kind)
            {
                case ObjectiveKind.Eliminate:
                {
                    int left = _sim.Units.Count(u => u.Alive && IsPlayerEnemy(u));
                    o.Progress = $"врагов осталось: {left}";
                    done = WavesDone && left == 0;
                    break;
                }
                case ObjectiveKind.Defend:
                {
                    o.Progress = $"пропущено {o.Leaks}/{d.AllowedLeaks}";
                    if (o.Leaks > d.AllowedLeaks) fail = $"пропущено врагов: {o.Leaks}";
                    if (!string.IsNullOrEmpty(d.ProtectTag) && _sim.Units.Any(u => u.Tag == d.ProtectTag && !u.Alive)) fail = "защищаемый объект уничтожен";
                    done = d.TimeLimit <= 0 && WavesDone && _waves.Count > 0 && !_sim.Units.Any(u => u.Alive && IsPlayerEnemy(u));
                    break;
                }
                case ObjectiveKind.KillBoss:
                {
                    var bosses = _sim.Units.Where(u => string.IsNullOrEmpty(d.UnitTag) ? u.Def.Boss.IsBoss : u.Tag == d.UnitTag).ToList();
                    var alive = bosses.Where(u => u.Alive).ToList();
                    o.Progress = alive.Count > 0 ? $"здоровье босса {alive.Average(u => u.HpFraction):P0}" : bosses.Count == 0 ? "босс ещё не появился" : "";
                    done = bosses.Count > 0 && alive.Count == 0;
                    break;
                }
                case ObjectiveKind.Survive:
                {
                    double need = d.TimeLimit > 0 ? d.TimeLimit : d.Seconds;
                    o.Progress = $"осталось {Math.Max(0, need - elapsed):0} с";
                    done = elapsed + 1e-9 >= need;
                    break;
                }
                case ObjectiveKind.Waves:
                {
                    int need = d.WaveCount > 0 ? d.WaveCount : _waves.Count;
                    int cleared = _waves.Count(w => w.State == WaveState.Cleared);
                    o.Progress = $"волны {cleared}/{need}";
                    done = need > 0 && cleared >= need;
                    break;
                }
                case ObjectiveKind.Escort:
                {
                    var who = _sim.Units.Where(u => u.Tag == d.UnitTag).ToList();
                    if (who.Count > 0 && who.All(u => !u.Alive)) fail = "сопровождаемый погиб";
                    var goal = new HashSet<HexCoord>(ObjectiveCells(d));
                    var alive = who.FirstOrDefault(u => u.Alive);
                    o.Progress = alive != null && goal.Count > 0 ? $"до цели {goal.Min(g => HexCoord.Distance(g, alive.Pos))} кл." : "";
                    done = alive != null && goal.Contains(alive.Pos);
                    break;
                }
                case ObjectiveKind.Capture:
                {
                    var zone = new HashSet<HexCoord>(ObjectiveCells(d));
                    bool mine = _sim.Units.Any(u => u.Alive && _sim.IsPlayerFaction(u.FactionId) && zone.Contains(u.Pos));
                    bool theirs = _sim.Units.Any(u => u.Alive && IsPlayerEnemy(u) && zone.Contains(u.Pos));
                    if (mine && !theirs) o.HoldSeconds += _sim.Dt;
                    else if (theirs && !mine && d.ResetOnLoss) o.HoldSeconds = 0;
                    o.Progress = $"удержание {o.HoldSeconds:0}/{d.Seconds:0} с" + (mine && theirs ? " (оспаривается)" : "");
                    done = o.HoldSeconds + 1e-9 >= d.Seconds;
                    break;
                }
                case ObjectiveKind.Destroy:
                {
                    var targets = _sim.Units.Where(u => u.Tag == d.UnitTag).ToList();
                    int dead = targets.Count(u => !u.Alive);
                    int need = d.Count > 0 ? d.Count : targets.Count;
                    o.Progress = $"уничтожено {dead}/{need}";
                    done = need > 0 && dead >= need && _taggedSeen.Contains(d.UnitTag);
                    break;
                }
                case ObjectiveKind.Prevent:
                    o.Progress = d.TimeLimit > 0 ? $"осталось {Math.Max(0, d.TimeLimit - elapsed):0} с" : "не допустить";
                    break;
                case ObjectiveKind.Custom:
                    done = true;
                    break;
            }
            if (d.TimeLimit > 0 && d.Kind is not ObjectiveKind.Survive and not ObjectiveKind.Prevent && !timerSuccess)
                o.Progress += $" · {Math.Max(0, d.TimeLimit - elapsed):0} с";
            if (fail.Length > 0)
            {
                Finish(o, ObjectiveState.Failed, fail);
                continue;
            }
            if (done && extra)
            {
                Finish(o, ObjectiveState.Completed, "");
                continue;
            }
            if (d.TimeLimit > 0 && elapsed + 1e-9 >= d.TimeLimit)
            {
                if (timerSuccess && extra) Finish(o, ObjectiveState.Completed, "время вышло — продержались");
                else if (!timerSuccess) Finish(o, ObjectiveState.Failed, "время вышло");
            }
        }
    }

    // ------------------------------------------------------------------ bosses

    private void UpdateBosses()
    {
        foreach (var u in _sim.Units.ToList())
        {
            if (!u.Alive || !u.Def.Boss.IsBoss || u.Def.Boss.Phases.Count == 0) continue;
            if (!_bossPhases.TryGetValue(u.Id, out var fired)) _bossPhases[u.Id] = fired = new HashSet<int>();
            var phases = u.Def.Boss.Phases;
            for (int i = 0; i < phases.Count; i++)
            {
                if (fired.Contains(i)) continue;
                if (!Evaluate(phases[i].When, u)) break; // phases go in order
                fired.Add(i);
                var ph = phases[i];
                _sim.Emit(SimEventType.Message, LogLevel.Normal, string.IsNullOrEmpty(ph.Message) ? $"{u.Name}: фаза «{ph.Name}»" : ph.Message, u.Id);
                foreach (var a in ph.Actions) Execute(a, $"{u.Name}: {ph.Name}", u);
            }
        }
    }

    /// <summary>Current phase name of a boss ("" — before the first phase).</summary>
    public string PhaseOf(Unit u)
    {
        if (!_bossPhases.TryGetValue(u.Id, out var fired) || fired.Count == 0) return "";
        return u.Def.Boss.Phases[fired.Max()].Name;
    }

    // ------------------------------------------------------------------ outcome

    public void CheckOutcome()
    {
        if (Def == null || _sim.Outcome != BattleOutcome.None) return;
        if (Def.Limits.TimeLimit > 0 && _sim.Time >= Def.Limits.TimeLimit)
        {
            SetOutcome(BattleOutcome.Defeat, "Время вышло");
            return;
        }
        foreach (var c in Def.Defeat)
            if (Evaluate(c))
            {
                SetOutcome(BattleOutcome.Defeat, c.Describe());
                return;
            }
        if (_objectives.Count > 0)
        {
            CheckObjectiveOutcome();
            return;
        }
        foreach (var c in Def.Victory)
            if (Evaluate(c))
            {
                SetOutcome(BattleOutcome.Victory, c.Describe());
                return;
            }
    }

    /// <summary>Victory by the objective logic; a failed required objective is a defeat.</summary>
    private void CheckObjectiveOutcome()
    {
        var req = _objectives.Where(o => o.Def.Required).ToList();
        if (req.Count == 0) return;
        bool Satisfied(ObjectiveRuntime o) =>
            o.State == ObjectiveState.Completed || (o.Def.Kind == ObjectiveKind.Prevent && o.State == ObjectiveState.Active);
        switch (Def!.ObjectiveLogic)
        {
            case ObjectiveLogic.AnyRequired:
                if (req.All(o => o.State == ObjectiveState.Failed))
                    SetOutcome(BattleOutcome.Defeat, "все цели провалены");
                else if (req.Any(o => o.State == ObjectiveState.Completed))
                    SetOutcome(BattleOutcome.Victory, "цель выполнена: " + req.First(o => o.State == ObjectiveState.Completed).Def.Name);
                break;
            default:
                var failed = req.FirstOrDefault(o => o.State == ObjectiveState.Failed);
                if (failed != null)
                {
                    SetOutcome(BattleOutcome.Defeat, $"цель «{failed.Def.Name}» провалена: {failed.Reason}");
                    break;
                }
                // «Предотвратить» holds while nothing happened; at least one other required objective must be done.
                if (req.All(Satisfied) && req.Any(o => o.State == ObjectiveState.Completed))
                    SetOutcome(BattleOutcome.Victory, "все обязательные цели выполнены");
                break;
        }
    }

    private void SetOutcome(BattleOutcome o, string text)
    {
        if (_sim.Outcome != BattleOutcome.None) return;
        _sim.Outcome = o;
        _sim.OutcomeText = text;
        _sim.Emit(o == BattleOutcome.Victory ? SimEventType.Victory : SimEventType.Defeat, LogLevel.Normal,
            (o == BattleOutcome.Victory ? "ПОБЕДА: " : "ПОРАЖЕНИЕ: ") + text);
        _sim.RequestAutopause(o == BattleOutcome.Victory ? "победа" : "поражение");
    }
}
