using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>
/// Cell effects (fire, poison, ice, smoke...) on top of terrain, their ticks, spreading,
/// terrain restrictions and data-driven synergies with loop protection.
/// The terrain type is never replaced by an effect (only explicit ChangeTerrain does that).
/// </summary>
public sealed class CellEffectSystem
{
    private readonly Simulation _sim;
    private long _serial;
    private readonly Dictionary<(int Cell, string Synergy), long> _synergyCooldown = new();
    private readonly List<(Cell Cell, CellEffectDef Def, int Level, double Duration, int Source, int Generation)> _spreadQueue = new();

    public CellEffectSystem(Simulation sim)
    {
        _sim = sim;
    }

    public int ActiveEffectCount { get; private set; }
    public long SynergiesTriggered { get; private set; }

    public void ApplyIntrinsicEffects()
    {
        foreach (var cell in _sim.Map.Cells) AddIntrinsic(cell);
    }

    private void AddIntrinsic(Cell cell)
    {
        foreach (var id in cell.Terrain.IntrinsicEffects)
        {
            var def = _sim.Content.CellEffect(id);
            if (def == null || cell.HasEffect(id)) continue;
            var inst = new CellEffectInstance { Def = def, Level = 1, RemainingTicks = -1, TotalTicks = -1, Intrinsic = true, Serial = ++_serial, Origin = EffectOrigin.Terrain };
            cell.Effects.Add(inst);
            SortEffects(cell);
        }
    }

    private static void SortEffects(Cell cell)
    {
        if (cell.Effects.Count < 2) return;
        var sorted = cell.Effects.OrderBy(e => e.Def.Priority).ThenBy(e => e.Serial).ToList();
        cell.Effects.Clear();
        cell.Effects.AddRange(sorted);
    }

    private static bool AnyTag(Cell cell, List<string> tags)
    {
        foreach (var t in tags)
            if (cell.HasTag(t))
                return true;
        return false;
    }

    public bool CanHost(Cell cell, CellEffectDef def, out string reason)
    {
        reason = "";
        if (def.AllowedTerrainTags.Count > 0 && !AnyTag(cell, def.AllowedTerrainTags))
        {
            reason = $"«{def.DisplayName}» не держится на местности «{cell.Terrain.DisplayName}»";
            return false;
        }
        if (def.ForbiddenTerrainTags.Count > 0 && AnyTag(cell, def.ForbiddenTerrainTags))
        {
            reason = $"«{def.DisplayName}» невозможен на местности «{cell.Terrain.DisplayName}»";
            return false;
        }
        if (cell.AllowedEffects != null && !cell.AllowedEffects.Contains(def.Id))
        {
            reason = $"«{def.DisplayName}» запрещён на этой клетке";
            return false;
        }
        return true;
    }

    private void TouchFor(CellEffectDef def) =>
        _sim.Map.Touch(def.BlocksLoS || def.BlocksMovement || Math.Abs(def.MoveCostAdd) > 1e-9);

    /// <summary>Adds (or stacks) an effect. duration 0 = definition default. Returns the instance or null if not allowed.</summary>
    public CellEffectInstance? Add(Cell cell, CellEffectDef def, int level, double duration, int source, int generation, int chainDepth,
        EffectOrigin origin = EffectOrigin.Skill)
    {
        if (!CanHost(cell, def, out var reason))
        {
            _sim.Emit(SimEventType.CellEffectBlocked, LogLevel.Detail, reason, source, cell: cell.Coord);
            return null;
        }
        double dur = duration > 0 ? duration : def.Duration;
        long ticks = dur > 0 ? _sim.TicksFor(dur) : -1;
        level = Math.Clamp(level, 1, Math.Max(1, def.MaxLevel));

        var existing = cell.FindEffect(def.Id);
        if (existing != null)
        {
            switch (def.StackPolicy)
            {
                case CellStackPolicy.Ignore:
                    return existing;
                case CellStackPolicy.Refresh:
                    if (existing.RemainingTicks >= 0 && (ticks < 0 || ticks > existing.RemainingTicks))
                    {
                        existing.RemainingTicks = ticks;
                        existing.TotalTicks = Math.Max(existing.TotalTicks, ticks);
                    }
                    existing.Level = Math.Max(existing.Level, level);
                    break;
                case CellStackPolicy.AddLevel:
                    existing.Level = Math.Min(Math.Max(1, def.MaxLevel), existing.Level + level);
                    if (existing.RemainingTicks >= 0 && (ticks < 0 || ticks > existing.RemainingTicks)) existing.RemainingTicks = existing.TotalTicks = ticks;
                    break;
                case CellStackPolicy.AddDuration:
                    if (existing.RemainingTicks >= 0 && ticks > 0)
                    {
                        existing.RemainingTicks = Math.Min(existing.RemainingTicks + ticks, ticks * 4);
                        existing.TotalTicks = Math.Max(existing.TotalTicks, existing.RemainingTicks);
                    }
                    break;
                case CellStackPolicy.Replace:
                    Remove(cell, existing, "заменён");
                    existing = null;
                    break;
            }
            if (existing != null)
            {
                existing.Generation = Math.Min(existing.Generation, generation);
                if (source >= 0) existing.SourceUnit = source;
                _sim.Map.Touch(false);
                return existing;
            }
        }

        if (cell.Effects.Count >= _sim.Rules.Limits.MaxEffectsPerCell)
        {
            _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"На клетке {cell.Coord} уже {cell.Effects.Count} эффектов — «{def.DisplayName}» не добавлен", source, cell: cell.Coord);
            return null;
        }

        var inst = new CellEffectInstance
        {
            Def = def,
            Level = level,
            RemainingTicks = ticks,
            TotalTicks = ticks,
            Generation = generation,
            SourceUnit = source,
            Serial = ++_serial,
            Origin = generation > 0 ? EffectOrigin.Spread : origin,
        };
        cell.Effects.Add(inst);
        SortEffects(cell);
        TouchFor(def);
        _sim.Emit(SimEventType.CellEffectAdded, generation > 0 ? LogLevel.Diagnostic : LogLevel.Detail,
            $"{cell.Coord}: «{def.DisplayName}»{(level > 1 ? $" ур.{level}" : "")}{(dur > 0 ? $" на {dur:0.#} с" : "")}", source, cell: cell.Coord);
        EvaluateSynergies(cell, inst, chainDepth);
        return inst;
    }

    public void Remove(Cell cell, CellEffectInstance inst, string reason)
    {
        if (!cell.Effects.Remove(inst)) return;
        TouchFor(inst.Def);
        _sim.Emit(SimEventType.CellEffectRemoved, LogLevel.Diagnostic, $"{cell.Coord}: «{inst.Def.DisplayName}» исчез ({reason})", cell: cell.Coord);
    }

    public int RemoveById(Cell cell, string id, string reason)
    {
        int n = 0;
        for (var e = cell.FindEffect(id); e != null; e = cell.FindEffect(id))
        {
            Remove(cell, e, reason);
            n++;
        }
        return n;
    }

    public void ChangeTerrain(Cell cell, TerrainDef terrain, string cause, int chainDepth = 0)
    {
        if (cell.Terrain == terrain) return;
        var old = cell.Terrain;
        foreach (var e in cell.Effects.Where(e => e.Intrinsic).ToList()) cell.Effects.Remove(e);
        _sim.Map.SetTerrain(cell, terrain);
        AddIntrinsic(cell);
        _sim.Emit(SimEventType.TerrainChanged, LogLevel.Normal, $"{cell.Coord}: «{old.DisplayName}» → «{terrain.DisplayName}» ({cause})", cell: cell.Coord);
        // Floor collapsed into a pit: everyone standing there may fall.
        if (terrain.Kind == TerrainKind.Pit) _sim.Falls.CheckOccupants(cell, null, cause);
        foreach (var e in cell.Effects.ToList())
            if (!CanHost(cell, e.Def, out var why))
                Remove(cell, e, why);
        foreach (var e in cell.Effects.ToList())
            if (cell.Effects.Contains(e))
                EvaluateSynergies(cell, e, chainDepth + 1, terrainOnly: true);
        _sim.Vision.MarkDirty();
    }

    public void DamageObstacle(Cell cell, int damage, Unit? source, string cause)
    {
        if (cell.Terrain.DestructibleHp <= 0 || damage <= 0) return;
        cell.ObstacleHp -= damage;
        if (cell.ObstacleHp > 0)
        {
            _sim.Emit(SimEventType.Info, LogLevel.Detail, $"{cell.Coord}: препятствие повреждено ({cell.ObstacleHp} осталось)", source?.Id ?? -1, cell: cell.Coord);
            return;
        }
        var into = _sim.Content.Terrain(cell.Terrain.DestroyedTerrain);
        if (into == null) return;
        _sim.Emit(SimEventType.ObstacleDestroyed, LogLevel.Normal, $"{cell.Coord}: «{cell.Terrain.DisplayName}» разрушено — {cause}", source?.Id ?? -1, cell: cell.Coord);
        ChangeTerrain(cell, into, "разрушение");
    }

    /// <summary>Immune creature category / immunity, or the effect does not reach the unit's layer.</summary>
    private bool UnitImmune(Unit u, CellEffectDef def) =>
        !LayerResolver.EffectAffects(_sim.Rules, def, u) || def.ImmuneCreatureCategories.Contains(u.Def.CreatureCategory) || u.IsImmuneTo(def.Id, def.Tags);

    public void OnUnitEntered(Unit u, Cell cell)
    {
        foreach (var e in cell.Effects.ToList())
        {
            if (!u.Alive) return;
            if (e.Def.OnEnter.Count == 0 || UnitImmune(u, e.Def)) continue;
            if (e.Def.ConsumeOnEnter)
                _sim.Emit(SimEventType.TrapTriggered, LogLevel.Normal, $"{cell.Coord}: срабатывает «{e.Def.DisplayName}» под {u.Name}", e.SourceUnit, u.Id, cell.Coord);
            _sim.Combat.ApplyDirect(_sim.GetUnit(e.SourceUnit), u, e.Def.OnEnter, e.Def.ScaleWithLevel ? e.Level : 1, e.Def.DisplayName);
            if (e.Def.ConsumeOnEnter)
            {
                if (e.Level > 1) e.Level--;
                else Remove(cell, e, "сработал");
            }
        }
    }

    public void OnUnitExited(Unit u, Cell cell)
    {
        foreach (var e in cell.Effects.ToList())
        {
            if (!u.Alive) return;
            if (e.Def.OnExit.Count == 0 || UnitImmune(u, e.Def)) continue;
            _sim.Combat.ApplyDirect(_sim.GetUnit(e.SourceUnit), u, e.Def.OnExit, e.Def.ScaleWithLevel ? e.Level : 1, e.Def.DisplayName + " (выход)");
        }
    }

    /// <summary>(unit, effect def) → tick of the last adjacency application (one per interval, whatever the number of nearby cells).</summary>
    private readonly Dictionary<(int, string), long> _adjacentApplied = new();

    private void ApplyAdjacent(Cell cell, CellEffectInstance e)
    {
        long interval = Math.Max(1, _sim.TicksFor(e.Def.AdjacentInterval));
        e.AdjacentTimer++;
        if (e.AdjacentTimer < interval) return;
        e.AdjacentTimer = 0;
        int radius = Math.Max(1, e.Def.AdjacentRadius);
        foreach (var c in _sim.Map.InRadius(cell.Coord, radius))
        {
            if (c == cell) continue;
            foreach (var u in c.Occupants.ToArray())
            {
                if (!u.Alive || UnitImmune(u, e.Def)) continue;
                var key = (u.Id, e.Def.Id);
                if (_adjacentApplied.TryGetValue(key, out var last) && _sim.Tick - last < interval) continue;
                _adjacentApplied[key] = _sim.Tick;
                _sim.Combat.ApplyDirect(_sim.GetUnit(e.SourceUnit), u, e.Def.OnAdjacent, e.Def.ScaleWithLevel ? e.Level : 1, e.Def.DisplayName + " (рядом)");
            }
        }
    }

    public void Update()
    {
        int count = 0;
        foreach (var cell in _sim.Map.Cells)
        {
            if (cell.Effects.Count == 0) continue;
            foreach (var e in cell.Effects.ToArray())
            {
                if (!cell.Effects.Contains(e)) continue;
                count++;
                if (e.RemainingTicks > 0)
                {
                    e.RemainingTicks--;
                    if (e.RemainingTicks == 0)
                    {
                        Remove(cell, e, "время истекло");
                        continue;
                    }
                }
                if (e.Def.OnTick.Count > 0)
                {
                    e.TickTimer++;
                    if (e.TickTimer >= _sim.TicksFor(e.Def.TickInterval))
                    {
                        e.TickTimer = 0;
                        foreach (var u in cell.Occupants.ToArray())
                        {
                            if (!u.Alive || UnitImmune(u, e.Def)) continue;
                            _sim.Combat.ApplyDirect(_sim.GetUnit(e.SourceUnit), u, e.Def.OnTick, e.Def.ScaleWithLevel ? e.Level : 1, e.Def.DisplayName);
                        }
                    }
                }
                if (e.Def.OnAdjacent.Count > 0) ApplyAdjacent(cell, e);
                var sp = e.Def.Spread;
                if (sp.Enabled && e.Generation < sp.MaxGenerations)
                {
                    e.SpreadTimer++;
                    if (e.SpreadTimer >= _sim.TicksFor(sp.Interval))
                    {
                        e.SpreadTimer = 0;
                        for (int d = 0; d < 6; d++)
                        {
                            var n = cell.Neighbors[d];
                            if (n == null || n.HasEffect(e.Def.Id)) continue;
                            if (!_sim.Rng.Chance(sp.Chance)) continue;
                            if (!SpreadTargetOk(n, sp)) continue;
                            double baseDur = e.TotalTicks > 0 ? e.TotalTicks * _sim.Dt : e.Def.Duration;
                            _spreadQueue.Add((n, e.Def, e.Level, Math.Max(_sim.Dt, baseDur * sp.ChildDurationFactor), e.SourceUnit, e.Generation + 1));
                        }
                    }
                }
            }
        }
        ActiveEffectCount = count;
        if (_spreadQueue.Count == 0) return;
        var queue = _spreadQueue.ToArray();
        _spreadQueue.Clear();
        foreach (var (cell, def, level, dur, src, gen) in queue)
            if (!cell.HasEffect(def.Id))
                Add(cell, def, level, dur, src, gen, 0);
    }

    private bool SpreadTargetOk(Cell n, SpreadDef sp)
    {
        bool hasTerrainRule = sp.ToTerrainTags.Count > 0;
        bool hasEffectRule = sp.ToCellsWithEffects.Count > 0;
        if (!hasTerrainRule && !hasEffectRule) return true;
        if (hasTerrainRule && AnyTag(n, sp.ToTerrainTags)) return true;
        if (hasEffectRule)
            foreach (var x in sp.ToCellsWithEffects)
            foreach (var e in n.Effects)
                if (MatchesRef(x, e.Def))
                    return true;
        return false;
    }

    private static bool MatchesRef(string reference, CellEffectDef def) =>
        reference.StartsWith("tag:", StringComparison.Ordinal) ? def.Tags.Contains(reference.Substring(4)) : def.Id == reference;

    // ------------------------------------------------------------------ synergies

    private static bool Matches(SynergyOperandDef op, CellEffectInstance e) => op.Kind switch
    {
        SynergyOperandKind.CellEffect => e.Def.Id == op.Value,
        SynergyOperandKind.CellEffectTag => e.Def.Tags.Contains(op.Value),
        _ => false,
    };

    private void EvaluateSynergies(Cell cell, CellEffectInstance added, int depth, bool terrainOnly = false)
    {
        var synergies = _sim.Content.Table(DefCategory.Synergy).Items;
        if (synergies.Count == 0) return;
        if (depth >= _sim.Rules.Limits.MaxSynergyChain)
        {
            _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"{cell.Coord}: цепочка синергий достигла предела ({depth}) — остановлена", cell: cell.Coord);
            return;
        }
        foreach (SynergyDef syn in synergies)
        {
            if (!cell.Effects.Contains(added)) return;
            if (terrainOnly && syn.B.Kind != SynergyOperandKind.TerrainTag) continue;
            if (_synergyCooldown.TryGetValue((cell.Index, syn.Id), out var until) && until > _sim.Tick) continue;

            CellEffectInstance? a = null, b = null;
            bool terrainB = false;
            if (Matches(syn.A, added))
            {
                a = added;
                if (syn.B.Kind == SynergyOperandKind.TerrainTag) terrainB = cell.HasTag(syn.B.Value);
                else b = cell.Effects.FirstOrDefault(x => x != added && Matches(syn.B, x));
            }
            else if (syn.B.Kind != SynergyOperandKind.TerrainTag && Matches(syn.B, added))
            {
                b = added;
                a = cell.Effects.FirstOrDefault(x => x != added && Matches(syn.A, x));
            }
            if (a == null || (!terrainB && b == null)) continue;
            ApplySynergy(syn, cell, a, b, depth);
        }
    }

    private void ApplySynergy(SynergyDef syn, Cell cell, CellEffectInstance a, CellEffectInstance? b, int depth)
    {
        SynergiesTriggered++;
        if (syn.CooldownPerCell > 0) _synergyCooldown[(cell.Index, syn.Id)] = _sim.Tick + _sim.TicksFor(syn.CooldownPerCell);
        else _synergyCooldown[(cell.Index, syn.Id)] = _sim.Tick + 1;
        var src = a.SourceUnit >= 0 ? a.SourceUnit : b?.SourceUnit ?? -1;
        var source = _sim.GetUnit(src);
        string text = string.IsNullOrEmpty(syn.LogText) ? $"Синергия «{syn.DisplayName}»" : syn.LogText;
        _sim.Emit(SimEventType.Synergy, LogLevel.Normal, $"{cell.Coord}: {text}", src, cell: cell.Coord);

        if (syn.RemoveA) Remove(cell, a, "синергия " + syn.DisplayName);
        if (syn.RemoveB && b != null) Remove(cell, b, "синергия " + syn.DisplayName);

        if (!string.IsNullOrEmpty(syn.ConvertTerrain))
        {
            var t = _sim.Content.Terrain(syn.ConvertTerrain);
            if (t != null && !cell.Occupants.Any(o => o.Alive && !t.IsPassableFor(o.Def.MoveCategory)))
                ChangeTerrain(cell, t, syn.DisplayName, depth);
        }

        var area = _sim.Map.InRadius(cell.Coord, Math.Max(0, syn.Radius)).ToList();
        if (syn.UnitEffects.Count > 0)
            foreach (var c in area)
            foreach (var u in c.Occupants.ToArray())
                if (u.Alive)
                    _sim.Combat.ApplyDirect(source, u, syn.UnitEffects, 1.0, syn.DisplayName);

        foreach (var id in syn.AddEffects)
        {
            var d = _sim.Content.CellEffect(id);
            if (d != null) Add(cell, d, 1, 0, src, 0, depth + 1);
        }
        foreach (var id in syn.RadiusCellEffects)
        {
            var d = _sim.Content.CellEffect(id);
            if (d == null) continue;
            foreach (var c in area)
                if (c != cell)
                    Add(c, d, 1, 0, src, 0, depth + 1);
        }
    }
}
