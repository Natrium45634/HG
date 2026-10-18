using System;
using System.Collections.Generic;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>
/// Who sees whom (detection range, line of sight, stealth, invisibility, blindness, darkness).
/// Recomputed on an interval (Vision.UpdateInterval) or when forced (spawn/death/status change).
/// The player is never shown units its side cannot detect (fog of war).
/// </summary>
public sealed class VisionSystem
{
    private readonly Simulation _sim;
    private readonly Dictionary<string, HashSet<int>> _visible = new();
    private readonly HashSet<HexCoord> _playerCells = new();
    private long _nextUpdate;
    private bool _changed = true;
    private bool _urgent = true;
    private bool _initialized;
    private readonly Dictionary<int, (HexCoord Pos, int Range, int Version, List<HexCoord> Cells)> _viewerCells = new();

    public VisionSystem(Simulation sim)
    {
        _sim = sim;
    }

    /// <summary>Last position where the player side saw a (now hidden) unit.</summary>
    public Dictionary<int, HexCoord> LastSeenByPlayer { get; } = new();

    public long Recomputes { get; private set; }

    /// <summary>
    /// Something that may affect visibility changed. Non-urgent changes (movement, facing,
    /// statuses) are picked up at the next update interval; urgent ones (spawn, death) next tick.
    /// </summary>
    public void MarkDirty(bool urgent = false)
    {
        _changed = true;
        if (urgent) _urgent = true;
    }

    public double DetectionRange(Unit viewer, Unit target)
    {
        var vr = _sim.Rules.Vision;
        double range = viewer.Stat(Stats.DetectionRange) * (1.0 - _sim.Map.Darkness * 0.5);
        if (viewer.Flags.Blind) range = Math.Min(range, vr.BlindDetectRange);
        double stealth = Math.Clamp(target.Stat(Stats.Stealth) * vr.StealthRangeFactor, 0, 0.9);
        range *= 1.0 - stealth;
        if (target.Flags.Invisible) range = Math.Min(range, vr.InvisibleDetectRange);
        return Math.Max(range, target.Flags.Invisible ? 0 : 1);
    }

    public bool CanSee(Unit viewer, Unit target, HexCoord? targetPos = null)
    {
        if (!viewer.Alive || !target.Alive) return false;
        if (viewer == target) return true;
        var pos = targetPos ?? target.Pos;
        int d = HexCoord.Distance(viewer.Pos, pos);
        if (d > DetectionRange(viewer, target) + 1e-9) return false;
        // Hidden layers (underground): only viewers with the layer's detection stat, or up close.
        var layer = target.LayerDef;
        if (layer.Hidden && d > layer.RevealRange && (string.IsNullOrEmpty(layer.DetectionStat) || viewer.Stat(layer.DetectionStat) <= 0)) return false;
        if (layer.Hidden) return true; // detected by tremor/sonar: no line of sight needed
        return _sim.Los.Has(_sim.Map, _sim.Rules, viewer.Pos, pos);
    }

    private bool SharesVision(string a, string b) => a == b || _sim.Content.RelationOf(a, b) == Relation.Ally;

    public bool CanFactionSee(string faction, Unit target)
    {
        if (!target.Alive) return false;
        if (SharesVision(faction, target.FactionId)) return true;
        if (!_initialized) Recompute();
        foreach (var kv in _visible)
            if (SharesVision(faction, kv.Key) && kv.Value.Contains(target.Id))
                return true;
        return false;
    }

    public bool IsVisibleToPlayer(Unit u)
    {
        if (!_sim.FogOfWar || _sim.Options.AdminMode) return true;
        foreach (var f in _sim.Options.PlayerFactions)
            if (CanFactionSee(f, u))
                return true;
        return false;
    }

    public bool IsCellVisibleToPlayer(HexCoord c)
    {
        if (!_sim.FogOfWar || _sim.Options.AdminMode) return true;
        if (!_initialized) Recompute();
        return _playerCells.Contains(c);
    }

    public HashSet<HexCoord> PlayerVisibleCells
    {
        get
        {
            if (!_initialized) Recompute();
            return _playerCells;
        }
    }

    public void Update()
    {
        bool due = _sim.Tick >= _nextUpdate;
        if (!_initialized || _urgent || (due && _changed))
        {
            Recompute();
            _nextUpdate = _sim.Tick + Math.Max(1, _sim.TicksFor(_sim.Rules.Vision.UpdateInterval));
        }
        else if (due)
        {
            _nextUpdate = _sim.Tick + Math.Max(1, _sim.TicksFor(_sim.Rules.Vision.UpdateInterval));
        }
    }

    private double BaseRange(Unit viewer)
    {
        double range = viewer.Stat(Stats.DetectionRange) * (1.0 - _sim.Map.Darkness * 0.5);
        if (viewer.Flags.Blind) range = Math.Min(range, _sim.Rules.Vision.BlindDetectRange);
        return Math.Max(range, 1);
    }

    public void Recompute()
    {
        Recomputes++;
        _initialized = true;
        _changed = false;
        _urgent = false;
        var units = _sim.Units;
        var factions = new List<string>();
        foreach (var u in units)
            if (u.Alive && !factions.Contains(u.FactionId))
                factions.Add(u.FactionId);

        var newVisible = new Dictionary<string, HashSet<int>>();
        foreach (var f in factions) newVisible[f] = new HashSet<int>();

        foreach (var viewer in units)
        {
            if (!viewer.Alive) continue;
            var set = newVisible[viewer.FactionId];
            double baseRange = BaseRange(viewer) + 1e-9;
            foreach (var target in units)
            {
                if (!target.Alive || target.FactionId == viewer.FactionId || set.Contains(target.Id)) continue;
                // Cheap reject before stat lookups and line of sight.
                if (HexCoord.Distance(viewer.Pos, target.Pos) > baseRange) continue;
                if (CanSee(viewer, target)) set.Add(target.Id);
            }
        }

        // Events: newly spotted / lost.
        foreach (var f in factions)
        {
            _visible.TryGetValue(f, out var old);
            var now = newVisible[f];
            foreach (var id in now)
            {
                if (old != null && old.Contains(id)) continue;
                var t = _sim.GetUnit(id);
                if (t == null) continue;
                if (_sim.Content.RelationOf(f, t.FactionId) != Relation.Enemy) continue;
                bool player = _sim.IsPlayerFaction(f);
                bool alreadySeenByAlly = false;
                foreach (var kv in _visible)
                    if (kv.Key != f && SharesVision(f, kv.Key) && kv.Value.Contains(id))
                        alreadySeenByAlly = true;
                if (alreadySeenByAlly) continue;
                _sim.Emit(SimEventType.UnitSpotted, player ? LogLevel.Normal : LogLevel.Diagnostic,
                    player ? $"Обнаружен враг: {t.Name} в {t.Pos}" : $"«{_sim.Content.Faction(f)?.DisplayName ?? f}» обнаруживает {t.Name}", -1, id, t.Pos);
                if (player && _sim.Rules.Autopause.EnemySpotted) _sim.RequestAutopause($"обнаружен {t.Name}");
                foreach (var u in units)
                    if (u.Alive && u.FactionId == f)
                        _sim.Reactions.Enqueue(PassiveTrigger.OnEnemySpotted, u, t);
            }
            if (old != null)
                foreach (var id in old)
                    if (!now.Contains(id))
                    {
                        var t = _sim.GetUnit(id);
                        if (t != null && t.Alive && _sim.IsPlayerFaction(f))
                        {
                            LastSeenByPlayer[id] = t.Pos;
                            _sim.Emit(SimEventType.UnitLost, LogLevel.Detail, $"{t.Name} пропадает из виду", -1, id);
                        }
                    }
        }
        _visible.Clear();
        foreach (var kv in newVisible) _visible[kv.Key] = kv.Value;
        foreach (var kv in _visible)
            if (_sim.IsPlayerFaction(kv.Key))
                foreach (var id in kv.Value)
                    LastSeenByPlayer.Remove(id);

        // Cells visible to the player side (fog rendering, log visibility).
        _playerCells.Clear();
        if (!_sim.FogOfWar) return;
        foreach (var viewer in units)
        {
            if (!viewer.Alive || !(_sim.IsPlayerFaction(viewer.FactionId) || IsPlayerAlly(viewer.FactionId))) continue;
            int r = (int)Math.Floor(BaseRange(viewer) + 1e-9);
            // Visible cells of a viewer only change when it moves, its range changes or the map changes.
            if (!_viewerCells.TryGetValue(viewer.Id, out var vc) || vc.Pos != viewer.Pos || vc.Range != r || vc.Version != _sim.Map.Version)
            {
                var list = new List<HexCoord>();
                foreach (var c in HexCoord.Spiral(viewer.Pos, r))
                    if (_sim.Map.Contains(c) && _sim.Los.Has(_sim.Map, _sim.Rules, viewer.Pos, c))
                        list.Add(c);
                vc = (viewer.Pos, r, _sim.Map.Version, list);
                _viewerCells[viewer.Id] = vc;
            }
            foreach (var c in vc.Cells) _playerCells.Add(c);
        }
    }

    private bool IsPlayerAlly(string faction)
    {
        foreach (var p in _sim.Options.PlayerFactions)
            if (_sim.Content.RelationOf(p, faction) == Relation.Ally)
                return true;
        return false;
    }
}
