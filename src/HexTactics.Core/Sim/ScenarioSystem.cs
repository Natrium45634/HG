using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>Scenario runtime: initial units, waves, triggers, victory and defeat conditions.</summary>
public sealed class ScenarioSystem
{
    private readonly Simulation _sim;
    private readonly HashSet<int> _spawnedWaves = new();
    private readonly HashSet<int> _firedTriggers = new();
    private bool _hadPlayerUnits;

    public ScenarioSystem(Simulation sim)
    {
        _sim = sim;
    }

    public ScenarioDef? Def { get; private set; }
    public bool ContinueAfterOutcome { get; set; }
    public IReadOnlyCollection<int> SpawnedWaves => _spawnedWaves;

    /// <summary>Creates a simulation for a scenario (sides → player/AI factions, units placed).</summary>
    public static Simulation Create(ContentDatabase db, ScenarioDef sc, ulong? seed = null, bool admin = false)
    {
        var map = db.Get<MapDef>(sc.Map) ?? throw new InvalidOperationException($"Карта '{sc.Map}' не найдена");
        var player = new HashSet<string>(sc.Sides.Where(s => s.Controller == SideController.Player).Select(s => s.Faction));
        var ai = new HashSet<string>(sc.Sides.Where(s => s.Controller == SideController.Ai).Select(s => s.Faction));
        ulong s0 = seed ?? (sc.Seed != 0 ? sc.Seed : (ulong)DateTime.UtcNow.Ticks);
        var sim = new Simulation(db, map, new SimOptions
        {
            Seed = s0,
            PlayerFactions = player,
            AiFactions = ai,
            Stacking = sc.Limits.Stacking,
            FogOfWar = sc.Limits.FogOfWar && db.Rules.Vision.FogOfWar,
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
            _sim.Spawn(def, string.IsNullOrEmpty(su.Faction) ? null : su.Faction, su.C, su.Facing,
                string.IsNullOrEmpty(su.Name) ? null : su.Name, su.Tag, string.IsNullOrEmpty(su.AiProfile) ? null : su.AiProfile);
        }
        _hadPlayerUnits = _sim.Units.Any(u => _sim.IsPlayerFaction(u.FactionId));
        if (!string.IsNullOrWhiteSpace(sc.Intro)) _sim.Emit(SimEventType.Message, LogLevel.Normal, sc.Intro);
        _sim.Vision.Recompute();
    }

    public void Update()
    {
        if (Def == null) return;
        double t = _sim.Time;
        for (int i = 0; i < Def.Waves.Count; i++)
            if (!_spawnedWaves.Contains(i) && t >= Def.Waves[i].Time)
                SpawnWave(i);
        for (int i = 0; i < Def.Triggers.Count; i++)
        {
            var tr = Def.Triggers[i];
            if (tr.Once && _firedTriggers.Contains(i)) continue;
            if (!Evaluate(tr.Condition)) continue;
            _firedTriggers.Add(i);
            foreach (var a in tr.Actions) Execute(a, tr);
            if (!tr.Once) _firedTriggers.Remove(i);
        }
    }

    public void SpawnWave(int index)
    {
        if (Def == null || index < 0 || index >= Def.Waves.Count || !_spawnedWaves.Add(index)) return;
        var w = Def.Waves[index];
        var marker = _sim.Map.Def.FindMarker(w.SpawnMarker);
        if (marker == null)
        {
            _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"Волна «{w.Name}»: метка '{w.SpawnMarker}' не найдена");
            return;
        }
        var center = MapCenter();
        int spawned = 0;
        foreach (var wu in w.Units)
        {
            var def = _sim.Content.Unit(wu.Unit);
            if (def == null) continue;
            for (int k = 0; k < wu.Count; k++)
            {
                var cell = FindFreeCell(marker.C, def, Math.Max(3, marker.Radius + 3));
                if (cell == null)
                {
                    _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"Волна «{w.Name}»: нет свободной клетки у {marker.C}");
                    break;
                }
                var facing = cell.Value.DirectionTo(center) ?? HexDirection.W;
                try
                {
                    _sim.Spawn(def, w.Faction, cell.Value, facing);
                    spawned++;
                }
                catch (InvalidOperationException e)
                {
                    _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, e.Message);
                    break;
                }
            }
        }
        string msg = string.IsNullOrWhiteSpace(w.Message) ? $"Волна «{w.Name}»: {spawned} противников" : w.Message;
        _sim.Emit(SimEventType.WaveStarted, LogLevel.Normal, msg, cell: marker.C);
        if (_sim.Rules.Autopause.WaveStarted) _sim.RequestAutopause("началась волна");
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
        foreach (var c in HexCoord.Spiral(center, radius))
        {
            var cell = _sim.Map.Get(c);
            if (cell == null || !cell.AllowsPlacement) continue;
            if (MovementRules.Check(MoveContext.For(_sim, def, null), null, cell, false, out _) != StepCheck.Ok) continue;
            return c;
        }
        return null;
    }

    private void Execute(TriggerActionDef a, TriggerDef tr)
    {
        switch (a.Type)
        {
            case TriggerActionType.Message:
                _sim.Emit(SimEventType.Message, LogLevel.Normal, string.IsNullOrEmpty(a.Text) ? tr.Name : a.Text);
                break;
            case TriggerActionType.SpawnWave:
                SpawnWave(a.Wave);
                break;
            case TriggerActionType.Autopause:
                _sim.RequestAutopause(string.IsNullOrEmpty(a.Text) ? tr.Name : a.Text);
                break;
            case TriggerActionType.Victory:
                SetOutcome(BattleOutcome.Victory, string.IsNullOrEmpty(a.Text) ? tr.Name : a.Text);
                break;
            case TriggerActionType.Defeat:
                SetOutcome(BattleOutcome.Defeat, string.IsNullOrEmpty(a.Text) ? tr.Name : a.Text);
                break;
            case TriggerActionType.AddCellEffect:
            {
                var m = _sim.Map.Def.FindMarker(a.Marker);
                var def = _sim.Content.CellEffect(a.CellEffect);
                if (m == null || def == null) break;
                foreach (var cell in _sim.Map.InRadius(m.C, m.Radius)) _sim.CellEffects.Add(cell, def, 1, 0, -1, 0, 0);
                break;
            }
        }
    }

    public bool Evaluate(ConditionDef c)
    {
        switch (c.Type)
        {
            case ConditionType.AllEnemiesDead:
            {
                if (Def != null && _spawnedWaves.Count < Def.Waves.Count) return false;
                foreach (var u in _sim.Units)
                {
                    if (!u.Alive) continue;
                    foreach (var pf in _sim.Options.PlayerFactions)
                        if (_sim.Content.RelationOf(pf, u.FactionId) == Relation.Enemy)
                            return false;
                }
                return true;
            }
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
            case ConditionType.UnitDead:
                return _sim.Units.Any(u => u.Tag == c.UnitTag && !u.Alive);
            case ConditionType.UnitAlive:
                return _sim.Units.Any(u => u.Tag == c.UnitTag && u.Alive);
            case ConditionType.KillCount:
            {
                int kills = 0;
                foreach (var pf in _sim.Options.PlayerFactions)
                    if (_sim.Combat.KillsByFaction.TryGetValue(pf, out var k))
                        kills += k;
                return kills >= c.Count;
            }
            case ConditionType.WaveSpawned:
                return _spawnedWaves.Contains(c.Wave);
        }
        return false;
    }

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
        foreach (var c in Def.Victory)
            if (Evaluate(c))
            {
                SetOutcome(BattleOutcome.Victory, c.Describe());
                return;
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
