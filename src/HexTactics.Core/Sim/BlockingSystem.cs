using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

public enum MovementHold
{
    /// <summary>Free to move.</summary>
    None,
    /// <summary>Held back by a blocker (cannot advance, attacks it by default).</summary>
    Blocked,
    /// <summary>Held by a special mechanic: a status that forbids or slows movement.</summary>
    Held,
}

/// <summary>
/// Blocking (docs/DECISIONS_2.md §27). Links "enemy → blocker" are recomputed every tick from
/// the current state (existing links first), so a blocker that dies, is pushed away, stunned or
/// loses its limit never leaves an enemy stuck. Capacity = floor(stat block_limit) slots.
/// </summary>
public sealed class BlockingSystem
{
    private readonly Simulation _sim;
    private readonly Dictionary<int, int> _blockedBy = new();
    private readonly Dictionary<int, int> _load = new();
    private readonly Dictionary<int, long> _detourUntil = new();

    public BlockingSystem(Simulation sim)
    {
        _sim = sim;
    }

    public int Links => _blockedBy.Count;

    public Unit? BlockerOf(Unit u) => _blockedBy.TryGetValue(u.Id, out var b) ? _sim.GetUnit(b) : null;

    public int Load(Unit blocker) => _load.TryGetValue(blocker.Id, out var n) ? n : 0;

    public int Limit(Unit blocker) => Math.Max(0, (int)Math.Floor(blocker.Stat(Stats.BlockLimit) + 1e-9));

    public IEnumerable<Unit> BlockedBy(Unit blocker)
    {
        foreach (var kv in _blockedBy)
            if (kv.Value == blocker.Id)
            {
                var u = _sim.GetUnit(kv.Key);
                if (u != null) yield return u;
            }
    }

    /// <summary>Blocked by a unit, held by a status, or free.</summary>
    public MovementHold HoldOf(Unit u)
    {
        if (_blockedBy.ContainsKey(u.Id)) return MovementHold.Blocked;
        if (u.Flags.BlocksMovement || u.Stat(Stats.MoveSpeed) < u.Stats.Base(Stats.MoveSpeed) - 1e-6) return MovementHold.Held;
        return MovementHold.None;
    }

    public bool CanBlock(Unit b)
    {
        if (!b.Alive || Limit(b) <= 0) return false;
        if (b.Flags.BlocksActions || b.Flags.Fear || b.Flags.MindControl) return false;
        if (b.StepTo.HasValue && !b.Def.Blocking.WhileMoving) return false;
        return true;
    }

    private static bool Matches(List<string> list, Unit e)
    {
        if (list.Count == 0) return true;
        foreach (var x in list)
        {
            if (x == e.Def.CreatureCategory || x == e.Def.Id) return true;
            if (x.StartsWith("tag:", StringComparison.Ordinal) && e.Def.Tags.Contains(x.Substring(4))) return true;
        }
        return false;
    }

    /// <summary>Can this blocker hold this enemy at all (relation, layers, categories, special ability)?</summary>
    public bool Compatible(Unit b, Unit e)
    {
        if (!e.Alive || !_sim.IsHostile(b, e)) return false;
        var able = e.Def.Blockable;
        if (!able.CanBeBlocked || able.CanBypass) return false;
        var bd = b.Def.Blocking;
        if (bd.Layers.Count > 0 ? !bd.Layers.Contains(e.Layer) : !e.LayerDef.Blockable) return false;
        if (!Matches(bd.Categories, e)) return false;
        if (!string.IsNullOrEmpty(able.RequiredAbility) && !bd.Abilities.Contains(able.RequiredAbility)) return false;
        return true;
    }

    public bool InZone(Unit b, Unit e)
    {
        int d = HexCoord.Distance(b.Pos, e.Pos);
        switch (b.Def.Blocking.Zone)
        {
            case BlockZone.SameCell:
                return d == 0;
            case BlockZone.FrontArc:
                if (d == 0) return true;
                if (d != 1) return false;
                return !b.Def.Perception.HasFacing || CombatSystem.SectorOf(b.Pos, b.Facing, e.Pos) <= AttackSector.NearFlank;
            default:
                return d <= 1;
        }
    }

    /// <summary>
    /// The enemy is trying to advance (it has a movement order). A walker is mid-step almost every tick,
    /// so the link is made while it walks and stops it before its next step.
    /// </summary>
    private static bool Advancing(Unit e)
    {
        var c = e.Queue.Current;
        return c != null && c.Type == CommandType.Move && !c.IsFinished;
    }

    public void OnRemoved(Unit u)
    {
        if (_blockedBy.Remove(u.Id, out var b)) Release(b, u);
        foreach (var e in _blockedBy.Where(kv => kv.Value == u.Id).Select(kv => kv.Key).ToList())
        {
            _blockedBy.Remove(e);
            var eu = _sim.GetUnit(e);
            if (eu != null) _sim.Emit(SimEventType.Info, LogLevel.Detail, $"{eu.Name}: блок снят — {u.Name} выбыл", u.Id, e);
        }
        _load.Remove(u.Id);
    }

    private void Release(int blockerId, Unit enemy)
    {
        var b = _sim.GetUnit(blockerId);
        if (b == null) return;
        _load[blockerId] = Math.Max(0, Load(b) - enemy.Def.Blockable.SlotCost);
    }

    /// <summary>The enemy found a detour around its blocker: the link is released and not re-made for a while.</summary>
    public void GrantDetour(Unit e, double seconds)
    {
        _detourUntil[e.Id] = _sim.Tick + _sim.TicksFor(seconds);
        if (_blockedBy.Remove(e.Id, out var b)) Release(b, e);
    }

    private bool Detouring(Unit e) => _detourUntil.TryGetValue(e.Id, out var until) && until > _sim.Tick;

    public void Update()
    {
        // 1. Keep links that are still valid; drop the others (no stale blocking).
        var keep = new Dictionary<int, int>();
        var load = new Dictionary<int, int>();
        foreach (var (eid, bid) in _blockedBy.OrderBy(kv => kv.Key))
        {
            var e = _sim.GetUnit(eid);
            var b = _sim.GetUnit(bid);
            string? why = null;
            if (e == null || !e.Alive) why = "";
            else if (b == null || !b.Alive) why = "блокирующий выбыл";
            else if (!CanBlock(b)) why = $"{b.Name} не может блокировать";
            else if (!Compatible(b, e)) why = "блок невозможен";
            else if (!InZone(b, e)) why = "вышел из зоны блока";
            else if (Detouring(e)) why = "обходит блок";
            else
            {
                int used = load.TryGetValue(bid, out var u) ? u : 0;
                if (used + e.Def.Blockable.SlotCost > Limit(b)) why = $"лимит {b.Name} снизился";
                else
                {
                    keep[eid] = bid;
                    load[bid] = used + e.Def.Blockable.SlotCost;
                }
            }
            if (why != null && why.Length > 0 && e != null)
                _sim.Emit(SimEventType.Info, LogLevel.Detail, $"{e.Name}: блок снят — {why}", bid, eid, e.Pos);
        }

        // 2. New links: advancing enemies inside a blocker's zone, while the blocker has free slots.
        foreach (var b in _sim.Units)
        {
            if (!CanBlock(b)) continue;
            int limit = Limit(b);
            int used = load.TryGetValue(b.Id, out var lu) ? lu : 0;
            if (used >= limit) continue;
            var cell = _sim.Map.Get(b.Pos);
            if (cell == null) continue;
            var candidates = new List<Unit>();
            foreach (var near in _sim.Map.InRadius(b.Pos, 1))
            foreach (var e in near.Occupants)
            {
                if (keep.ContainsKey(e.Id) || !Advancing(e)) continue;
                if (Detouring(e)) continue;
                if (!Compatible(b, e) || !InZone(b, e)) continue;
                candidates.Add(e);
            }
            foreach (var e in candidates.OrderBy(x => HexCoord.Distance(x.Pos, b.Pos)).ThenBy(x => x.Id))
            {
                int cost = e.Def.Blockable.SlotCost;
                if (used + cost > limit) continue;
                used += cost;
                keep[e.Id] = b.Id;
                _sim.Emit(SimEventType.Info, LogLevel.Normal, $"{b.Name} блокирует {e.Name} ({used}/{limit})", b.Id, e.Id, e.Pos);
            }
            load[b.Id] = used;
        }
        _blockedBy.Clear();
        foreach (var kv in keep) _blockedBy[kv.Key] = kv.Value;
        _load.Clear();
        foreach (var kv in load) _load[kv.Key] = kv.Value;
    }
}
