using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>An interaction in progress: the unit is busy until the tick, then the object acts.</summary>
public sealed record ObjectInteraction(int Unit, string Object, long EndTick);

/// <summary>
/// Interactive objects (ТЗ ч. 6, 6; docs/DECISIONS_6.md §100): the states of an object, the terrain of its hexes by the
/// state, interactions and signals to other objects. No game system of its own: the terrain changes through the common
/// service of the map (<see cref="CellEffectSystem.ChangeTerrain"/>), the actions of a state are the actions of the
/// scenario, the turret is an auto-attack of the structure, health is the structure's. A change of the terrain under
/// standing units follows the object's policy — never a silent teleport.
/// </summary>
public sealed class ObjectSystem
{
    /// <summary>A chain of signals deeper than this is cut (a switch that switches itself back and forth).</summary>
    public const int MaxChain = 16;

    private readonly Simulation _sim;
    private readonly Dictionary<string, string> _state = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _cooldownUntil = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);
    private readonly Dictionary<HexCoord, TerrainDef> _mapTerrain = new();
    private readonly List<ObjectInteraction> _busy = new();
    private int _depth;

    public ObjectSystem(Simulation sim) => _sim = sim;

    public IReadOnlyList<ObjectInteraction> InProgress => _busy;

    private ScenarioObjectDef? Obj(string id) => _sim.Scenario.Def?.Object(id);

    /// <summary>The current state of an object ("" — it has no states).</summary>
    public string StateOf(string objectId) => _state.GetValueOrDefault(objectId, "");

    /// <summary>The state an object waits to enter (a deferred change, "" — none).</summary>
    public string PendingOf(string objectId) => _pending.GetValueOrDefault(objectId, "");

    public ObjectStateDef? CurrentState(string objectId) => Obj(objectId)?.States?.Get(StateOf(objectId));

    /// <summary>The objects with states, as «id=state[→pending]» (the fingerprint of the battle, T28).</summary>
    public IEnumerable<string> Digest() => _state.OrderBy(k => k.Key, StringComparer.Ordinal)
        .Select(k => $"{k.Key}={k.Value}" + (_pending.TryGetValue(k.Key, out var p) ? "→" + p : "") + (_used.Contains(k.Key) ? "!" : ""))
        .Concat(_busy.Select(b => $"busy:{b.Unit}:{b.Object}:{b.EndTick}"));

    internal void Load(ScenarioDef sc)
    {
        foreach (var o in sc.Objects)
        {
            if (o.States is not { States.Count: > 0 } st || o.Id == "") continue;
            foreach (var c in o.Cells())
                if (_sim.Map.Get(c) is { } cell)
                    _mapTerrain.TryAdd(c, cell.Terrain);
            string first = st.Get(st.First) != null ? st.First : st.States[0].Id;
            _state[o.Id] = first;
            ApplyTerrain(o, st.Get(first)!, "начальное состояние");
            ApplyTurret(o);
        }
    }

    // ------------------------------------------------------------------ states

    /// <summary>The hexes of the object whose terrain the state would change, and to what.</summary>
    private List<(Cell Cell, TerrainDef To)> Changes(ScenarioObjectDef o, ObjectStateDef s)
    {
        var list = new List<(Cell, TerrainDef)>();
        foreach (var c in o.Cells())
        {
            if (_sim.Map.Get(c) is not { } cell) continue;
            var to = s.Terrain != "" ? _sim.Content.Terrain(s.Terrain) : _mapTerrain.GetValueOrDefault(c);
            if (to != null && to != cell.Terrain) list.Add((cell, to));
        }
        return list;
    }

    /// <summary>Units that cannot stay on a hex once it becomes <paramref name="to"/> (structures of the object aside).</summary>
    private static List<Unit> Stuck(Cell cell, TerrainDef to, ScenarioObjectDef o) =>
        cell.Occupants.Where(u => u.Alive && u.ObjectId != o.Id && (to.Kind is TerrainKind.Obstacle or TerrainKind.Pit || !to.IsPassableFor(u.Def.MoveCategory)) && u.Layer != "air").ToList();

    /// <summary>
    /// Switches an object into a state (one transaction): the hexes under it are checked against the policy first, then the
    /// terrain changes through the map service, the turret follows, the actions of the state run and «ObjectStateChanged»
    /// is raised. False and the reason — nothing changed (or the change is deferred: then the reason says so).
    /// </summary>
    public bool SetState(string objectId, string stateId, string source, out string why)
    {
        why = "";
        if (!_sim.Options.Objects)
        {
            why = "интерактивные объекты выключены правилами режима";
            return false;
        }
        var o = Obj(objectId);
        if (o?.States is not { } st) { why = $"у объекта «{objectId}» нет состояний"; return false; }
        if (stateId == "") stateId = NextOf(o);
        var s = st.Get(stateId);
        if (s == null) { why = $"у объекта «{o.DisplayName}» нет состояния «{stateId}»"; return false; }
        if (StateOf(objectId) == stateId) { _pending.Remove(objectId); return true; }
        if (_depth >= MaxChain)
        {
            why = $"цепочка сигналов длиннее {MaxChain} — оборвана";
            _sim.Emit(SimEventType.Diagnostic, LogLevel.Normal, $"«{o.DisplayName}»: {why} ({source})");
            return false;
        }
        var changes = Changes(o, s);
        var stuck = changes.SelectMany(ch => Stuck(ch.Cell, ch.To, o).Select(u => (u, ch.Cell, ch.To))).ToList();
        if (stuck.Count > 0)
        {
            switch (st.WhenOccupied)
            {
                case OccupiedCellPolicy.Forbid:
                    why = $"на гексах «{o.DisplayName}» стоят юниты ({string.Join(", ", stuck.Select(x => x.u.Name).Distinct())}) — переключение запрещено";
                    _sim.Emit(SimEventType.Info, LogLevel.Normal, why);
                    return false;
                case OccupiedCellPolicy.Defer:
                    return Defer(o, stateId, out why);
                case OccupiedCellPolicy.Evacuate:
                case OccupiedCellPolicy.Fall:
                {
                    var affected = changes.Select(ch => ch.Cell.Coord).ToHashSet();
                    foreach (var (u, cell, to) in stuck)
                    {
                        if (st.WhenOccupied == OccupiedCellPolicy.Fall && to.Kind == TerrainKind.Pit) continue; // the pit takes them by its rules
                        var free = NearestFree(u, cell.Coord, affected);
                        if (free == null) return Defer(o, stateId, out why);
                        _sim.Commands.ClearQueue(u, $"выведен: «{o.DisplayName}» меняет состояние", CommandStatus.Cancelled);
                        _sim.Relocate(u, free.Value, null, $"«{o.DisplayName}»: {s.DisplayName}");
                        _sim.Emit(SimEventType.Info, LogLevel.Normal, $"{u.Name} выведен с {cell.Coord} на {free.Value}: «{o.DisplayName}» — {s.DisplayName}", u.Id, cell: free.Value);
                    }
                    break;
                }
            }
        }
        Enter(o, s, changes, source);
        return true;
    }

    private bool Defer(ScenarioObjectDef o, string stateId, out string why)
    {
        bool was = _pending.ContainsKey(o.Id);
        _pending[o.Id] = stateId;
        why = $"«{o.DisplayName}»: на гексах юниты — смена состояния отложена";
        if (!was) _sim.Emit(SimEventType.Info, LogLevel.Normal, why);
        return false;
    }

    private void Enter(ScenarioObjectDef o, ObjectStateDef s, List<(Cell Cell, TerrainDef To)> changes, string source)
    {
        _pending.Remove(o.Id);
        string from = StateOf(o.Id);
        _state[o.Id] = s.Id;
        foreach (var (cell, to) in changes) _sim.CellEffects.ChangeTerrain(cell, to, $"«{o.DisplayName}»: {s.DisplayName}");
        ApplyTurret(o);
        _sim.Emit(SimEventType.Info, LogLevel.Normal, $"«{o.DisplayName}»: {o.States!.Get(from)?.DisplayName ?? from} → {s.DisplayName} ({source})");
        _depth++;
        try
        {
            foreach (var a in s.OnEnter) _sim.Scenario.Execute(a, $"«{o.DisplayName}»: {s.DisplayName}", null);
        }
        finally
        {
            _depth--;
        }
        _sim.Scenario.Raise(ScenarioEventType.ObjectStateChanged, null, o.Id);
    }

    private void ApplyTerrain(ScenarioObjectDef o, ObjectStateDef s, string cause)
    {
        foreach (var (cell, to) in Changes(o, s)) _sim.CellEffects.ChangeTerrain(cell, to, $"«{o.DisplayName}»: {cause}");
    }

    private void ApplyTurret(ScenarioObjectDef o)
    {
        if (o.Turret == null || _sim.Structures.Of(o.Id) is not { } u) return;
        bool on = o.States == null || CurrentState(o.Id)?.TurretActive != false;
        if (on) _sim.AutoAttacks.DisabledUnits.Remove(u.Id);
        else _sim.AutoAttacks.DisabledUnits.Add(u.Id);
    }

    private string NextOf(ScenarioObjectDef o)
    {
        var list = o.States!.States;
        int i = list.FindIndex(x => x.Id == StateOf(o.Id));
        return list.Count == 0 ? "" : list[(i + 1) % list.Count].Id;
    }

    /// <summary>The nearest hex a unit can stand on outside the hexes being changed.</summary>
    private HexCoord? NearestFree(Unit u, HexCoord from, HashSet<HexCoord> affected)
    {
        var mc = MoveContext.For(_sim, u);
        for (int r = 1; r <= 4; r++)
            foreach (var c in HexCoord.Ring(from, r).OrderBy(c => c.R).ThenBy(c => c.Q))
                if (!affected.Contains(c) && _sim.Map.Get(c) is { } cell && !cell.Blocked && mc.Passable(cell.Terrain) && !cell.IsHazard
                    && cell.Terrain.Kind != TerrainKind.Pit && Occupancy.CanEnter(mc, cell) && cell.Occupants.All(x => !x.Alive))
                    return c;
        return null;
    }

    // ------------------------------------------------------------------ interactions

    /// <summary>Why the unit cannot use the object now (who, distance, ОД, state, pause, once); null — it can.</summary>
    public string? CanInteract(Unit u, string objectId)
    {
        var o = Obj(objectId);
        if (o?.Interaction is not { } it) return "с объектом нельзя взаимодействовать";
        if (!u.Alive) return "юнит выбыл";
        if (_sim.Structures.Of(objectId) is { Alive: false }) return "объект разрушен";
        bool ok = it.Who switch
        {
            InteractionWho.Player => _sim.IsPlayerFaction(u.FactionId),
            InteractionWho.Allies => _sim.Content.RelationOf(u.FactionId, o.Faction) != Relation.Enemy,
            InteractionWho.Enemies => _sim.Content.RelationOf(u.FactionId, o.Faction) == Relation.Enemy,
            _ => true,
        };
        if (!ok) return "этому юниту нельзя";
        if (_busy.Any(b => b.Unit == u.Id)) return "юнит уже занят взаимодействием";
        if (_busy.Any(b => b.Object == objectId)) return "объектом уже пользуются";
        if (it.Once && _used.Contains(objectId)) return "уже использован";
        if (_cooldownUntil.TryGetValue(objectId, out var until) && until > _sim.Tick) return $"перезарядка: ещё {(until - _sim.Tick) * _sim.Dt:0.#} с";
        if (it.RequiredStates.Count > 0 && !it.RequiredStates.Contains(StateOf(objectId))) return $"не в том состоянии ({CurrentState(objectId)?.DisplayName ?? StateOf(objectId)})";
        int d = o.Cells().Min(c => HexCoord.Distance(c, u.Pos));
        if (d > it.Range) return $"далеко: {d} гекс., нужно не дальше {it.Range}";
        if (u.Flags.BlocksActions) return "юнит не может действовать";
        if (it.ApCost > 0 && u.Ap + 1e-9 < it.ApCost) return $"не хватает ОД: нужно {it.ApCost:0.#}";
        if (it.Effect == InteractionEffect.Signal && Obj(it.Target) == null) return $"получателя «{it.Target}» нет";
        return null;
    }

    /// <summary>Starts an interaction: ОД spent, the unit busy for the duration; the effect comes when it ends.</summary>
    public bool Interact(Unit u, string objectId, out string why)
    {
        why = !_sim.Options.Objects ? "интерактивные объекты выключены правилами режима" : CanInteract(u, objectId) ?? "";
        if (why != "") return false;
        var it = Obj(objectId)!.Interaction!;
        if (it.ApCost > 0) u.Ap -= it.ApCost;
        _sim.Commands.ClearQueue(u, "взаимодействие", CommandStatus.Cancelled);
        long end = _sim.Tick + _sim.TicksFor(it.Duration);
        u.BusyUntil = Math.Max(u.BusyUntil, end);
        _busy.Add(new ObjectInteraction(u.Id, objectId, end));
        _sim.Emit(SimEventType.Info, LogLevel.Normal, $"{u.Name}: «{it.Prompt}» — {Obj(objectId)!.DisplayName}" + (it.Duration > 0 ? $" ({it.Duration:0.#} с)" : ""), u.Id);
        if (end <= _sim.Tick) Update();
        return true;
    }

    /// <summary>The tick: finished interactions act, interrupted ones end, deferred states retry, destroyed objects break.</summary>
    internal void Update()
    {
        for (int i = 0; i < _busy.Count; i++)
        {
            var b = _busy[i];
            var u = _sim.GetUnit(b.Unit);
            var o = Obj(b.Object);
            if (u == null || !u.Alive || u.Flags.BlocksActions || o == null)
            {
                _busy.RemoveAt(i--);
                if (u != null) u.BusyUntil = Math.Min(u.BusyUntil, _sim.Tick);
                _sim.Emit(SimEventType.Info, LogLevel.Normal, $"Взаимодействие с «{o?.DisplayName ?? b.Object}» прервано");
                continue;
            }
            if (_sim.Tick < b.EndTick) continue;
            _busy.RemoveAt(i--);
            Complete(u, o);
        }
        foreach (var (id, state) in _pending.ToList())
            SetState(id, state, "отложенная смена", out _);
        var sc = _sim.Scenario.Def;
        if (sc == null) return;
        foreach (var o in sc.Objects)
            if (o.States is { OnDestroyed.Length: > 0 } st && StateOf(o.Id) != st.OnDestroyed && !_pending.ContainsKey(o.Id)
                && _sim.Structures.Of(o.Id) is { Alive: false })
                SetState(o.Id, st.OnDestroyed, "объект разрушен", out _);
    }

    private void Complete(Unit u, ScenarioObjectDef o)
    {
        var it = o.Interaction!;
        _used.Add(o.Id);
        if (it.Cooldown > 0) _cooldownUntil[o.Id] = _sim.Tick + _sim.TicksFor(it.Cooldown);
        string src = $"{u.Name}: «{it.Prompt}»";
        string why;
        switch (it.Effect)
        {
            case InteractionEffect.SetState:
                SetState(o.Id, it.State, src, out why);
                break;
            case InteractionEffect.NextState:
                if (o.States != null) SetState(o.Id, "", src, out why);
                break;
            case InteractionEffect.Signal:
                if (Obj(it.Target) is { States: not null }) SetState(it.Target, it.TargetState, src, out why);
                break;
        }
        // The common bus of events: the triggers of the scenario hear «активация объекта».
        _sim.Scenario.Raise(ScenarioEventType.ObjectActivated, u, o.Id);
        if (it.Effect == InteractionEffect.Signal) _sim.Scenario.Raise(ScenarioEventType.ObjectActivated, u, it.Target);
    }
}
