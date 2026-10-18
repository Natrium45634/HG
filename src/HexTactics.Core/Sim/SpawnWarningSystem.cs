using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>
/// Warning line of one planned spawn (docs/DECISIONS_2.md §25): simulation data, not a combat object.
/// The head follows the future path with the step timing of this enemy.
/// </summary>
public sealed class SpawnWarning
{
    public int Id { get; init; }
    public int SpawnId { get; init; }
    public int Wave { get; init; }
    public string UnitDef { get; init; } = "";
    public string Marker { get; init; } = "";
    public string Route { get; init; } = "";
    public HexCoord From { get; set; }
    /// <summary>Path cells from the spawn cell (inclusive) along the route.</summary>
    public List<HexCoord> Path { get; set; } = new();
    /// <summary>Seconds (warning time) at which the head reaches Path[i]; Times[0] = 0.</summary>
    public List<double> Times { get; set; } = new();
    public double Duration => Times.Count == 0 ? 0 : Times[^1];
    public long SpawnTick { get; set; }
    public long StartTick { get; set; }
    public long VanishTick { get; set; }
    public int MapVersion { get; set; }
    /// <summary>Shown at all by the settings (global / scenario / wave / unit type / spawn point).</summary>
    public bool Enabled { get; set; } = true;
}

public sealed class SpawnWarningSystem
{
    private readonly Simulation _sim;
    private readonly Dictionary<int, SpawnWarning> _bySpawn = new();
    private int _nextId = 1;

    public SpawnWarningSystem(Simulation sim)
    {
        _sim = sim;
    }

    public IEnumerable<SpawnWarning> All => _bySpawn.Values;

    private SpawnWarningRules Rules => _sim.Rules.SpawnWarnings;

    public WarningFog FogMode
    {
        get
        {
            var sc = _sim.Scenario.Def?.SpawnWarnings;
            return sc != null && sc.OverrideFog ? sc.Fog : Rules.Fog;
        }
    }

    /// <summary>Every "off" switch: global, scenario, wave, unit type, spawn point.</summary>
    public bool EnabledFor(WaveDef w, UnitDef def, string marker)
    {
        if (!Rules.Enabled) return false;
        var sc = _sim.Scenario.Def?.SpawnWarnings;
        if (sc != null && !sc.Enabled) return false;
        if (!w.Warning || !def.SpawnWarning) return false;
        var m = _sim.Map.Def.FindMarker(marker);
        return m == null || m.SpawnWarning;
    }

    private double MinSeconds(WaveDef w) => w.WarningSeconds > 0 ? w.WarningSeconds : Rules.MinSeconds;

    /// <summary>The future path of an enemy from its spawn cell along the route (rules of its movement, units ignored).</summary>
    public (List<HexCoord> Path, List<double> Times) PathOf(UnitDef def, string faction, HexCoord from, string routeId)
    {
        var path = new List<HexCoord> { from };
        var times = new List<double> { 0 };
        var route = string.IsNullOrEmpty(routeId) ? null : _sim.Map.Def.FindRoute(routeId);
        if (route == null || route.Points.Count == 0) return (path, times);
        var mc = MoveContext.For(_sim, def, faction);
        double speed = Math.Max(0.05, ExpectedSpeed(def)) * Math.Max(0.1, Rules.SpeedFactor);
        var cur = from;
        double t = 0;
        foreach (var p in route.Points)
        {
            if (p == cur || !_sim.Map.Contains(p)) continue;
            var leg = _sim.Pathfinder.Find(_sim.Map, mc, cur, p, new PathOptions { IgnoreUnits = true, MaxExpanded = 8000 });
            if (!leg.Found) break;
            var last = cur;
            for (int i = 0; i < leg.Path.Count; i++)
            {
                var c = leg.Path[i];
                double cost = i < leg.StepCosts.Count ? leg.StepCosts[i] : 1;
                // The same step timing as movement: cost / move speed.
                t += _sim.TicksFor(cost / speed) * _sim.Dt;
                path.Add(c);
                times.Add(t);
                last = c;
            }
            cur = last;
        }
        return (path, times);
    }

    private double ExpectedSpeed(UnitDef def)
    {
        if (def.Stats.TryGetValue(Stats.MoveSpeed, out var v)) return v;
        return _sim.Content.Stat(Stats.MoveSpeed)?.Default ?? 1;
    }

    /// <summary>Seconds of warning needed before a spawn (the line must be able to travel its path), 0 if no warning.</summary>
    public double LeadSeconds(WaveDef w, UnitDef def, string faction, HexCoord from, string marker)
    {
        if (!EnabledFor(w, def, marker)) return 0;
        var (_, times) = PathOf(def, faction, from, w.Route);
        return Math.Max(times[^1], MinSeconds(w)) + Rules.VanishBefore;
    }

    /// <summary>Plans (or re-plans) the warning of a spawn at a known tick.</summary>
    public SpawnWarning? Plan(int spawnId, int wave, WaveDef w, UnitDef def, string faction, string marker, HexCoord from, long spawnTick)
    {
        Remove(spawnId);
        var (path, times) = PathOf(def, faction, from, w.Route);
        var sw = new SpawnWarning
        {
            Id = _nextId++, SpawnId = spawnId, Wave = wave, UnitDef = def.Id, Marker = marker, Route = w.Route, From = from,
            Path = path, Times = times, SpawnTick = spawnTick, MapVersion = _sim.Map.Version, Enabled = EnabledFor(w, def, marker),
        };
        Retime(sw, w);
        _bySpawn[spawnId] = sw;
        return sw;
    }

    private void Retime(SpawnWarning sw, WaveDef w)
    {
        double lead = Math.Max(sw.Duration, MinSeconds(w));
        sw.VanishTick = sw.SpawnTick - _sim.TicksFor(Rules.VanishBefore);
        sw.StartTick = sw.VanishTick - _sim.TicksFor(lead);
    }

    /// <summary>The spawn happened or was cancelled: the line disappears.</summary>
    public void Remove(int spawnId) => _bySpawn.Remove(spawnId);

    public void RemoveWave(int wave)
    {
        foreach (var id in _bySpawn.Where(kv => kv.Value.Wave == wave).Select(kv => kv.Key).ToList()) _bySpawn.Remove(id);
    }

    public SpawnWarning? OfSpawn(int spawnId) => _bySpawn.TryGetValue(spawnId, out var w) ? w : null;

    /// <summary>Route or terrain changed: recompute paths (the start moves so the head still meets the spawn on time).</summary>
    public void Update()
    {
        if (_bySpawn.Count == 0) return;
        var sc = _sim.Scenario.Def;
        foreach (var sw in _bySpawn.Values)
        {
            if (sw.MapVersion == _sim.Map.Version) continue;
            var def = _sim.Content.Unit(sw.UnitDef);
            var w = sc != null && sw.Wave >= 0 && sw.Wave < sc.Waves.Count ? sc.Waves[sw.Wave] : null;
            if (def == null || w == null) continue;
            var (path, times) = PathOf(def, w.Faction, sw.From, sw.Route);
            sw.Path = path;
            sw.Times = times;
            sw.MapVersion = _sim.Map.Version;
            if (_sim.Tick < sw.StartTick) Retime(sw, w);
        }
    }

    /// <summary>The line is on screen now (between start and vanish).</summary>
    public bool IsActive(SpawnWarning sw, long tick) => sw.Enabled && tick >= sw.StartTick && tick < sw.VanishTick;

    /// <summary>Head position along the path in cells (fractional): 0 — spawn cell, Path.Count−1 — the end.</summary>
    public double HeadCells(SpawnWarning sw, double tick)
    {
        double t = (tick - sw.StartTick) * _sim.Dt;
        if (t <= 0 || sw.Path.Count <= 1) return 0;
        for (int i = 1; i < sw.Times.Count; i++)
        {
            if (t < sw.Times[i])
            {
                double seg = sw.Times[i] - sw.Times[i - 1];
                return i - 1 + (seg <= 1e-9 ? 1 : (t - sw.Times[i - 1]) / seg);
            }
        }
        return sw.Path.Count - 1;
    }

    /// <summary>Visible part of the line in cells [from; to] for the player (fog rules applied by the caller per cell for «частично»).</summary>
    public (double From, double To) VisibleSpan(SpawnWarning sw, double tick)
    {
        double head = HeadCells(sw, tick);
        double tail = Rules.Mode == WarningMode.Comet ? Math.Max(0, head - Rules.CometCells) : 0;
        return (tail, head);
    }

    /// <summary>Fog of war: shown, hidden, after scouting the spawn point, or partially (visible cells only).</summary>
    public bool VisibleToPlayer(SpawnWarning sw)
    {
        if (!sw.Enabled) return false;
        if (!_sim.FogOfWar || _sim.Options.AdminMode) return true;
        return FogMode switch
        {
            WarningFog.Hide => false,
            WarningFog.AfterScouting => _sim.Vision.IsExplored(sw.From),
            _ => true,
        };
    }

    public bool CellVisible(SpawnWarning sw, HexCoord c) =>
        FogMode != WarningFog.Partial || !_sim.FogOfWar || _sim.Options.AdminMode || _sim.Vision.IsCellVisibleToPlayer(c);
}
