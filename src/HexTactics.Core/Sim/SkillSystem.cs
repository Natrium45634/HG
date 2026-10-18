using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

public enum InterruptCause
{
    Damage,
    Control,
    ForcedMove,
    Death,
    Cancelled,
}

/// <summary>An active (preparing) or released skill use.</summary>
public sealed class SkillCast
{
    public int Id { get; init; }
    public Unit Caster { get; init; } = null!;
    public SkillDef Skill { get; init; } = null!;
    public HexCoord Anchor { get; set; }
    public HexDirection Orientation { get; set; }
    public int TargetUnitId { get; init; } = -1;
    public HexCoord CasterPos { get; set; }
    public long StartTick { get; init; }
    public long ReleaseTick { get; set; }
    public double PaidAp { get; init; }
    public Dictionary<string, double> PaidResources { get; } = new();
    /// <summary>Cells shown as the threat/telegraph area (stage 0 at start of preparation).</summary>
    public List<HexCoord> TelegraphCells { get; } = new();
    public bool Released { get; set; }
    public bool Interrupted { get; set; }
    /// <summary>The player side saw the caster when the preparation started (telegraph may be shown).</summary>
    public bool VisibleToPlayer { get; set; }

    public double Progress(long now) => ReleaseTick <= StartTick ? 1.0 : Math.Clamp((double)(now - StartTick) / (ReleaseTick - StartTick), 0, 1);
}

public sealed class PendingStage
{
    public SkillCast Cast { get; init; } = null!;
    public int StageIndex { get; init; }
    public long Tick { get; init; }
}

/// <summary>Validation result for using a skill.</summary>
public readonly record struct SkillCheck(FailureReason Reason, string Message, HexCoord Anchor, HexDirection Orientation)
{
    public bool Ok => Reason == FailureReason.None;
}

/// <summary>
/// Skill validation, preparation/telegraph, release and stage resolution.
/// Effect phase order is fixed and documented in docs/DECISIONS.md §10.
/// </summary>
public sealed class SkillSystem
{
    private readonly Simulation _sim;
    private readonly List<PendingStage> _pending = new();
    private int _nextCastId = 1;

    public SkillSystem(Simulation sim)
    {
        _sim = sim;
    }

    public IReadOnlyList<PendingStage> PendingStages => _pending;

    /// <summary>Recently resolved stage areas (renderer flashes the real effect area). Trimmed to the last ~2 s.</summary>
    public List<(long Tick, int CasterId, string Color, List<HexCoord> Cells, bool PlayerVisible)> RecentAreas { get; } = new();

    /// <summary>All casts currently in preparation (for telegraph rendering).</summary>
    public IEnumerable<SkillCast> ActiveCasts => _sim.Units.Where(u => u.Alive && u.Casting != null && !u.Casting.Released).Select(u => u.Casting!);

    public int RangeBonus(SkillDef s, HexCoord from, HexCoord to)
    {
        if (!s.Ranged) return 0;
        var a = _sim.Map.Get(from);
        var b = _sim.Map.Get(to);
        if (a == null || b == null) return 0;
        int dh = a.Height - b.Height;
        return dh > 0 ? (int)Math.Floor(dh * _sim.Rules.Height.RangePerLevel + 1e-9) : 0;
    }

    public bool IsBasicAttack(Unit u, SkillDef s) => u.BasicAttack != null && u.BasicAttack.Id == s.Id;

    /// <summary>
    /// Checks whether the unit can use the skill from the given position/facing now.
    /// checkCosts=false is used by planning previews of future queue steps.
    /// </summary>
    public SkillCheck Validate(Unit u, SkillDef s, HexCoord? targetCell, int targetUnitId, HexDirection? direction, HexCoord fromPos, HexDirection facing,
        bool checkCosts = true, bool checkState = true)
    {
        var none = new SkillCheck(FailureReason.Invalid, "", fromPos, facing);
        if (!u.Alive) return none with { Message = "юнит погиб" };
        if (_sim.Content.DisabledSkills.TryGetValue(s.Id, out var why))
            return none with { Message = "навык отключён из-за ошибки данных: " + why };
        if (!LayerResolver.CanCastFrom(s, u)) return none with { Message = $"нельзя применить из слоя «{u.LayerDef.DisplayName}»" };
        if (checkState)
        {
            if (u.Flags.BlocksActions) return new SkillCheck(FailureReason.Disabled, "юнит оглушён", fromPos, facing);
            if (u.Flags.BlocksSkills && !IsBasicAttack(u, s)) return new SkillCheck(FailureReason.Disabled, "немота: навыки недоступны", fromPos, facing);
            if (u.Flags.BlocksAttacks && s.IsHarmful) return new SkillCheck(FailureReason.Disabled, "атаки недоступны", fromPos, facing);
        }
        if (checkCosts)
        {
            long cd = u.CooldownRemaining(s.Id, _sim.Tick);
            if (cd > 0) return new SkillCheck(FailureReason.Cooldown, $"перезарядка {cd * _sim.Dt:0.#} с", fromPos, facing);
            if (u.Ap + 1e-9 < s.ApCost) return new SkillCheck(FailureReason.NotEnoughAp, $"недостаточно ОД ({u.Ap:0.#}/{s.ApCost:0.#})", fromPos, facing);
            foreach (var rc in s.ResourceCosts)
            {
                if (!u.Resources.TryGetValue(rc.Resource, out var r))
                    return none with { Message = $"у юнита нет ресурса «{_sim.Content.Get<ResourceDef>(rc.Resource)?.DisplayName ?? rc.Resource}»" };
                if (r.Current + 1e-9 < rc.Amount)
                    return new SkillCheck(FailureReason.NotEnoughResource, $"недостаточно: {r.Def.DisplayName} ({r.Current:0.#}/{rc.Amount:0.#})", fromPos, facing);
            }
        }
        else if (s.ApCost > u.ApMax + 1e-9)
        {
            return none with { Message = $"стоимость {s.ApCost:0.#} ОД больше максимума {u.ApMax:0.#}" };
        }

        switch (s.Targeting)
        {
            case TargetingMode.Self:
            case TargetingMode.Direction:
                return new SkillCheck(FailureReason.None, "", fromPos, direction ?? facing);
            case TargetingMode.Unit:
            {
                var t = _sim.GetUnit(targetUnitId);
                if (t == null || !t.Alive) return new SkillCheck(FailureReason.TargetLost, "цель потеряна", fromPos, facing);
                if (t != u && !_sim.Vision.CanFactionSee(u.FactionId, t)) return new SkillCheck(FailureReason.TargetLost, "цель не видна", fromPos, facing);
                if (!_sim.FilterAccepts(s.ValidTargets, u, t)) return none with { Message = "недопустимая цель" };
                if (t != u && !LayerResolver.CanTarget(_sim.Rules, s, t)) return none with { Message = $"цель в слое «{t.LayerDef.DisplayName}» — навык её не поражает" };
                return CheckRangeAndLos(u, s, fromPos, t.Pos, facing);
            }
            case TargetingMode.Cell:
            {
                if (!targetCell.HasValue || !_sim.Map.Contains(targetCell.Value)) return none with { Message = "клетки нет на карте" };
                if (!s.AllowEmptyCell)
                {
                    var c = _sim.Map.Get(targetCell.Value)!;
                    if (!c.Occupants.Any(o => o.Alive && LayerResolver.CanTarget(_sim.Rules, s, o))) return new SkillCheck(FailureReason.TargetLost, "в клетке нет цели", fromPos, facing);
                }
                return CheckRangeAndLos(u, s, fromPos, targetCell.Value, facing);
            }
        }
        return none;
    }

    private SkillCheck CheckRangeAndLos(Unit u, SkillDef s, HexCoord from, HexCoord to, HexDirection facing)
    {
        int dist = HexCoord.Distance(from, to);
        int max = s.RangeMax + RangeBonus(s, from, to);
        var orient = from.DirectionTo(to) ?? facing;
        if (dist < s.RangeMin) return new SkillCheck(FailureReason.OutOfRange, $"цель слишком близко (мин. {s.RangeMin})", to, orient);
        if (dist > max) return new SkillCheck(FailureReason.OutOfRange, $"цель вне дальности ({dist} > {max})", to, orient);
        if (s.RequiresLoS)
        {
            var block = _sim.Los.Check(_sim.Map, _sim.Rules, from, to, out _);
            if (block != LosBlock.None) return new SkillCheck(FailureReason.NoLineOfSight, "нет линии видимости: " + LineOfSight.Describe(block), to, orient);
        }
        return new SkillCheck(FailureReason.None, "", to, orient);
    }

    /// <summary>Pays costs and starts preparation. Returns the cast.</summary>
    public SkillCast Begin(Unit u, SkillDef s, SkillCheck check, int targetUnitId)
    {
        u.Ap = Math.Max(0, u.Ap - s.ApCost);
        var cast = new SkillCast
        {
            Id = _nextCastId++,
            Caster = u,
            Skill = s,
            Anchor = check.Anchor,
            Orientation = check.Orientation,
            TargetUnitId = targetUnitId,
            CasterPos = u.Pos,
            StartTick = _sim.Tick,
            ReleaseTick = _sim.Tick + _sim.TicksFor(s.PrepTime),
            PaidAp = s.ApCost,
        };
        foreach (var rc in s.ResourceCosts)
        {
            if (!u.Resources.TryGetValue(rc.Resource, out var r)) continue;
            r.Current = Math.Max(0, r.Current - rc.Amount);
            cast.PaidResources[rc.Resource] = rc.Amount;
        }
        if (s.Stages.Count > 0)
            foreach (var c in StageCells(cast, s.Stages[0], preview: true))
                cast.TelegraphCells.Add(c.Cell);
        cast.VisibleToPlayer = _sim.IsPlayerFaction(u.FactionId) || _sim.Vision.IsVisibleToPlayer(u) || _sim.Options.AdminMode;
        _sim.Knowledge.OnSkillUsed(u, s);
        u.Casting = cast;
        if (s.ApCost > 0)
            _sim.Emit(SimEventType.ApSpent, LogLevel.Detail, $"{u.Name}: −{s.ApCost:0.#} ОД ({s.DisplayName})", u.Id, value: s.ApCost);
        _sim.Emit(SimEventType.CastStarted, s.PrepTime > 0 ? LogLevel.Normal : LogLevel.Detail,
            s.PrepTime > 0 ? $"{u.Name} готовит «{s.DisplayName}» ({s.PrepTime:0.##} с)" : $"{u.Name}: «{s.DisplayName}»", u.Id, targetUnitId, check.Anchor);
        return cast;
    }

    /// <summary>Releases a prepared cast: stage 0 now (or after its delay), later stages scheduled.</summary>
    public void Release(SkillCast cast)
    {
        var u = cast.Caster;
        cast.Released = true;
        u.Casting = null;
        var s = cast.Skill;
        // Unit-targeted skills follow the target if it moved during preparation (still validated by range/LoS before release).
        if (s.Targeting == TargetingMode.Unit)
        {
            var t = _sim.GetUnit(cast.TargetUnitId);
            if (t != null && t.Alive)
            {
                cast.Anchor = t.Pos;
                cast.Orientation = u.Pos.DirectionTo(t.Pos) ?? cast.Orientation;
            }
        }
        if (s.UsesChosenDirection) cast.Anchor = u.Pos;
        cast.CasterPos = u.Pos;
        if (s.Cooldown > 0) u.CooldownUntil[s.Id] = _sim.Tick + _sim.TicksFor(s.Cooldown);
        _sim.Emit(SimEventType.CastReleased, LogLevel.Detail, $"{u.Name} применяет «{s.DisplayName}»", u.Id, cast.TargetUnitId, cast.Anchor);
        for (int i = 0; i < s.Stages.Count; i++)
        {
            var st = s.Stages[i];
            if (st.Delay <= 0) ResolveStage(cast, i);
            else _pending.Add(new PendingStage { Cast = cast, StageIndex = i, Tick = _sim.Tick + _sim.TicksFor(st.Delay) });
        }
    }

    public void Interrupt(Unit u, InterruptCause cause, string reason)
    {
        var cast = u.Casting;
        if (cast == null || cast.Released) return;
        var ir = cast.Skill.Interrupt;
        bool allowed = cause switch
        {
            InterruptCause.Control => ir.ByControl,
            InterruptCause.ForcedMove => ir.ByForcedMove,
            _ => true,
        };
        if (!allowed) return;
        cast.Interrupted = true;
        u.Casting = null;
        double refund = cause is InterruptCause.Death ? 0 : Math.Clamp(ir.Refund, 0, 1);
        if (cause == InterruptCause.Cancelled) refund = 1.0;
        if (refund > 0)
        {
            double ap = cast.PaidAp * refund;
            u.Ap = Math.Min(u.ApMax, u.Ap + ap);
            foreach (var kv in cast.PaidResources)
                if (u.Resources.TryGetValue(kv.Key, out var r))
                    r.Current = Math.Min(r.Max, r.Current + kv.Value * refund);
            if (ap > 0) _sim.Emit(SimEventType.ApRefund, LogLevel.Detail, $"{u.Name}: возврат {ap:0.#} ОД", u.Id, value: ap);
        }
        if (ir.CooldownOnInterrupt && cast.Skill.Cooldown > 0 && cause != InterruptCause.Cancelled)
            u.CooldownUntil[cast.Skill.Id] = _sim.Tick + _sim.TicksFor(cast.Skill.Cooldown);
        if (cause == InterruptCause.Cancelled)
        {
            _sim.Emit(SimEventType.CastInterrupted, LogLevel.Detail, $"{u.Name}: подготовка «{cast.Skill.DisplayName}» отменена", u.Id);
        }
        else if (cause != InterruptCause.Death)
        {
            _sim.Emit(SimEventType.CastInterrupted, LogLevel.Normal, $"{u.Name}: «{cast.Skill.DisplayName}» прервано — {reason}", u.Id);
            if (_sim.Rules.Autopause.SkillInterrupted && (_sim.IsPlayerFaction(u.FactionId) || cast.VisibleToPlayer))
                _sim.RequestAutopause($"прерван навык {u.Name}");
            _sim.Commands.OnCastInterrupted(u, reason);
        }
    }

    public void UpdatePendingStages()
    {
        if (_pending.Count == 0) return;
        for (int i = 0; i < _pending.Count; i++)
        {
            var p = _pending[i];
            if (p.Tick > _sim.Tick) continue;
            _pending.RemoveAt(i);
            i--;
            ResolveStage(p.Cast, p.StageIndex);
        }
    }

    // ------------------------------------------------------------------ area & resolution

    /// <summary>Cells of a stage for a cast (chains resolved against current units unless preview).</summary>
    public List<AreaCell> StageCells(SkillCast cast, SkillStageDef stage, bool preview = false) =>
        StageCells(cast.Caster, cast.Skill, stage, cast.Anchor, cast.Orientation, cast.TargetUnitId, preview);

    public List<AreaCell> StageCells(Unit caster, SkillDef skill, SkillStageDef stage, HexCoord anchor, HexDirection orientation, int targetUnitId, bool preview)
    {
        var area = stage.Area;
        List<AreaCell> cells;
        if (area.Shape == AreaShape.Chain)
            cells = ResolveChain(caster, skill, stage, anchor, preview);
        else
            cells = AreaResolver.Resolve(area, anchor, orientation, _sim.Map);

        if (stage.BlockedByObstacles && area.Shape != AreaShape.Global && area.Shape != AreaShape.Chain)
        {
            cells = cells.Where(c => c.Cell == anchor || _sim.Los.Has(_sim.Map, _sim.Rules, anchor, c.Cell)).ToList();
        }

        if (area.Shape == AreaShape.Line && !stage.Pierce)
        {
            var ordered = cells.OrderBy(c => c.Order).ToList();
            var cut = new List<AreaCell>();
            foreach (var c in ordered)
            {
                cut.Add(c);
                var cell = _sim.Map.Get(c.Cell);
                if (cell == null) continue;
                bool hasTarget = cell.Occupants.Any(o => o.Alive && o != caster && stage.Effects.Any(e => e.TargetsUnits && _sim.FilterAccepts(e.Filter, caster, o)));
                if (hasTarget || cell.TerrainBlocksLoS) break;
            }
            cells = cut;
        }
        return cells;
    }

    private List<AreaCell> ResolveChain(Unit caster, SkillDef skill, SkillStageDef stage, HexCoord anchor, bool preview)
    {
        var result = new List<AreaCell> { new(anchor, null, 0) };
        if (preview) return result;
        var filter = stage.Effects.FirstOrDefault(e => e.TargetsUnits)?.Filter ?? TargetFilterDef.EnemiesOnly();
        var hit = new HashSet<int>();
        var first = _sim.Map.Get(anchor)?.Occupant;
        if (first != null) hit.Add(first.Id);
        var cur = anchor;
        for (int j = 0; j < stage.Area.ChainJumps; j++)
        {
            Unit? best = null;
            int bestD = int.MaxValue;
            foreach (var u in _sim.Units)
            {
                if (!u.Alive || hit.Contains(u.Id) || u == caster) continue;
                if (!_sim.FilterAccepts(filter, caster, u)) continue;
                if (!LayerResolver.CanTarget(_sim.Rules, skill, u)) continue;
                int d = HexCoord.Distance(cur, u.Pos);
                if (d > stage.Area.ChainRange) continue;
                if (stage.BlockedByObstacles && !_sim.Los.Has(_sim.Map, _sim.Rules, cur, u.Pos)) continue;
                if (d < bestD || (d == bestD && best != null && u.Id < best.Id))
                {
                    best = u;
                    bestD = d;
                }
            }
            if (best == null) break;
            hit.Add(best.Id);
            cur = best.Pos;
            result.Add(new AreaCell(cur, null, j + 1));
        }
        return result;
    }

    private sealed class TargetEntry
    {
        public Unit Unit = null!;
        public AreaCell Cell;
        public List<SkillEffectDef> Effects = new();
        public AttackRoll? Roll;
    }

    /// <summary>Resolves one stage in the fixed phase order (docs/DECISIONS.md §10, steps 2–12).</summary>
    public void ResolveStage(SkillCast cast, int stageIndex)
    {
        var skill = cast.Skill;
        if (stageIndex < 0 || stageIndex >= skill.Stages.Count) return;
        var stage = skill.Stages[stageIndex];
        var caster = cast.Caster;
        var origin = caster.Alive ? caster.Pos : cast.CasterPos;
        string cause = skill.DisplayName;

        // 2. Area.
        var cells = StageCells(cast, stage);
        RecentAreas.Add((_sim.Tick, caster.Id, skill.Color, cells.Select(c => c.Cell).ToList(),
            _sim.IsPlayerFaction(caster.FactionId) || _sim.Vision.IsVisibleToPlayer(caster) || cells.Any(c => _sim.Vision.IsCellVisibleToPlayer(c.Cell))));
        while (RecentAreas.Count > 0 && RecentAreas[0].Tick < _sim.Tick - _sim.TicksPerSecond * 2) RecentAreas.RemoveAt(0);

        // 3. Targets.
        var entries = new List<TargetEntry>();
        var seen = new HashSet<int>();
        foreach (var ac in cells)
        {
            var cell = _sim.Map.Get(ac.Cell);
            if (cell == null) continue;
            foreach (var u in cell.Occupants.ToArray())
            {
                if (!u.Alive || !seen.Add(u.Id)) continue;
                if (u != caster && !LayerResolver.CanTarget(_sim.Rules, skill, u)) continue;
                var list = new List<SkillEffectDef>();
                foreach (var e in stage.Effects.Concat(ac.Mask?.Effects ?? Enumerable.Empty<SkillEffectDef>()))
                {
                    if (!e.TargetsUnits || !_sim.FilterAccepts(e.Filter, caster, u)) continue;
                    if (!_sim.Rules.Combat.FriendlyFire && u != caster && _sim.RelationOf(caster, u) == Relation.Ally && e.Type == EffectType.Damage) continue;
                    list.Add(e);
                }
                if (list.Count > 0) entries.Add(new TargetEntry { Unit = u, Cell = ac, Effects = list });
            }
        }
        entries = entries.OrderBy(t => HexCoord.Distance(t.Unit.Pos, cast.Anchor)).ThenBy(t => t.Unit.Id).ToList();
        if (stage.MaxTargets > 0 && entries.Count > stage.MaxTargets) entries = entries.Take(stage.MaxTargets).ToList();

        // 4. Rolls.
        foreach (var t in entries)
            if (t.Effects.Any(e => e.RequiresHit) && t.Unit != caster)
                t.Roll = _sim.Combat.Roll(caster, t.Unit, skill, origin);

        bool Passes(TargetEntry t, SkillEffectDef e)
        {
            if (!t.Unit.Alive) return false;
            if (e.RequiresHit && t.Roll != null && !t.Roll.Hit) return false;
            if (e.Chance < 1 && !_sim.Rng.Chance(e.Chance)) return false;
            return true;
        }

        var deferred = new List<(Unit Target, bool Surprise)>();

        // 5–6. Damage and healing (death checks inside).
        foreach (var t in entries)
        foreach (var e in t.Effects)
        {
            if (e.Type != EffectType.Damage && e.Type != EffectType.Heal) continue;
            if (!Passes(t, e)) continue;
            int dist = HexCoord.Distance(origin, t.Unit.Pos);
            double amount = _sim.Combat.EffectAmount(e, caster, t.Unit, 1.0, dist);
            if (e.Type == EffectType.Damage)
                _sim.Combat.DealDamage(caster, t.Unit, amount, e.DamageType, t.Roll, cause, deferred);
            else
            {
                if (t.Roll?.Crit == true && _sim.Rules.Combat.HealCanCrit) amount *= caster.Stat(Stats.CritMult);
                _sim.Combat.Heal(caster, t.Unit, amount * (1.0 + caster.Stat(Stats.HealPower)), cause);
            }
        }

        // 7. Statuses.
        foreach (var t in entries)
        foreach (var e in t.Effects)
            if ((e.Type == EffectType.ApplyStatus || e.Type == EffectType.RemoveStatus) && Passes(t, e))
                _sim.Combat.ApplyUnitEffect(e, caster, t.Unit, origin, 1.0, cause);

        // 8. Displacement, facing, resources/AP.
        foreach (var t in entries)
        foreach (var e in t.Effects)
            if ((e.Type is EffectType.Push or EffectType.Pull or EffectType.ChangeFacing or EffectType.ModifyResource or EffectType.ModifyAp) && Passes(t, e))
            {
                // Area blasts around a target point push away from that point; melee shoves from the caster.
                bool fromAnchor = (e.Type is EffectType.Push or EffectType.Pull) && stage.Area.Shape != AreaShape.Single && skill.Targeting == TargetingMode.Cell;
                var from = fromAnchor ? cast.Anchor : origin;
                _sim.Combat.ApplyUnitEffect(e, caster, t.Unit, from, 1.0, cause);
            }

        // 9. Interrupts of targets' preparations.
        foreach (var (target, surprise) in deferred) _sim.Combat.TryDamageInterrupt(target, surprise);

        // 10. Cell changes.
        foreach (var ac in cells)
        {
            var cell = _sim.Map.Get(ac.Cell);
            if (cell == null) continue;
            foreach (var e in stage.Effects.Concat(ac.Mask?.Effects ?? Enumerable.Empty<SkillEffectDef>()))
            {
                if (e.Type is not (EffectType.ChangeTerrain or EffectType.DestroyObstacle or EffectType.AddCellEffect or EffectType.RemoveCellEffect)) continue;
                if (e.Chance < 1 && !_sim.Rng.Chance(e.Chance)) continue;
                ApplyCellEffect(e, caster, cell, cause);
            }
        }

        // 11. Summons and caster teleport.
        foreach (var ac in cells)
        {
            var cell = _sim.Map.Get(ac.Cell);
            if (cell == null) continue;
            foreach (var e in stage.Effects.Concat(ac.Mask?.Effects ?? Enumerable.Empty<SkillEffectDef>()))
            {
                if (e.Type == EffectType.Summon) Summon(e, caster, cell, cause);
                else if (e.Type == EffectType.TeleportCaster && ac.Cell == cast.Anchor) Teleport(caster, cell, cause);
            }
        }

        // 12. Reactions are processed by the reaction queue (depth-limited).
        _sim.Reactions.Process();
    }

    private void ApplyCellEffect(SkillEffectDef e, Unit caster, Cell cell, string cause)
    {
        switch (e.Type)
        {
            case EffectType.ChangeTerrain:
            {
                var t = _sim.Content.Terrain(e.Terrain);
                if (t == null || t == cell.Terrain) return;
                // Never bury units in walls; a floor may collapse into a pit (units fall).
                if (t.Kind != TerrainKind.Pit && cell.Occupants.Any(o => o.Alive && !t.IsPassableFor(o.Def.MoveCategory) && o.LayerDef.PassesObstaclesUpTo <= 0)) return;
                _sim.CellEffects.ChangeTerrain(cell, t, cause);
                break;
            }
            case EffectType.DestroyObstacle:
                _sim.CellEffects.DamageObstacle(cell, e.ObstacleDamage, caster, cause);
                break;
            case EffectType.AddCellEffect:
            {
                var d = _sim.Content.CellEffect(e.CellEffect);
                if (d != null) _sim.CellEffects.Add(cell, d, Math.Max(1, e.Level), e.Duration, caster.Id, 0, 0);
                break;
            }
            case EffectType.RemoveCellEffect:
                _sim.CellEffects.RemoveById(cell, e.CellEffect, cause);
                break;
        }
    }

    private void Summon(SkillEffectDef e, Unit caster, Cell cell, string cause)
    {
        var def = _sim.Content.Unit(e.SummonUnit);
        if (def == null) return;
        if (MovementRules.Check(MoveContext.For(_sim, def, caster.FactionId), null, cell, false, out _) != StepCheck.Ok || !cell.AllowsPlacement)
        {
            _sim.Emit(SimEventType.Diagnostic, LogLevel.Detail, $"Призыв «{def.DisplayName}» невозможен: клетка {cell.Coord} занята или непроходима", caster.Id, cell: cell.Coord);
            return;
        }
        Unit s;
        try
        {
            s = _sim.Spawn(def, caster.FactionId, cell.Coord, caster.Facing);
        }
        catch (InvalidOperationException ex)
        {
            _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, "Призыв отменён: " + ex.Message, caster.Id);
            return;
        }
        s.SummonerId = caster.Id;
        if (e.Duration > 0) s.ExpiresAtTick = _sim.Tick + _sim.TicksFor(e.Duration);
        _sim.Emit(SimEventType.Summoned, LogLevel.Normal, $"{caster.Name} призывает «{s.Name}» — {cause}", caster.Id, s.Id, cell.Coord);
    }

    private void Teleport(Unit caster, Cell cell, string cause)
    {
        if (!caster.Alive) return;
        if (MovementRules.Check(MoveContext.For(_sim, caster), null, cell, false, out _) != StepCheck.Ok) return;
        _sim.Commands.OnForcedMove(caster);
        _sim.Emit(SimEventType.Displaced, LogLevel.Normal, $"{caster.Name} перемещается в {cell.Coord} — {cause}", caster.Id, cell: cell.Coord);
        if (!_sim.Relocate(caster, cell.Coord, caster, cause)) return;
        _sim.CellEffects.OnUnitEntered(caster, cell);
    }
}
