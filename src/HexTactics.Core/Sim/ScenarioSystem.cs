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
    // Part 4 (docs/DECISIONS_4.md §58): appearance of a group from a spawn base along a scenario route.
    public string Route { get; init; } = "";
    public string Target { get; init; } = "";
    public string Group { get; init; } = "";
    public string SpawnBase { get; init; } = "";
    public string Faction { get; init; } = "";
    /// <summary>Modifiers of the instance: the group's and the wave's difficulty (docs/DECISIONS_4.md §58).</summary>
    public List<(StatModDef Mod, string Source)> Mods { get; init; } = new();
    /// <summary>Why the appearance waits now (a limit), "" — it does not.</summary>
    public string WaitReason { get; set; } = "";
    /// <summary>Planned again after waiting for a slot (holds that slot until it appears).</summary>
    public bool Replanned { get; set; }
    /// <summary>The portion (subgroup of a run of a group) the appearance belongs to; "" — alone.</summary>
    public string Batch { get; init; } = "";
    /// <summary>When it appeared (−1 — not yet or cancelled).</summary>
    public long SpawnedTick { get; set; } = -1;
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
    /// <summary>
    /// Every appearance is done (spawned or cancelled): «появление окончено» — the wave itself ends when its enemies are
    /// gone (ТЗ ч. 5, 9.7; docs/DECISIONS_5.md §85).
    /// </summary>
    public long SpawnFinishedTick { get; set; } = -1;
    /// <summary>The composition picked by the budget at the start (an unpinned budget wave).</summary>
    public BudgetResult? Budget { get; set; }
    /// <summary>Groups whose «условие отмены» fired.</summary>
    public HashSet<string> CancelledGroups { get; } = new(StringComparer.Ordinal);
    /// <summary>The wave ran out of its «макс. продолжительность».</summary>
    public bool Expired { get; set; }
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
public sealed partial class ScenarioSystem
{
    private readonly Simulation _sim;
    private readonly List<WaveRuntime> _waves = new();
    private readonly List<ObjectiveRuntime> _objectives = new();
    private readonly HashSet<int> _firedTriggers = new();
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _vars = new(StringComparer.Ordinal);
    private readonly Dictionary<ConditionDef, long> _areaSince = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ConditionDef, int> _orderProgress = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, HashSet<int>> _bossPhases = new();
    // Part 6, stage 6.7 (§102): the phase each boss is in and how many transitions it made (repeatable phases).
    private readonly Dictionary<int, int> _bossCurrent = new();
    private readonly Dictionary<int, int> _bossEntries = new();
    private readonly HashSet<string> _taggedSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _flagTicks = new(StringComparer.Ordinal);
    private readonly Dictionary<ConditionDef, HashSet<int>> _areaInside = new(ReferenceEqualityComparer.Instance);
    /// <summary>AI rules: (unit, profile, rule index) → next allowed tick (long.MaxValue — fired once).</summary>
    private readonly Dictionary<(int Unit, AiProfileDef Profile, int Rule), long> _aiRules = new();
    private int _triggerDepth;

    /// <summary>Nested trigger activations deeper than this are cut (a cycle of «запустить триггер»).</summary>
    public const int MaxTriggerDepth = 8;

    // Part 6, stage 6.6 (§101): triggers started later, and what the compiled graphs did (the debugger reads it).
    private readonly List<(long Tick, string Name)> _delayed = new();
    private readonly List<GraphTraceEntry> _graphTrace = new();
    public const int MaxGraphTrace = 2000;

    /// <summary>The nodes of the graphs of events that ran, in order, with the tick and why (ТЗ ч. 6, 7.6; outside the fingerprint).</summary>
    public IReadOnlyList<GraphTraceEntry> GraphTrace => _graphTrace;

    /// <summary>Triggers waiting for their tick (the runtime state of «Задержка» and «Ограниченный повтор», saved with the battle — ТЗ 7.8).</summary>
    public IEnumerable<string> DelayedTriggers => _delayed.Select(d => $"{d.Tick}:{d.Name}");

    /// <summary>Raised for every entry of the trace (a breakpoint on a node of a graph listens to it).</summary>
    public event Action<GraphTraceEntry>? GraphNodeRan;

    private void Trace(TriggerDef tr, string text) => TraceNode(tr.GraphNode, text);

    private void TraceNode(string mark, string text)
    {
        if (mark == "") return;
        int slash = mark.IndexOf('/');
        var e = new GraphTraceEntry(_sim.Tick, slash > 0 ? mark[..slash] : mark, slash > 0 ? mark[(slash + 1)..] : "", text);
        if (_graphTrace.Count >= MaxGraphTrace) _graphTrace.RemoveAt(0);
        _graphTrace.Add(e);
        _sim.Emit(SimEventType.Diagnostic, LogLevel.Detail, $"Граф «{e.Graph}», узел {e.Node}: {text}");
        GraphNodeRan?.Invoke(e);
    }
    public long TriggerCycleCuts { get; private set; }
    private bool _hadPlayerUnits;
    private int _nextSpawnId = 1;
    private readonly Dictionary<string, int> _createdByBase = new(StringComparer.Ordinal);
    private int _createdTotal;

    /// <summary>Enemies created by waves so far (for «лимит созданных»).</summary>
    public int CreatedTotal => _createdTotal;
    public int CreatedBy(string spawnBase) => _createdByBase.GetValueOrDefault(spawnBase);

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

    /// <summary>Variables of the scenario: time of day, weather… (docs/DECISIONS_4.md §62); "" — not set.</summary>
    public IReadOnlyDictionary<string, string> Variables => _vars;

    public string Variable(string name) => _vars.GetValueOrDefault(name, "");

    /// <summary>Changes a variable (logged); conditions read it on their next check.</summary>
    public void SetVariable(string name, string value)
    {
        if (string.IsNullOrEmpty(name) || _vars.GetValueOrDefault(name) == value) return;
        _vars[name] = value;
        var caption = Def?.Environment.FirstOrDefault(v => v.Name == name)?.Caption;
        _sim.Emit(SimEventType.Info, LogLevel.Normal, $"{(string.IsNullOrEmpty(caption) ? name : caption)}: {value}");
    }

    /// <summary>Waves that have started (at least one enemy appeared).</summary>
    public IReadOnlyCollection<int> SpawnedWaves => _waves.Where(w => w.StartedTick >= 0).Select(w => w.Index).ToList();

    public ObjectiveRuntime? Objective(string id) => _objectives.FirstOrDefault(o => o.Def.Id == id);

    /// <summary>Creates a simulation for a scenario (sides → player/AI factions, units placed).</summary>
    public static Simulation Create(ContentDatabase db, ScenarioDef sc, ulong? seed = null, bool admin = false, bool autoPlayer = false,
        IReadOnlyList<string>? roster = null)
    {
        // The graphs of events (part 6, §101) become ordinary triggers of a copy of the scenario.
        sc = RuleGraphs.WithCompiled(sc);
        // «Автотест» (docs/DECISIONS_4.md §64): the player's side is driven by AI profiles as well.
        autoPlayer |= sc.Mode == BattleMode.AutoTest;
        string defender = sc.Placement.DefenderProfile != "" ? sc.Placement.DefenderProfile : db.Get<AiProfileDef>("ai_guard") != null ? "ai_guard" : "";
        var map = db.Get<MapDef>(sc.Map) ?? throw new InvalidOperationException($"Карта '{sc.Map}' не найдена");
        var player = new HashSet<string>(sc.Sides.Where(s => s.Controller == SideController.Player).Select(s => s.Faction));
        var ai = new HashSet<string>(sc.Sides.Where(s => s.Controller == SideController.Ai).Select(s => s.Faction));
        ulong s0 = seed ?? (sc.Seed != 0 ? sc.Seed : (ulong)DateTime.UtcNow.Ticks);
        // The rules of the mode (ТЗ ч. 6, 1.4–1.5; docs/DECISIONS_6.md §96): the battle rules they point to and the switches.
        var mode = EffectiveGameRules.Resolve(db, sc);
        var rules = db.Get<RulesPresetDef>(mode.RulesPreset)?.Rules ?? db.Rules;
        if (mode.FriendlyFire is { } ff && ff != rules.Combat.FriendlyFire)
        {
            rules = ContentJson.Clone(rules);
            rules.Combat.FriendlyFire = ff;
        }
        var sim = new Simulation(db, map, new SimOptions
        {
            Rules = rules,
            Seed = s0,
            PlayerFactions = player,
            AiFactions = ai,
            Stacking = sc.Limits.Stacking,
            FogOfWar = sc.Limits.FogOfWar && rules.Vision.FogOfWar,
            AdminMode = admin,
            AutoPlayer = autoPlayer,
            DefaultPlayerProfile = defender,
            Blocking = mode.Blocking,
            Roster = roster,
            SquadStart = mode.SquadStart,
            Deployment = mode.Deployment,
        });
        sim.Scenario.Load(sc);
        return sim;
    }

    public void Load(ScenarioDef sc)
    {
        Def = sc;
        foreach (var v in sc.Environment)
            if (v.Name != "")
                _vars[v.Name] = v.Value;
        ScenarioObjects.ResolveMarkers(sc, _sim.Map.Def);
        // Structures first: their hexes are taken before units are placed (docs/DECISIONS_4.md §56).
        _sim.Structures.Load(sc);
        // The states of the objects (part 6, §100): their terrain is set before anyone is placed.
        _sim.Interactive.Load(sc);
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
            if (su.Level > 0) ApplyLevel(u, su.Level);
            if (su.Boss != null) u.BossOverride = su.Boss;
        }
        // The player's squad (part 6, §96): after the scenario's own units, so their hexes are taken.
        _sim.Squad.Load(sc);
        _sim.Deployment.Load(_sim.Options.Deployment);
        _sim.Mission.Load(sc);
        _hadPlayerUnits = _sim.Units.Any(u => _sim.IsPlayerFaction(u.FactionId));
        for (int i = 0; i < sc.Waves.Count; i++) _waves.Add(new WaveRuntime { Index = i, Def = sc.Waves[i] });
        foreach (var o in sc.Objectives.Select((d, i) => (d, i)).OrderBy(x => x.d.Priority).ThenBy(x => x.i))
            _objectives.Add(new ObjectiveRuntime { Def = o.d });
        LoadEvents(sc);
        // «Автотест»: the defenders stand as the scenario's preset placement says.
        if (sc.Mode == BattleMode.AutoTest) _sim.Placement.PlacePreset(free: true);
        // Waves by time are known in advance: their warning lines start before the first enemy appears (after the
        // preparation of «TD с расстановкой» — from its end).
        if (!WavesHeld)
            foreach (var w in _waves)
                if (w.Def.Start == WaveStart.Time)
                    Schedule(w, _sim.TicksFor(w.Def.Time), lead: false);
        if (!string.IsNullOrWhiteSpace(sc.Intro)) _sim.Emit(SimEventType.Message, LogLevel.Normal, sc.Intro);
        _sim.Vision.Recompute();
    }

    // ------------------------------------------------------------------ waves

    /// <summary>
    /// Hexes (or waypoints) of a route by ID: a route of the scenario — its main line hex by hex; otherwise a route
    /// of the map — its points (docs/DECISIONS_4.md §57). Null — no such route.
    /// </summary>
    public List<HexCoord>? RoutePoints(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (Def?.Route(id) is { } r)
        {
            var path = r.MainPath();
            return path.Count > 0 ? path : null;
        }
        return _sim.Map.Def.FindRoute(id)?.Points;
    }

    /// <summary>One appearance before it is planned on the timeline.</summary>
    private sealed record SpawnItem(UnitDef Def, string Tag, string AiProfile, HexCoord From, string Marker, string Route, string Target, string Group,
        string Base, string Faction, double Offset, List<(StatModDef, string)> Mods, string Batch = "");

    /// <summary>The wave's difficulty and the group's own modifiers as instance modifiers.</summary>
    private static List<(StatModDef, string)> ModsOf(WaveDef w, SpawnGroupDef? g)
    {
        var list = new List<(StatModDef, string)>();
        var d = w.Difficulty;
        string src = $"сложность волны «{w.Name}»";
        if (d.Health != 1) list.Add((new StatModDef { Stat = Stats.HpMax, Mul = d.Health - 1 }, src));
        if (d.Power != 1)
        {
            list.Add((new StatModDef { Stat = Stats.Power, Mul = d.Power - 1 }, src));
            list.Add((new StatModDef { Stat = Stats.MagicPower, Mul = d.Power - 1 }, src));
        }
        if (d.Speed != 1) list.Add((new StatModDef { Stat = Stats.MoveSpeed, Mul = d.Speed - 1 }, src));
        if (g != null)
            foreach (var m in g.StatMods)
                list.Add((m, $"группа «{(g.Name != "" ? g.Name : g.Id)}»"));
        return list;
    }

    /// <summary>The groups a wave spawns now: its own and, for an unpinned budget, the picked ones.</summary>
    private List<SpawnGroupDef> GroupsOf(WaveRuntime w)
    {
        var groups = w.Def.Groups.Where(g => g.Enabled).ToList();
        var b = w.Def.Budget;
        if (b.Enabled && !b.Pinned && Def != null)
        {
            w.Budget = WaveBudget.Compose(_sim.Content, w.Def, b, WaveBudget.SeedOf(b, Def.Seed != 0 ? Def.Seed : _sim.Options.Seed, w.Index), Def);
            groups.AddRange(WaveBudget.ToGroups(w.Budget, b, w.Def.Id != "" ? w.Def.Id : $"w{w.Index + 1}"));
            _sim.Emit(SimEventType.Info, LogLevel.Detail, $"Волна «{w.Def.Name}» по бюджету: {w.Budget}");
            foreach (var pr in w.Budget.Problems) _sim.Emit(SimEventType.Diagnostic, LogLevel.Normal, $"Волна «{w.Def.Name}»: {pr}");
        }
        return groups;
    }

    /// <summary>Appearances of a wave: the old composition at spawn markers, then the groups at their spawn bases.</summary>
    private List<SpawnItem> SpawnItems(WaveRuntime wr, List<MapMarkerDef> markers)
    {
        var w = wr.Def;
        var list = new List<SpawnItem>();
        if (markers.Count > 0)
        {
            int i = 0;
            var mods = ModsOf(w, null);
            foreach (var (wu, def) in WaveUnits(w))
            {
                var m = markers[i % markers.Count];
                list.Add(new SpawnItem(def, wu.Tag, wu.AiProfile, m.C, m.Id, w.Route, "", "", "", w.Faction, w.Interval * i, mods));
                i++;
            }
        }
        foreach (var g in GroupsOf(wr))
        {
            var def = _sim.Content.Unit(g.Unit);
            var bases = g.Bases().Select(id => Def?.Object(id)).Where(b => b?.Spawn != null).Select(b => b!).ToList();
            if (def == null || bases.Count == 0)
            {
                _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"Волна «{w.Name}», группа «{g.Id}»: {(def == null ? $"нет шаблона «{g.Unit}»" : $"нет базы спавна «{g.SpawnBase}»")} — пропущена");
                continue;
            }
            int n = WaveSchedule.CountOf(g, w);
            var mods = ModsOf(w, g);
            var routes = g.Routes();
            int turn = 0;
            // Subgroups inside a run, the runs one after another; several bases take the enemies in turn, several
            // routes — by the group's rule.
            for (int rep = 0; rep <= g.Repeats; rep++)
                for (int k = 0; k < n; k++)
                {
                    var b = bases[k % bases.Count];
                    string route = routes.Count == 1 || g.RouteSelect == GroupRouteSelect.Single ? routes[0]
                        : g.RouteSelect == GroupRouteSelect.Random ? routes[_sim.SpawnRng.NextInt(0, routes.Count)] : routes[turn++ % routes.Count];
                    string target = g.Target != "" ? g.Target : Def?.Route(route)?.Target ?? "";
                    list.Add(new SpawnItem(def, g.Tag, g.AiProfile, b.Anchor, b.Marker != "" ? b.Marker : b.Id, route, target, g.Id, b.Id,
                        g.Faction != "" ? g.Faction : w.Faction, b.Spawn!.InitialDelay + rep * g.RepeatEvery + g.OffsetOf(k), mods,
                        $"{g.Id}:{rep}:{(g.SubgroupSize > 0 ? k / g.SubgroupSize : k)}"));
                }
        }
        return list;
    }

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
        if (markers.Count == 0 && w.Def.Groups.Count == 0 && !w.Def.Budget.Enabled)
        {
            _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"Волна «{w.Def.Name}»: нет точки появления");
            w.State = WaveState.Cancelled;
            return;
        }
        var list = SpawnItems(w, markers);
        if (lead && list.Count > 0)
        {
            double maxLead = 0;
            foreach (var it in list)
                maxLead = Math.Max(maxLead, Warnings.LeadSeconds(w.Def, it.Def, it.Faction, it.From, it.Marker, it.Route));
            baseTick += _sim.TicksFor(maxLead);
        }
        foreach (var it in list)
        {
            var p = new PlannedSpawn
            {
                Id = _nextSpawnId++, Wave = w.Index, Def = it.Def, Marker = it.Marker, From = it.From,
                Tick = baseTick + _sim.TicksFor(it.Offset), Tag = it.Tag, AiProfile = it.AiProfile,
                Route = it.Route, Target = it.Target, Group = it.Group, SpawnBase = it.Base, Faction = it.Faction, Mods = it.Mods, Batch = it.Batch,
            };
            w.Planned.Add(p);
            Warnings.Plan(p.Id, w.Index, w.Def, p.Def, p.Faction, p.Marker, p.From, p.Tick, p.Route);
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
        if (w.SpawnFinishedTick < 0) SpawnFinished(w);
        _sim.Emit(SimEventType.Info, LogLevel.Normal, $"Волна «{w.Def.Name}» отменена");
        _sim.Emit(SimEventType.WaveEnded, LogLevel.Diagnostic, $"Волна «{w.Def.Name}» завершена: отменена", value: index);
        Raise(ScenarioEventType.WaveEnded, null, "", index);
    }

    private int AliveOf(WaveRuntime w) => w.UnitIds.Count(id => _sim.GetUnit(id)?.Alive == true);

    private void ProcessSpawns(WaveRuntime w, bool ignoreLimit = false)
    {
        foreach (var p in w.Planned)
        {
            if (p.Done || _sim.Tick < p.Tick) continue;
            if (CreatedLimit(p) is { } full)
            {
                // «Лимит созданных» is not «лимит живых»: the appearance does not wait, it is cancelled.
                p.Done = true;
                Warnings.Remove(p.Id);
                _sim.Emit(SimEventType.Diagnostic, LogLevel.Normal, $"Волна «{w.Def.Name}»: {full} — появление «{p.Def.DisplayName}» отменено");
                continue;
            }
            if (!ignoreLimit && AliveLimit(w, p) is { } reason)
            {
                // No free slot: the appearance waits; its line disappears and is planned again when a slot frees.
                if (!p.Postponed)
                {
                    p.Postponed = true;
                    Warnings.Remove(p.Id);
                }
                if (p.WaitReason != reason)
                {
                    if (p.WaitReason == "") Raise(ScenarioEventType.AliveLimitExceeded, null, p.SpawnBase, w.Index, p.Group);
                    p.WaitReason = reason;
                    _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"Волна «{w.Def.Name}»: «{p.Def.DisplayName}» ждёт — {reason}");
                }
                continue;
            }
            p.WaitReason = "";
            SpawnOne(w, p);
        }
    }

    private bool IsWaveEnemy(Unit u) => u.Alive && u.Wave >= 0 && !u.IsStructure;

    /// <summary>
    /// The dispatcher's limits of the living (docs/DECISIONS_4.md §58): the group, the wave, the spawn base, the
    /// scenario. Null — a slot is free. <paramref name="reserved"/> counts appearances already re-planned.
    /// </summary>
    private string? AliveLimit(WaveRuntime w, PlannedSpawn p, int reserved = 0)
    {
        if (w.Def.MaxAlive > 0 && AliveOf(w) + reserved >= w.Def.MaxAlive) return $"лимит живых волны {w.Def.MaxAlive}";
        if (p.Group != "" && w.Def.Groups.FirstOrDefault(g => g.Id == p.Group) is { MaxAlive: > 0 } g
            && w.UnitIds.Count(id => _sim.GetUnit(id) is { Alive: true } u && u.Group == p.Group) + reserved >= g.MaxAlive)
            return $"лимит живых группы «{g.Id}» {g.MaxAlive}";
        if (p.SpawnBase != "" && Def?.Object(p.SpawnBase)?.Spawn is { MaxAlive: > 0 } sp
            && _sim.Units.Count(u => IsWaveEnemy(u) && u.SpawnBase == p.SpawnBase) + reserved >= sp.MaxAlive)
            return $"лимит живых базы «{Def!.Object(p.SpawnBase)!.DisplayName}» {sp.MaxAlive}";
        if (Def?.Limits.MaxAliveEnemies is > 0 and var max && _sim.Units.Count(IsWaveEnemy) + reserved >= max)
            return $"лимит живых врагов сценария {max}";
        return null;
    }

    /// <summary>The limits of the created: the spawn base and the scenario (for the whole battle).</summary>
    private string? CreatedLimit(PlannedSpawn p)
    {
        if (p.SpawnBase != "" && Def?.Object(p.SpawnBase) is { Spawn.MaxCreated: > 0 } b && CreatedBy(p.SpawnBase) >= b.Spawn!.MaxCreated)
            return $"лимит созданных базы «{b.DisplayName}» {b.Spawn.MaxCreated} исчерпан";
        if (Def?.Limits.MaxCreatedEnemies is > 0 and var max && _createdTotal >= max)
            return $"лимит созданных врагов сценария {max} исчерпан";
        return null;
    }

    private void SpawnOne(WaveRuntime w, PlannedSpawn p)
    {
        var b = p.SpawnBase != "" ? Def?.Object(p.SpawnBase) : null;
        HexCoord? cell;
        if (b?.Spawn != null)
        {
            // A destroyed base: its policy (ТЗ ч. 5, 9.10; docs/DECISIONS_5.md §85).
            if (_sim.Structures.Of(b.Id) is { Alive: false } dead && b.Spawn.OnDestroyed != SpawnerDestroyedPolicy.ContinueQueue)
            {
                bool keep = b.Spawn.OnDestroyed == SpawnerDestroyedPolicy.CompleteBatch && p.Batch != ""
                            && w.Planned.Any(x => x.Batch == p.Batch && x.SpawnBase == p.SpawnBase && x.SpawnedTick >= 0 && x.SpawnedTick <= dead.DiedAtTick);
                if (!keep)
                {
                    p.Done = true;
                    Warnings.Remove(p.Id);
                    _sim.Emit(SimEventType.Diagnostic, LogLevel.Normal, $"«{b.DisplayName}» разрушена — появление «{p.Def.DisplayName}» отменено " +
                        (b.Spawn.OnDestroyed == SpawnerDestroyedPolicy.Stop ? "(остановить)" : "(порция не начиналась)"));
                    return;
                }
            }
            // A spawn base: inactive — the appearance waits; a taken exit — the base's policy (docs/DECISIONS_4.md §58).
            if (!SpawnBaseActive(b)) return;
            cell = SpawnCell(b, p.Def);
            if (cell == null)
            {
                switch (b.Spawn.Blocked)
                {
                    case SpawnBlockedPolicy.Wait:
                    case SpawnBlockedPolicy.OtherInArea when b.Spawn.AreaRadius > 0:
                        return; // retried next tick
                    default:
                        p.Done = true;
                        Warnings.Remove(p.Id);
                        _sim.Emit(SimEventType.Diagnostic, LogLevel.Normal, $"«{b.DisplayName}»: выход занят — появление «{p.Def.DisplayName}» отменено");
                        return;
                }
            }
        }
        else cell = FindFreeCell(p.From, p.Def, 4);
        p.Done = true;
        Warnings.Remove(p.Id);
        if (cell == null)
        {
            _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"Волна «{w.Def.Name}»: нет свободной клетки у {p.From}");
            return;
        }
        string routeId = p.Group != "" && GroupRoute(p.Group) is { Length: > 0 } switched ? switched : p.Route != "" ? p.Route : p.Group == "" ? w.Def.Route : "";
        // «Свободный бой»: no routes — enemies look for the defenders by their AI profiles (docs/DECISIONS_4.md §64).
        if (Mode == BattleMode.FreeBattle) routeId = "";
        var points = RoutePoints(routeId);
        var first = points != null && points.Count > 0 ? points[0] : MapCenter();
        var facing = cell.Value.DirectionTo(first == cell.Value && points != null && points.Count > 1 ? points[1] : first) ?? HexDirection.W;
        Unit u;
        try
        {
            u = _sim.Spawn(p.Def, p.Faction != "" ? p.Faction : w.Def.Faction, cell.Value, facing, null, string.IsNullOrEmpty(p.Tag) ? null : p.Tag,
                string.IsNullOrEmpty(p.AiProfile) ? null : p.AiProfile);
        }
        catch (InvalidOperationException e)
        {
            _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, e.Message);
            return;
        }
        p.SpawnedTick = _sim.Tick;
        var gdef = p.Group != "" ? w.Def.Groups.FirstOrDefault(g => g.Id == p.Group) : null;
        string target = p.Target;
        // The final base chosen when the enemy appears (ТЗ ч. 5, 8.3): nearest reachable, shortest, weakest, by weights.
        if (gdef is { RouteSelect: GroupRouteSelect.NearestBase or GroupRouteSelect.ShortestRoute or GroupRouteSelect.LowestHp or GroupRouteSelect.Weighted }
            && Mode != BattleMode.FreeBattle && GroupRoute(p.Group) is not { Length: > 0 } && ChooseRoute(u, gdef) is { } chosen && chosen != routeId)
        {
            routeId = chosen;
            points = RoutePoints(routeId);
            if (points is { Count: > 0 }) u.Facing = u.Pos.DirectionTo(points[0] == u.Pos && points.Count > 1 ? points[1] : points[0]) ?? u.Facing;
            if (gdef.Target == "") target = Def?.Route(routeId)?.Target ?? "";
        }
        u.Wave = w.Index;
        u.Group = p.Group;
        u.RouteId = routeId;
        u.TargetObject = target;
        u.SpawnBase = p.SpawnBase;
        u.Ai.Hunts = Mode == BattleMode.Assault || Mode == BattleMode.Mixed && gdef?.Hunt == true;
        if (p.Mods.Count > 0)
        {
            u.InstanceMods.AddRange(p.Mods);
            _sim.Statuses.RecalculateModifiers(u);
            u.Hp = u.HpMax;
        }
        // The level of the instances of a group (docs/DECISIONS_4.md §65): a fixed one or a range by the seed.
        if (p.Group != "" && w.Def.Groups.FirstOrDefault(g => g.Id == p.Group) is { Level: > 0 } lg)
            ApplyLevel(u, lg.LevelMax > lg.Level ? _sim.SpawnRng.NextInt(lg.Level, lg.LevelMax + 1) : lg.Level);
        _createdTotal++;
        if (p.SpawnBase != "") _createdByBase[p.SpawnBase] = CreatedBy(p.SpawnBase) + 1;
        if (!string.IsNullOrEmpty(u.Tag)) _taggedSeen.Add(u.Tag);
        if (RouteNavigator.IsGraph(Def?.Route(routeId)))
            _sim.Routes.Assign(u, routeId, fromStart: true);
        else if (points != null && points.Count > 0)
        {
            u.Route = new List<HexCoord>(points);
            // Already on the route: continue from the nearest point ahead.
            int at = u.Route.IndexOf(u.Pos);
            u.RouteIndex = at >= 0 ? at + 1 : 0;
        }
        if (b?.Spawn != null && b.Spawn.SpawnVfx != "") _sim.Emit(SimEventType.Vfx, LogLevel.Diagnostic, b.Spawn.SpawnVfx, u.Id, -1, u.Pos);
        w.UnitIds.Add(u.Id);
        if (w.StartedTick < 0)
        {
            w.StartedTick = _sim.Tick;
            w.State = WaveState.Active;
            string msg = string.IsNullOrWhiteSpace(w.Def.Message) ? $"Волна «{w.Def.Name}»: {w.Total} противников" : w.Def.Message;
            _sim.Emit(SimEventType.WaveStarted, LogLevel.Normal, msg, cell: p.From, value: w.Index);
            if (_sim.Rules.Autopause.WaveStarted) _sim.RequestAutopause("началась волна");
            Raise(ScenarioEventType.WaveStarted, null, "", w.Index);
            foreach (var a in w.Def.OnStart) Execute(a, w.Def.Name, null);
        }
    }

    /// <summary>The route of a group by its base rule; null — keep the planned one. The choice is logged.</summary>
    private string? ChooseRoute(Unit u, SpawnGroupDef g)
    {
        var routes = g.Routes().Select(id => Def?.Route(id)).Where(r => r != null && RoutePoints(r.Id) is { Count: > 0 }).Select(r => r!).ToList();
        if (routes.Count < 2) return null;
        // A destroyed base is never chosen while another is left.
        var alive = routes.Where(r => r.Target == "" || _sim.Structures.Of(r.Target) is not { Alive: false }).ToList();
        if (alive.Count > 0) routes = alive;
        RouteDef? pick;
        string why;
        switch (g.RouteSelect)
        {
            case GroupRouteSelect.NearestBase:
            {
                var open = routes.Where(r => _sim.Pathfinder.Find(_sim, u, u.Pos, RoutePoints(r.Id)![^1], new PathOptions { IgnoreUnits = true, MaxExpanded = 4000 }).Found).ToList();
                if (open.Count > 0) routes = open;
                pick = routes.OrderBy(r => HexCoord.Distance(u.Pos, TargetCell(r))).First();
                why = open.Count > 0 ? "ближайшая достижимая база" : "ближайшая база (путь ни к одной не открыт)";
                break;
            }
            case GroupRouteSelect.ShortestRoute:
                pick = routes.OrderBy(r => RoutePoints(r.Id)!.Count).First();
                why = "кратчайший путь";
                break;
            case GroupRouteSelect.LowestHp:
                pick = routes.OrderBy(r => _sim.Structures.Of(r.Target) is { } s ? s.Hp / Math.Max(1, s.HpMax) : 2).First();
                why = "база с наименьшим здоровьем";
                break;
            default:
            {
                double sum = routes.Sum(r => Math.Max(0, r.Weight));
                if (sum <= 0) return null;
                double roll = _sim.SpawnRng.NextDouble() * sum;
                pick = routes[^1];
                foreach (var r in routes)
                {
                    roll -= Math.Max(0, r.Weight);
                    if (roll < 0)
                    {
                        pick = r;
                        break;
                    }
                }
                why = "по весам";
                break;
            }
        }
        string to = pick.Target != "" && Def?.Object(pick.Target) is { } o ? $"«{o.DisplayName}»" : $"«{pick.DisplayName}»";
        _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"{u.Name} (группа «{g.Id}»): цель {to} — {why}", u.Id, cell: u.Pos);
        return pick.Id;
    }

    /// <summary>The hex of a route's target object, or its last hex.</summary>
    private HexCoord TargetCell(RouteDef r) => r.Target != "" && Def?.Object(r.Target) is { } o ? o.Anchor : RoutePoints(r.Id)![^1];

    /// <summary>A spawn base works now: active and not switched off by its conditions.</summary>
    private bool SpawnBaseActive(ScenarioObjectDef b)
    {
        var sp = b.Spawn!;
        // Switched by an event («активировать / отключить базу») — the event wins over the setting.
        if (ObjectActive(b.Id) is { } forced) return forced;
        if (!sp.Active) return false;
        if (sp.DisableCondition != null && Evaluate(sp.DisableCondition)) return false;
        return sp.EnableCondition == null || Evaluate(sp.EnableCondition);
    }

    /// <summary>A free hex of a spawn base: its own hexes and area; with «nearest free» — also around (bounded).</summary>
    private HexCoord? SpawnCell(ScenarioObjectDef b, UnitDef def)
    {
        var sp = b.Spawn!;
        var mc = MoveContext.For(_sim, def, null);
        bool Free(HexCoord c)
        {
            var cell = _sim.Map.Get(c);
            return cell != null && cell.AllowsPlacement && MovementRules.Check(mc, null, cell, false, out _) == StepCheck.Ok;
        }
        foreach (var c in ScenarioObjectsCheck.SpawnArea(b))
        {
            if (Free(c)) return c;
            if (sp.Blocked == SpawnBlockedPolicy.Wait) return null; // only the base's own hex
        }
        if (sp.Blocked != SpawnBlockedPolicy.NearestFree) return null;
        foreach (var c in HexCoord.Spiral(b.Anchor, sp.AreaRadius + Math.Max(1, sp.SearchRadius)))
            if (Free(c))
                return c;
        return null;
    }

    private void UpdateWaves()
    {
        foreach (var w in _waves)
        {
            // «Условие прерывания» (docs/DECISIONS_4.md §58): the rest of the wave does not appear.
            if (w.State is WaveState.Pending or WaveState.Scheduled or WaveState.Active && w.Def.CancelCondition != null && Evaluate(w.Def.CancelCondition))
            {
                CancelWave(w.Index);
                continue;
            }
            switch (w.State)
            {
                case WaveState.Pending:
                {
                    long at = _sim.Tick + _sim.TicksFor(w.Def.Delay);
                    // «Мин. интервал до следующей» of the previous wave holds back every wave that is not on a timer.
                    if (w.Index > 0 && _waves[w.Index - 1] is { StartedTick: >= 0 } prev && prev.Def.MinGapNext > 0)
                        at = Math.Max(at, prev.StartedTick + _sim.TicksFor(prev.Def.MinGapNext));
                    if (w.Def.Start == WaveStart.AfterPrevious && w.Index > 0 && _waves[w.Index - 1].State is WaveState.Cleared or WaveState.Cancelled)
                        Schedule(w, at, lead: true);
                    else if (w.Def.Start == WaveStart.AfterPreviousSpawned && (w.Index == 0 || _waves[w.Index - 1].SpawnFinishedTick >= 0 || _waves[w.Index - 1].State == WaveState.Cancelled))
                        Schedule(w, at, lead: true);
                    else if (w.Def.Start == WaveStart.AfterPrevious && w.Index == 0)
                        Schedule(w, at, lead: true);
                    else if (w.Def.Start == WaveStart.OnCondition && Evaluate(w.Def.StartCondition))
                        Schedule(w, at, lead: true);
                    // Appearances due right now come out in this tick, as planned (ТЗ ч. 5, T07), not one tick later.
                    if (w.State == WaveState.Scheduled) ProcessSpawns(w);
                    break;
                }
                case WaveState.Scheduled:
                case WaveState.Active:
                {
                    CancelGroups(w);
                    if (!w.Expired && w.Def.MaxDuration > 0 && w.StartedTick >= 0 && _sim.Tick - w.StartedTick >= _sim.TicksFor(w.Def.MaxDuration))
                    {
                        w.Expired = true;
                        int n = 0;
                        foreach (var p in w.Planned.Where(p => !p.Done))
                        {
                            p.Done = true;
                            Warnings.Remove(p.Id);
                            n++;
                        }
                        _sim.Emit(SimEventType.Info, LogLevel.Normal, $"Волна «{w.Def.Name}»: время вышло ({w.Def.MaxDuration:0} с)" + (n > 0 ? $", не появились {n}" : ""));
                    }
                    // Postponed appearances get a new time (and a new line) as soon as their limits let them.
                    int reserved = w.Planned.Count(p => !p.Done && !p.Postponed && p.Replanned);
                    foreach (var p in w.Planned.Where(p => !p.Done && p.Postponed))
                    {
                        if (AliveLimit(w, p, reserved) != null) continue;
                        p.Postponed = false;
                        p.Replanned = true;
                        p.WaitReason = "";
                        reserved++;
                        p.Tick = _sim.Tick + _sim.TicksFor(Warnings.LeadSeconds(w.Def, p.Def, p.Faction, p.From, p.Marker, p.Route));
                        Warnings.Plan(p.Id, w.Index, w.Def, p.Def, p.Faction, p.Marker, p.From, p.Tick, p.Route);
                    }
                    ProcessSpawns(w);
                    if (w.SpawnFinishedTick < 0 && w.StartedTick >= 0 && w.Planned.All(p => p.Done)) SpawnFinished(w);
                    if (w.State == WaveState.Active && w.Planned.All(p => p.Done))
                    {
                        bool cleared = w.Def.CustomEnd ? Evaluate(w.Def.EndCondition) : AliveOf(w) == 0;
                        if (cleared)
                        {
                            w.State = WaveState.Cleared;
                            w.ClearedTick = _sim.Tick;
                            _sim.Emit(SimEventType.Info, LogLevel.Normal, $"Волна «{w.Def.Name}» отражена");
                            _sim.Emit(SimEventType.WaveEnded, LogLevel.Diagnostic, $"Волна «{w.Def.Name}» завершена: отражена", value: w.Index);
                            Raise(ScenarioEventType.WaveEnded, null, "", w.Index);
                            BetweenWaves?.Invoke(w);
                            foreach (var a in w.Def.OnEnd) Execute(a, w.Def.Name, null);
                        }
                    }
                    break;
                }
            }
        }
    }

    private void SpawnFinished(WaveRuntime w)
    {
        w.SpawnFinishedTick = _sim.Tick;
        _sim.Emit(SimEventType.Info, LogLevel.Detail, $"Волна «{w.Def.Name}»: появление окончено — выпущено {w.UnitIds.Count} из {w.Total}", value: w.Index);
        Raise(ScenarioEventType.WaveSpawnFinished, null, "", w.Index);
    }

    /// <summary>«Условие отмены» of a group: its remaining appearances are cancelled once.</summary>
    private void CancelGroups(WaveRuntime w)
    {
        foreach (var g in w.Def.Groups)
        {
            if (g.CancelCondition == null || w.CancelledGroups.Contains(g.Id) || !Evaluate(g.CancelCondition)) continue;
            w.CancelledGroups.Add(g.Id);
            int n = 0;
            foreach (var p in w.Planned.Where(p => !p.Done && p.Group == g.Id))
            {
                p.Done = true;
                Warnings.Remove(p.Id);
                n++;
            }
            _sim.Emit(SimEventType.Info, LogLevel.Normal, $"Волна «{w.Def.Name}»: группа «{(g.Name != "" ? g.Name : g.Id)}» отменена условием" + (n > 0 ? $", не появились {n}" : ""));
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
        if (!_started)
        {
            _started = true;
            Raise(ScenarioEventType.ScenarioStart);
        }
        if (Preparing) UpdatePreparation();
        if (!WavesHeld) UpdateWaves();
        Warnings.Update();
        UpdateDelayed();
        UpdateTriggers();
        ProcessEvents();
        if (!WavesHeld)
        {
            UpdateLeaks();
            UpdateObjectives();
            UpdateBosses();
        }
        UpdateAiRules();
        ProcessEvents();
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
        if (tr.MaxFires > 0 && FiresOf(i) >= tr.MaxFires) return false;
        if (!tr.Once && _triggerNext.TryGetValue(i, out var next) && _sim.Tick < next)
        {
            Trace(tr, $"не чаще {tr.Cooldown:0.##} с — пропущено");
            return false;
        }
        if (tr.CheckOnActivate && !Evaluate(tr.Condition, actor))
        {
            Trace(tr, "условие ложно: " + Explain(tr.Condition, actor));
            return false;
        }
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
            _triggerFires[i] = FiresOf(i) + 1;
            if (!tr.Once && tr.Cooldown > 0) _triggerNext[i] = _sim.Tick + _sim.TicksFor(tr.Cooldown);
            Trace(tr, "запущен" + (tr.MaxFires > 0 ? $" ({FiresOf(i)} из {tr.MaxFires})" : ""));
            foreach (var a in tr.Actions) Execute(a, tr.Name, actor);
        }
        finally
        {
            _triggerDepth--;
        }
        return true;
    }

    /// <summary>Behaviour rules of AI profiles: condition (self = the unit) → actions (actor = the unit).</summary>
    /// <summary>
    /// Rules of AI profiles «если — то — иначе» (docs/DECISIONS_4.md §64): from the highest priority; the first rule
    /// whose branch sets a behaviour decides the behaviour of this tick; the actions of every branch run with the
    /// rule's once/cooldown (each branch separately). A rule without «иначе» acts as in part 2.
    /// </summary>
    private void UpdateAiRules()
    {
        foreach (var u in _sim.Units.ToList())
        {
            var p = u.AiProfile;
            if (!u.Alive || p == null) continue;
            if (p.Rules.Count == 0)
            {
                u.Ai.Directive = AiDirective.None;
                continue;
            }
            var directive = AiDirective.None;
            string by = "";
            foreach (var (r, i) in OrderedRules(p))
            {
                bool holds = Evaluate(r.When, u);
                var dir = holds ? r.Directive : r.ElseDirective;
                var acts = holds ? r.Actions : r.ElseActions;
                if (directive == AiDirective.None && dir != AiDirective.None)
                {
                    directive = dir;
                    by = holds ? r.Name : r.Name + " (иначе)";
                }
                if (acts.Count == 0) continue;
                var key = (u.Id, p, holds ? i : -(i + 1));
                if (_aiRules.TryGetValue(key, out var next) && next > _sim.Tick) continue;
                _aiRules[key] = r.Once ? long.MaxValue : _sim.Tick + Math.Max(1, _sim.TicksFor(r.Cooldown));
                _sim.Emit(SimEventType.AiDecision, LogLevel.Detail, $"{u.Name}: правило «{r.Name}»{(holds ? "" : " — иначе")}", u.Id);
                foreach (var a in acts) Execute(a, $"{u.Name}: {r.Name}", u);
                if (!u.Alive || u.AiProfile != p) break;
            }
            u.Ai.Directive = directive;
            u.Ai.DirectiveRule = by;
        }
    }

    private static IEnumerable<(AiRuleDef Rule, int Index)> OrderedRules(AiProfileDef p) =>
        p.Rules.Select((r, i) => (r, i)).OrderByDescending(x => x.r.Priority).ThenBy(x => x.i);

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
        if (a.Node != "") TraceNode(a.Node, a.Describe());
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
            case TriggerActionType.ClearFlag:
                if (_flags.Remove(a.Flag)) _sim.Emit(SimEventType.Info, LogLevel.Detail, $"Флаг «{a.Flag}» снят ({source})");
                break;
            case TriggerActionType.DelayedTrigger:
                // At least one tick later: a loop through a delay can never be instant (ТЗ 7.5).
                _delayed.Add((_sim.Tick + Math.Max(1, _sim.TicksFor(a.Delay)), a.Text));
                break;
            case TriggerActionType.IfElse:
            {
                bool yes = a.If == null || Evaluate(a.If, actor);
                string branch = yes ? a.Text : a.ElseTrigger;
                if (Def?.Triggers.FirstOrDefault(t => t.Name == source) is { GraphNode.Length: > 0 } owner)
                    Trace(owner, (yes ? "ветка «да»" : "ветка «нет»") + (a.If != null ? ": " + Explain(a.If, actor) : ""));
                if (branch != "") ActivateTrigger(branch, actor);
                break;
            }
            case TriggerActionType.SetEnvironment:
                SetVariable(a.Variable, a.VariableValue);
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
                var points = RoutePoints(a.Route);
                if (points == null) break;
                foreach (var u in Targets(a, actor))
                {
                    // A graph route: from the nearest node of it; a route of the map: from its first point.
                    _sim.Routes.Assign(u, a.Route, fromStart: false);
                    if (Def?.Route(a.Route) is { Target: not "" } r) u.TargetObject = r.Target;
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
            case TriggerActionType.ChangeScale:
                foreach (var u in Targets(a, actor)) _sim.Scales.Change(u, a.Amount, source, actor);
                break;
            case TriggerActionType.PlayVfx:
            case TriggerActionType.PlaySound:
            {
                var at = (string.IsNullOrEmpty(a.Marker) ? null : _sim.Map.Def.FindMarker(a.Marker))?.C ?? actor?.Pos;
                _sim.Emit(a.Type == TriggerActionType.PlayVfx ? SimEventType.Vfx : SimEventType.Sound, LogLevel.Diagnostic, a.Text, actor?.Id ?? -1, cell: at);
                break;
            }
            // Part 4, stage 5 (docs/DECISIONS_4.md §68).
            case TriggerActionType.ActivateBase:
                SetObjectActive(a.Object, true, source);
                break;
            case TriggerActionType.DeactivateBase:
                SetObjectActive(a.Object, false, source);
                break;
            case TriggerActionType.SwitchObjectState:
                _sim.Interactive.SetState(a.Object, a.ObjectStateId, source, out _);
                break;
            case TriggerActionType.OpenDeploymentZone:
            case TriggerActionType.CloseDeploymentZone:
                SetZoneOpen(a.Object, a.Type == TriggerActionType.OpenDeploymentZone, source);
                break;
            case TriggerActionType.SetGroupRoute:
                SetGroupRoute(a.Group, a.Route, source);
                break;
            case TriggerActionType.SetTarget:
                foreach (var u in Targets(a, actor))
                {
                    if (a.TargetTag != "")
                    {
                        u.Ai.ForcedTargetTag = a.TargetTag;
                        continue;
                    }
                    if (Def?.Object(a.Object) == null) break;
                    // A new target object: the enemy leaves its route and goes straight there.
                    u.TargetObject = a.Object;
                    u.Route = null;
                    u.Nav = null;
                    u.Ai.Alternative = null;
                }
                _sim.Emit(SimEventType.Info, LogLevel.Detail, $"{source}: {a.Describe()}");
                break;
            case TriggerActionType.SetObjectState:
            {
                var o = Def?.Object(a.Object);
                if (o == null) break;
                switch (a.ObjectState)
                {
                    case ObjectStateChange.Activate:
                    case ObjectStateChange.Deactivate:
                        SetObjectActive(o.Id, a.ObjectState == ObjectStateChange.Activate, source);
                        break;
                    case ObjectStateChange.Destroy:
                        if (_sim.Structures.Of(o.Id) is { Alive: true } s) _sim.Structures.DamageStructure(s, s.Hp + s.HpMax, null, source, force: true);
                        break;
                    case ObjectStateChange.Repair:
                        if (_sim.Structures.Of(o.Id) is { Alive: true } r)
                        {
                            r.Hp = r.HpMax;
                            _sim.Emit(SimEventType.Heal, LogLevel.Normal, $"«{o.DisplayName}» восстановлена ({source})", -1, r.Id, r.Pos);
                        }
                        break;
                }
                break;
            }
            case TriggerActionType.EndScenario:
                SetOutcome(a.Win ? BattleOutcome.Victory : BattleOutcome.Defeat, a.Text != "" ? a.Text : $"сценарий завершён: {source}");
                break;
            case TriggerActionType.LogDiagnostic:
                _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"Сценарий [{source}]: {(a.Text != "" ? a.Text : "отметка")}", actor?.Id ?? -1, -1, actor?.Pos);
                break;
        }
    }

    private readonly Dictionary<(string Unit, int Level), Dictionary<string, double>> _levelMults = new();

    /// <summary>
    /// Fits an instance to a level: the stat multipliers of «Подогнать под уровень» (statistics only) become the
    /// instance's modifiers; the template does not change. Computed once per template and level.
    /// </summary>
    public void ApplyLevel(Unit u, int level)
    {
        if (level <= 0 || u.IsStructure) return;
        var key = (u.Def.Id, level);
        if (!_levelMults.TryGetValue(key, out var mults)) _levelMults[key] = mults = LevelFit.InstanceMultipliers(_sim.Content, u.Def, level);
        foreach (var (stat, m) in mults)
            if (Math.Abs(m - 1) > 1e-9)
                u.InstanceMods.Add((new StatModDef { Stat = stat, Mul = m - 1 }, $"уровень {level}"));
        u.InstanceLevel = level;
        _sim.Statuses.RecalculateModifiers(u);
        u.Hp = u.HpMax;
    }

    /// <summary>A wave was cleared (between waves: restore, relocation — the placement system listens).</summary>
    public event Action<WaveRuntime>? BetweenWaves;

    /// <summary>«Сменить фазу босса»: the phase starts now, regardless of its condition (once, unless it may repeat).</summary>
    private void ForcePhase(Unit u, int index, string source)
    {
        var phases = u.Boss.Phases;
        if (!u.Boss.IsBoss || index < 0 || index >= phases.Count) return;
        if (_bossPhases.TryGetValue(u.Id, out var fired) && fired.Contains(index) && !phases[index].CanRepeat) return;
        if (CurrentPhase(u) == index) return;
        EnterPhase(u, index, "по действию «" + source + "»");
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
            if (c.StartsWith("tag:", StringComparison.Ordinal) && u.HasTag(c[4..])) return true;
        }
        return false;
    }

    /// <summary>All waves have started (or are cancelled) — the field cannot get new enemies by waves.</summary>
    private bool WavesDone => _waves.All(w => w.State is WaveState.Cancelled or WaveState.Cleared || w.State == WaveState.Active && w.Planned.All(p => p.Done));

    /// <summary>The delayed triggers whose tick has come, in the order they were set.</summary>
    private void UpdateDelayed()
    {
        if (_delayed.Count == 0) return;
        var due = _delayed.Where(d => d.Tick <= _sim.Tick).ToList();
        if (due.Count == 0) return;
        _delayed.RemoveAll(d => d.Tick <= _sim.Tick);
        foreach (var (_, name) in due) ActivateTrigger(name, null);
    }

    /// <summary>
    /// A condition with what it is made of now (ТЗ ч. 6, 7.6, T23): «Прошло 120 с: нет (сейчас 95.3 с)». Evaluating it does not
    /// change the battle (except the latch of «по порядку», which it reads only).
    /// </summary>
    public string Explain(ConditionDef c, Unit? self = null)
    {
        string yes(bool v) => v ? "да" : "нет";
        switch (c.Type)
        {
            case ConditionType.All:
            case ConditionType.Any:
                return (c.Type == ConditionType.All ? "все: " : "любое: ") + string.Join("; ", c.Children.Select(x => Explain(x, self)));
            case ConditionType.Not:
                return "не (" + string.Join("; ", c.Children.Select(x => Explain(x, self))) + ")";
            case ConditionType.InOrder:
                return $"{c.Describe()}: пройдено {_orderProgress.GetValueOrDefault(c)} из {c.Children.Count}";
            case ConditionType.SurviveTime:
            case ConditionType.TimeElapsed:
                return $"{c.Describe()}: {yes(BattleTime >= c.Seconds)} (сейчас {BattleTime:0.#} с)";
            case ConditionType.FlagSet:
                return $"{c.Describe()}: {yes(_flags.Contains(c.Flag))}";
            case ConditionType.ObjectInState:
                return $"{c.Describe()}: {yes(_sim.Interactive.StateOf(c.ObjectId) == c.ObjectState)} (сейчас «{_sim.Interactive.StateOf(c.ObjectId)}»)";
            case ConditionType.UnitDead:
            case ConditionType.UnitAlive:
                return $"{c.Describe()}: {yes(Evaluate(c, self))} (с меткой: {_sim.Units.Count(u => u.Tag == c.UnitTag)}, живых {_sim.Units.Count(u => u.Tag == c.UnitTag && u.Alive)})";
            case ConditionType.Environment:
                return $"{c.Describe()}: {yes(Evaluate(c, self))} (сейчас «{Variable(c.Variable)}»)";
            default:
                return $"{c.Describe()}: {yes(Evaluate(c, self))}";
        }
    }

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
                return BattleTime >= c.Seconds;
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
            case ConditionType.Environment:
                return Variable(c.Variable) == c.VariableValue;
            case ConditionType.ObjectInState:
                return _sim.Interactive.StateOf(c.ObjectId) == c.ObjectState;
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
                    ? self != null && self.Boss.IsBoss ? new[] { self } : _sim.Units.Where(u => u.Boss.IsBoss)
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
                    var bosses = _sim.Units.Where(u => string.IsNullOrEmpty(d.UnitTag) ? u.Boss.IsBoss : u.Tag == d.UnitTag).ToList();
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
            if (!u.Alive || !u.Boss.IsBoss || u.Boss.Phases.Count == 0) continue;
            var phases = u.Boss.Phases;
            // Several transitions may follow one another in a tick (a big hit passes two thresholds), each phase once in it.
            var now = new HashSet<int>();
            for (int step = 0; step < phases.Count; step++)
            {
                int cur = CurrentPhase(u);
                int best = -1;
                for (int i = 0; i < phases.Count; i++)
                {
                    if (i == cur || now.Contains(i) || !CanEnter(u, i, cur) || !Evaluate(phases[i].When, u)) continue;
                    if (best < 0 || phases[i].Priority > phases[best].Priority) best = i;
                }
                if (best < 0) break;
                now.Add(best);
                EnterPhase(u, best, "условие: " + phases[best].When.Describe());
            }
        }
    }

    /// <summary>
    /// May a boss enter a phase now (ТЗ 8.2): not entered yet or «может повториться»; «из фаз» — the phase it is in («start» — before
    /// the first, «*» — any), empty — after the previous one of the list (the first — from the start).
    /// </summary>
    private bool CanEnter(Unit u, int i, int cur)
    {
        var ph = u.Boss.Phases[i];
        _bossPhases.TryGetValue(u.Id, out var fired);
        if (fired != null && fired.Contains(i) && !ph.CanRepeat) return false;
        if (ph.From.Count == 0) return i == 0 || fired != null && fired.Contains(i - 1);
        if (ph.From.Contains("*")) return true;
        return cur < 0 ? ph.From.Contains(BossPhases.Start) : ph.From.Contains(BossPhases.IdOf(u.Boss, cur));
    }

    /// <summary>
    /// The boss enters a phase: its message, the explicit rules of the transition (cooldowns, AP, health, statuses — none by
    /// default, ТЗ 8.4), then its actions; each written to the log with the phase as its source.
    /// </summary>
    private void EnterPhase(Unit u, int index, string why)
    {
        var ph = u.Boss.Phases[index];
        if (!_bossPhases.TryGetValue(u.Id, out var fired)) _bossPhases[u.Id] = fired = new HashSet<int>();
        fired.Add(index);
        _bossCurrent[u.Id] = index;
        _bossEntries[u.Id] = _bossEntries.GetValueOrDefault(u.Id) + 1;
        string src = $"фаза «{ph.Name}» ({u.Name})";
        PhaseEntered?.Invoke(u, index);
        _sim.Emit(SimEventType.Message, LogLevel.Normal, string.IsNullOrEmpty(ph.Message) ? $"{u.Name}: фаза «{ph.Name}»" : ph.Message, u.Id);
        _sim.Emit(SimEventType.Info, LogLevel.Detail, $"{u.Name}: переход в фазу «{ph.Name}» — {why}", u.Id);
        if (ph.ResetCooldowns && u.CooldownUntil.Count > 0)
        {
            u.CooldownUntil.Clear();
            _sim.Emit(SimEventType.Info, LogLevel.Normal, $"{u.Name}: перезарядки сброшены — {src}", u.Id);
        }
        if (ph.RefillAp && u.Ap < u.ApMax)
        {
            u.Ap = u.ApMax;
            _sim.Emit(SimEventType.Info, LogLevel.Normal, $"{u.Name}: ОД восполнены — {src}", u.Id);
        }
        if (ph.Heal > 0 && u.Hp < u.HpMax)
        {
            double before = u.Hp;
            u.Hp = Math.Min(u.HpMax, u.Hp + u.HpMax * ph.Heal);
            _sim.Emit(SimEventType.Heal, LogLevel.Normal, $"{u.Name}: +{u.Hp - before:0} здоровья — {src}", u.Id, u.Id, value: u.Hp - before);
        }
        foreach (var st in ph.ClearStatuses) _sim.Statuses.RemoveById(u, st, src);
        foreach (var a in ph.Actions)
        {
            _sim.Emit(SimEventType.Info, LogLevel.Detail, $"{src}: {a.Describe()}", u.Id);
            Execute(a, src, u);
        }
    }

    /// <summary>«Телеграф атаки» of the scenario for a caster and a skill (the first that matches; null — none).</summary>
    public TelegraphRuleDef? TelegraphFor(Unit u, SkillDef s)
    {
        if (Def == null || Def.Telegraphs.Count == 0) return null;
        foreach (var t in Def.Telegraphs)
            if (t.Skill == s.Id && (t.Caster == "" || u.Tag == t.Caster || _sim.Conditions.TypeMatches(u, t.Caster)))
                return t;
        return null;
    }

    /// <summary>A boss entered a phase (the scales listen: «босс сменил фазу»).</summary>
    public event Action<Unit, int>? PhaseEntered;

    /// <summary>The phase a boss is in (an index of its list; −1 — before the first).</summary>
    public int CurrentPhase(Unit u) => _bossCurrent.TryGetValue(u.Id, out var i) ? i : -1;

    /// <summary>How many times a boss changed its phase.</summary>
    public int PhaseEntries(Unit u) => _bossEntries.GetValueOrDefault(u.Id);

    /// <summary>Current phase number of a boss, from 1 (0 — before the first phase).</summary>
    public int PhaseNumberOf(Unit u) => CurrentPhase(u) + 1;

    /// <summary>Current phase name of a boss ("" — before the first phase).</summary>
    public string PhaseOf(Unit u)
    {
        int i = CurrentPhase(u);
        return i >= 0 && i < u.Boss.Phases.Count ? u.Boss.Phases[i].Name : "";
    }

    // ------------------------------------------------------------------ outcome

    public void CheckOutcome()
    {
        if (Def == null || _sim.Outcome != BattleOutcome.None || WavesHeld) return;
        if (Def.Limits.TimeLimit > 0 && BattleTime >= Def.Limits.TimeLimit)
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
        if (_sim.Structures.BaseDefeat(Def) is { } lost)
        {
            SetOutcome(BattleOutcome.Defeat, lost);
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
        // «Завершение боя»: the last events are processed at once (the simulation stops after this tick).
        Raise(ScenarioEventType.BattleEnded);
        ProcessEvents();
    }
}
