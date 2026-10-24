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
    /// <summary>Target unit; a series may move to another target when the first dies (docs/DECISIONS_3.md §45).</summary>
    public int TargetUnitId { get; set; } = -1;
    public HexCoord CasterPos { get; set; }
    public long StartTick { get; init; }
    public long ReleaseTick { get; set; }
    public double PaidAp { get; set; }
    /// <summary>AP to pay at the release (skills that pay at the release).</summary>
    public double DeferredAp { get; set; }
    public Dictionary<string, double> PaidResources { get; } = new();
    /// <summary>Cells shown as the threat/telegraph area (stage 0 at start of preparation).</summary>
    public List<HexCoord> TelegraphCells { get; } = new();
    public bool Released { get; set; }
    public bool Interrupted { get; set; }
    /// <summary>The player side saw the caster when the preparation started (telegraph may be shown).</summary>
    public bool VisibleToPlayer { get; set; }
    /// <summary>Damage source for the pipeline rules: a command skill or an auto-attack.</summary>
    public DamageSource Source { get; init; } = DamageSource.Skill;
    /// <summary>The «free if» condition held at the start: launch costs were not paid.</summary>
    public bool Free { get; set; }
    /// <summary>State of the series of hits (stages) after the release.</summary>
    public SeriesState Series { get; } = new();

    public double Progress(long now) => ReleaseTick <= StartTick ? 1.0 : Math.Clamp((double)(now - StartTick) / (ReleaseTick - StartTick), 0, 1);
}

public sealed class PendingStage
{
    public SkillCast Cast { get; init; } = null!;
    public int StageIndex { get; init; }
    /// <summary>Repetition of the stage (0-based) for stages with «повторить раз».</summary>
    public int Repeat { get; init; }
    public long Tick { get; init; }
}

/// <summary>One resolved hit of a series against one target (log, editor preview, tests).</summary>
public sealed record HitRecord(int Stage, int Repeat, int HitNumber, int TargetId, string TargetName, bool Hit, bool Crit, double Damage, bool Killed, string Note);

/// <summary>Series state of a released cast (docs/DECISIONS_3.md §45).</summary>
public sealed class SeriesState
{
    public bool Stopped { get; internal set; }
    public string StopReason { get; internal set; } = "";
    /// <summary>Stopped safely: only the stages marked «завершающий» still run.</summary>
    public bool SafeStop { get; internal set; }
    /// <summary>Did the previous hit land (true before the first one).</summary>
    public bool LastHit { get; internal set; } = true;
    /// <summary>Did the previous stage apply any effect (true before the first one).</summary>
    public bool LastEffect { get; internal set; } = true;
    /// <summary>Hits (damage stages × repetitions) resolved so far, and planned in total.</summary>
    public int HitsDone { get; internal set; }
    public int TotalHits { get; internal set; }
    /// <summary>Single crit roll of the series, per target.</summary>
    public Dictionary<int, bool> CritOf { get; } = new();
    public List<HitRecord> Hits { get; } = new();
    /// <summary>Tick of the last scheduled stage.</summary>
    public long LastTick { get; internal set; }
    internal string StopAfterStage = "";
    internal bool StopAfterSafe;
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

    /// <summary>The cast released last (editor preview, tests).</summary>
    public SkillCast? LastReleased { get; private set; }

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
            // Launch costs (none if «free if» holds) and the costs of the first hit: without them the skill does not start.
            bool free = IsFree(u, s, targetUnitId, targetCell ?? fromPos);
            var first = s.Stages.Count > 0 ? s.Stages[0].Costs.Where(c => c.When == StageCostWhen.PerHit).ToList() : new List<StageCostDef>();
            double ap = (free ? 0 : ApCostOf(u, s)) + first.Where(c => string.IsNullOrEmpty(c.Resource)).Sum(c => c.Amount);
            if (u.Ap + 1e-9 < ap) return new SkillCheck(FailureReason.NotEnoughAp, $"недостаточно ОД ({u.Ap:0.#}/{ap:0.#})", fromPos, facing);
            var need = new Dictionary<string, double>();
            if (!free)
                foreach (var rc in s.ResourceCosts)
                    need[rc.Resource] = need.GetValueOrDefault(rc.Resource) + rc.Amount;
            foreach (var c in first)
                if (!string.IsNullOrEmpty(c.Resource))
                    need[c.Resource] = need.GetValueOrDefault(c.Resource) + c.Amount;
            foreach (var (res, amount) in need)
            {
                if (!u.Resources.TryGetValue(res, out var r))
                    return none with { Message = $"у юнита нет ресурса «{_sim.Content.Get<ResourceDef>(res)?.DisplayName ?? res}»" };
                if (r.Current + 1e-9 < amount)
                    return new SkillCheck(FailureReason.NotEnoughResource, $"недостаточно: {r.Def.DisplayName} ({r.Current:0.#}/{amount:0.#})", fromPos, facing);
            }
        }
        else if (ApCostOf(u, s) > u.ApMax + 1e-9)
        {
            return none with { Message = $"стоимость {ApCostOf(u, s):0.#} ОД больше максимума {u.ApMax:0.#}" };
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
                return CheckRangeAndLos(u, s, fromPos, StructureSystem.AimCell(t, fromPos), facing);
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

    /// <summary>AP cost of the skill for the unit after the passives' cost modifiers (docs/DECISIONS_3.md §46).</summary>
    public double ApCostOf(Unit u, SkillDef s) => _sim.Passives.ApCost(u, s, s.ApCost);

    /// <summary>Maximum range of the skill for the unit: + «дальность атаки».</summary>
    public int RangeMaxOf(Unit u, SkillDef s) => Math.Max(s.RangeMin, s.RangeMax + (int)Math.Round(u.Stat(Stats.AttackRange)));

    /// <summary>Preparation time for the unit: ÷ (1 + «скорость применения»).</summary>
    public double PrepTimeOf(Unit u, SkillDef s) => s.PrepTime / Math.Max(0.1, 1 + u.Stat(Stats.CastSpeed));

    /// <summary>The skill's «free if» condition holds for this use.</summary>
    public bool IsFree(Unit u, SkillDef s, int targetUnitId, HexCoord anchor) =>
        s.FreeIf != null && _sim.Conditions.Evaluate(s.FreeIf, new ConditionContext(u, _sim.GetUnit(targetUnitId), anchor, null));

    private SkillCheck CheckRangeAndLos(Unit u, SkillDef s, HexCoord from, HexCoord to, HexDirection facing)
    {
        int dist = HexCoord.Distance(from, to);
        int max = RangeMaxOf(u, s) + RangeBonus(s, from, to);
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
    /// <param name="source">Skill command or auto-attack (damage source for the pipeline rules).</param>
    /// <param name="apCost">Overrides the skill's AP cost (auto-attack setting).</param>
    public SkillCast Begin(Unit u, SkillDef s, SkillCheck check, int targetUnitId, DamageSource source = DamageSource.Skill, double? apCost = null)
    {
        bool free = IsFree(u, s, targetUnitId, check.Anchor);
        double ap = free ? 0 : apCost ?? ApCostOf(u, s);
        bool deferred = s.PayAt == CostMoment.Release;
        if (!deferred) u.Ap = Math.Max(0, u.Ap - ap);
        var cast = new SkillCast
        {
            Source = source,
            Id = _nextCastId++,
            Caster = u,
            Skill = s,
            Anchor = check.Anchor,
            Orientation = check.Orientation,
            TargetUnitId = targetUnitId,
            CasterPos = u.Pos,
            StartTick = _sim.Tick,
            ReleaseTick = _sim.Tick + _sim.TicksFor(PrepTimeOf(u, s)),
            PaidAp = deferred ? 0 : ap,
            DeferredAp = deferred ? ap : 0,
            Free = free,
        };
        if (free) _sim.Emit(SimEventType.Info, LogLevel.Detail, $"{u.Name}: «{s.DisplayName}» бесплатно — {s.FreeIf!.Describe()}", u.Id);
        // Costs are paid at the start of the preparation, or at the release (docs/DECISIONS_3.md §42).
        if (!deferred && !free) PayResources(u, s, cast);
        if (s.CooldownAt == CostMoment.Start && s.Cooldown > 0) u.CooldownUntil[s.Id] = _sim.Tick + _sim.TicksFor(s.Cooldown);
        if (s.Stages.Count > 0)
            foreach (var c in StageCells(cast, s.Stages[0], preview: true))
                cast.TelegraphCells.Add(c.Cell);
        cast.VisibleToPlayer = _sim.IsPlayerFaction(u.FactionId) || _sim.Vision.IsVisibleToPlayer(u) || _sim.Options.AdminMode;
        _sim.Knowledge.OnSkillUsed(u, s);
        u.Casting = cast;
        if (ap > 0)
            _sim.Emit(SimEventType.ApSpent, LogLevel.Detail, $"{u.Name}: −{ap:0.#} ОД ({s.DisplayName}{(source == DamageSource.AutoAttack ? ", автоатака" : "")})", u.Id, value: ap);
        _sim.Emit(SimEventType.CastStarted, s.PrepTime > 0 ? LogLevel.Normal : LogLevel.Detail,
            s.PrepTime > 0 ? $"{u.Name} готовит «{s.DisplayName}» ({s.PrepTime:0.##} с)" : $"{u.Name}: «{s.DisplayName}»", u.Id, targetUnitId, check.Anchor);
        return cast;
    }

    private static void PayResources(Unit u, SkillDef s, SkillCast cast)
    {
        foreach (var rc in s.ResourceCosts)
        {
            if (!u.Resources.TryGetValue(rc.Resource, out var r)) continue;
            r.Current = Math.Max(0, r.Current - rc.Amount);
            cast.PaidResources[rc.Resource] = rc.Amount;
        }
    }

    /// <summary>Releases a prepared cast: stage 0 now (or after its delay), later stages scheduled.</summary>
    public void Release(SkillCast cast)
    {
        var u = cast.Caster;
        if (cast.DeferredAp > 0 || (cast.Skill.PayAt == CostMoment.Release && !cast.Free && cast.PaidResources.Count == 0 && cast.Skill.ResourceCosts.Count > 0))
        {
            // Costs at the release: not enough now — the skill does not fire, nothing is spent.
            string? lack = u.Ap + 1e-9 < cast.DeferredAp ? $"недостаточно ОД ({u.Ap:0.#}/{cast.DeferredAp:0.#})" : null;
            foreach (var rc in cast.Skill.ResourceCosts)
                if (lack == null && u.ResourceValue(rc.Resource) + 1e-9 < rc.Amount)
                    lack = $"недостаточно: {_sim.Content.Get<ResourceDef>(rc.Resource)?.DisplayName ?? rc.Resource}";
            if (lack != null)
            {
                Interrupt(u, InterruptCause.Cancelled, lack);
                _sim.Emit(SimEventType.CastInterrupted, LogLevel.Normal, $"{u.Name}: «{cast.Skill.DisplayName}» не сработало — {lack}", u.Id);
                _sim.Commands.OnCastInterrupted(u, lack);
                return;
            }
            u.Ap = Math.Max(0, u.Ap - cast.DeferredAp);
            cast.PaidAp = cast.DeferredAp;
            cast.DeferredAp = 0;
            PayResources(u, cast.Skill, cast);
        }
        cast.Released = true;
        u.Casting = null;
        LastReleased = cast;
        var s = cast.Skill;
        // Unit-targeted skills follow the target if it moved during preparation (still validated by range/LoS before release).
        if (s.Targeting == TargetingMode.Unit)
        {
            var t = _sim.GetUnit(cast.TargetUnitId);
            if (t != null && t.Alive)
            {
                cast.Anchor = StructureSystem.AimCell(t, u.Pos);
                cast.Orientation = u.Pos.DirectionTo(cast.Anchor) ?? cast.Orientation;
            }
        }
        if (s.UsesChosenDirection) cast.Anchor = u.Pos;
        cast.CasterPos = u.Pos;
        if (s.Cooldown > 0 && s.CooldownAt == CostMoment.Release) u.CooldownUntil[s.Id] = _sim.Tick + _sim.TicksFor(s.Cooldown);
        u.LastSkillUse[s.Id] = _sim.Tick;
        _sim.Emit(SimEventType.CastReleased, LogLevel.Detail, $"{u.Name} применяет «{s.DisplayName}»", u.Id, cast.TargetUnitId, cast.Anchor, damageType: s.Id);
        // Series: every stage (hit) N times, at its delay plus the repeat interval (docs/DECISIONS_3.md §45).
        var series = cast.Series;
        // «Количество ударов» of passives: extra repetitions of the last damage stage (docs/DECISIONS_3.md §46).
        int lastHit = s.Stages.FindLastIndex(st => st.HasDamage);
        int extra = lastHit >= 0 ? Math.Max(0, _sim.Passives.ExtraHits(u, s)) : 0;
        series.TotalHits = s.Stages.Where(st => st.HasDamage).Sum(st => Math.Max(1, st.Repeat)) + extra;
        series.LastTick = _sim.Tick;
        var now = new List<(int Stage, int Repeat)>();
        for (int i = 0; i < s.Stages.Count; i++)
        {
            var st = s.Stages[i];
            int reps = Math.Max(1, Math.Min(st.Repeat + (i == lastHit ? extra : 0), MaxRepeat));
            for (int r = 0; r < reps; r++)
            {
                double delay = st.Delay + r * (st.Repeat > 1 || st.RepeatInterval > 0 ? st.RepeatInterval : 0.3);
                long tick = _sim.Tick + _sim.TicksFor(delay);
                if (delay <= 0 || tick <= _sim.Tick) now.Add((i, r));
                else _pending.Add(new PendingStage { Cast = cast, StageIndex = i, Repeat = r, Tick = tick });
                series.LastTick = Math.Max(series.LastTick, tick);
            }
        }
        foreach (var (i, r) in now) ResolveStage(cast, i, r);
        _sim.Reactions.Enqueue(PassiveTrigger.OnSkillUsed, u, _sim.GetUnit(cast.TargetUnitId), s.Id);
        if (s.Defense.Enabled && u.Alive && !series.Stopped) _sim.Defense.Activate(u, s, 0, ActiveCommandId(u));
    }

    /// <summary>Hard cap of repetitions of one stage (the validator rejects more).</summary>
    public const int MaxRepeat = 100;

    private static int ActiveCommandId(Unit u) => u.Queue.Items.FirstOrDefault(c => c.Status == CommandStatus.Active)?.Id ?? -1;

    /// <summary>Tick until which the caster is held by the series (the skill's «занят до конца серии»), else now.</summary>
    public long SeriesEnd(SkillCast cast) => cast.Skill.HoldDuringSeries && !cast.Series.Stopped ? Math.Max(_sim.Tick, cast.Series.LastTick) : _sim.Tick;

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
            ResolveStage(p.Cast, p.StageIndex, p.Repeat);
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
        public bool WasAlive;
        public bool Landed = true;
        public double Dealt;
    }

    /// <summary>
    /// Resolves one stage (one hit of a series) — docs/DECISIONS.md §10 steps 2–12, preceded by the series
    /// checks of docs/DECISIONS_3.md §45: caster, target (stop or retarget), condition, costs of the hit,
    /// movement of the caster; followed by the costs on hit/miss, refunds and the series state.
    /// </summary>
    public void ResolveStage(SkillCast cast, int stageIndex, int repeat = 0)
    {
        var skill = cast.Skill;
        if (stageIndex < 0 || stageIndex >= skill.Stages.Count) return;
        var stage = skill.Stages[stageIndex];
        var caster = cast.Caster;
        var series = cast.Series;
        if (series.Stopped && !(series.SafeStop && stage.Final)) return;
        string cause = skill.DisplayName;
        string label = StageLabel(cast, stageIndex, repeat);

        // Series checks: the caster.
        if (!caster.Alive && skill.StopOnCasterDeath)
        {
            Stop(cast, "исполнитель погиб");
            return;
        }
        if (caster.Alive && skill.StopOnControl && !stage.Final && (caster.Flags.BlocksActions || caster.Flags.BlocksSkills && !IsBasicAttack(caster, skill)))
        {
            Stop(cast, "исполнитель под контролем");
            return;
        }

        // The target: follow it, or stop / move to another one when it died.
        Unit? target = null;
        if (skill.Targeting == TargetingMode.Unit)
        {
            target = _sim.GetUnit(cast.TargetUnitId);
            if (target == null || !target.Alive)
            {
                var rule = stage.Retarget == RetargetRule.Inherit ? skill.RetargetOnDeath : stage.Retarget;
                if (rule is RetargetRule.Nearest or RetargetRule.ByRules && Retarget(cast, rule, target?.Name ?? "цель", out var other))
                    target = other;
                else if (skill.StopOnTargetDeath && !stage.Final)
                {
                    Stop(cast, $"{target?.Name ?? "цель"} погибла — оставшиеся удары отменены");
                    return;
                }
                else target = null;
            }
            else if (stageIndex > 0 || repeat > 0)
            {
                cast.Anchor = StructureSystem.AimCell(target, caster.Pos);
                if (caster.Alive) cast.Orientation = caster.Pos.DirectionTo(cast.Anchor) ?? cast.Orientation;
            }
        }
        else
        {
            target = _sim.GetUnit(cast.TargetUnitId) is { Alive: true } tu ? tu : _sim.Map.Get(cast.Anchor)?.Occupants.FirstOrDefault(o => o.Alive && o != caster);
        }

        // Condition of the stage (AND/OR/NOT).
        if (stage.Condition != null && !_sim.Conditions.Evaluate(stage.Condition, new ConditionContext(caster, target, cast.Anchor, series)))
        {
            if (stage.OnConditionFail == ConditionFailAction.StopSeries)
            {
                Stop(cast, $"условие «{stage.Condition.Describe()}» не выполнено");
                return;
            }
            _sim.Emit(SimEventType.Info, LogLevel.Detail, $"{caster.Name}: «{cause}» — {label} пропущен: условие «{stage.Condition.Describe()}» не выполнено", caster.Id);
            return;
        }

        // Costs of the hit (before it): not enough — the series' policy.
        if (caster.Alive && stage.Costs.Count > 0)
        {
            string lack = PayStage(cast, stage, StageCostWhen.PerHit, label);
            if (lack != "" && !ResourceOut(cast, stage, lack, before: true)) return;
        }

        // Dash to the target / retreat before the hit.
        if (stage.Move != StageMove.None && caster.Alive) MoveCaster(cast, stage, target);
        if (skill.UsesChosenDirection && caster.Alive)
        {
            cast.Anchor = caster.Pos;
            if (target != null && target.Alive) cast.Orientation = caster.Pos.DirectionTo(target.Pos) ?? cast.Orientation;
        }
        var origin = caster.Alive ? caster.Pos : cast.CasterPos;

        // A later hit on a unit that is now out of reach (pushed, fled) misses this time; the series goes on.
        if (skill.Targeting == TargetingMode.Unit && target != null && (stageIndex > 0 || repeat > 0) && stage.HasDamage)
        {
            var aim = StructureSystem.AimCell(target, origin);
            int d = HexCoord.Distance(origin, aim);
            if (d > skill.RangeMax + RangeBonus(skill, origin, aim))
            {
                series.LastHit = false;
                series.LastEffect = false;
                series.HitsDone++;
                series.Hits.Add(new HitRecord(stageIndex, repeat, series.HitsDone, target.Id, target.Name, false, false, 0, false, "вне досягаемости"));
                _sim.Emit(SimEventType.SkillHit, LogLevel.Normal, $"«{cause}» · {label}: {target.Name} вне досягаемости", caster.Id, target.Id);
                AfterStage(cast);
                return;
            }
        }

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
                if (list.Count > 0) entries.Add(new TargetEntry { Unit = u, Cell = ac, Effects = list, WasAlive = true });
            }
        }
        entries = entries.OrderBy(t => HexCoord.Distance(t.Unit.Pos, cast.Anchor)).ThenBy(t => t.Unit.Id).ToList();
        if (stage.MaxTargets > 0 && entries.Count > stage.MaxTargets) entries = entries.Take(stage.MaxTargets).ToList();

        // 4. Rolls — every hit separately, with the stage's parameters; the series' single crit if so set.
        var spec = HitSpec.Of(stage);
        foreach (var t in entries)
            if (t.Effects.Any(e => e.RequiresHit) && t.Unit != caster)
            {
                bool once = skill.SeriesCrit == SeriesCritMode.OncePerSeries;
                var s = once && series.CritOf.TryGetValue(t.Unit.Id, out var c) ? spec with { ForcedCrit = c } : spec;
                t.Roll = _sim.Combat.Roll(caster, t.Unit, skill, origin, s);
                t.Landed = t.Roll.Hit;
                if (once && t.Roll.Hit && !series.CritOf.ContainsKey(t.Unit.Id)) series.CritOf[t.Unit.Id] = t.Roll.Crit;
            }

        bool anyEffect = false;
        bool Passes(TargetEntry t, SkillEffectDef e)
        {
            if (!t.Unit.Alive) return false;
            if (e.RequiresHit && t.Roll != null && !t.Roll.Hit) return false;
            double chance = e.Type == EffectType.ApplyStatus ? Math.Min(1, e.Chance * stage.StatusChanceMultiplier) : e.Chance;
            if (chance < 1 && !_sim.Rng.Chance(chance)) return false;
            anyEffect = true;
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
            {
                double power = 1 + _sim.Passives.HitPowerPercent(caster, skill, stage.Tag) / 100.0;
                amount = Math.Max(0, amount * stage.DamageMultiplier * Math.Max(0, power) + stage.FlatBonus);
                var r = _sim.Damage.Apply(new DamageRequest
                {
                    Attacker = caster, Target = t.Unit, Amount = amount, DamageType = string.IsNullOrEmpty(stage.DamageType) ? e.DamageType : stage.DamageType,
                    Source = cast.Source, Skill = skill, Roll = t.Roll, Cause = cause, DeferredInterrupts = deferred,
                    CritMultiplier = stage.CritMultiplier, HitTag = stage.Tag,
                });
                t.Dealt += r.Dealt;
                if (r.Missed) t.Landed = false;
            }
            else
            {
                if (t.Roll?.Crit == true && _sim.Rules.Combat.HealCanCrit) amount *= stage.CritMultiplier > 0 ? stage.CritMultiplier : caster.Stat(Stats.CritMult);
                _sim.Combat.Heal(caster, t.Unit, amount, cause, e.HealMode);
            }
        }

        // Crit effects (once per target, also for crits without extra damage).
        if (skill.CritEffects.Count > 0)
            foreach (var t in entries)
                if (t.Roll?.Crit == true && t.Unit.Alive)
                    _sim.Combat.ApplyDirect(caster, t.Unit, skill.CritEffects, 1.0, cause + " (крит)", cast.Source);

        // 7. Statuses and shields.
        foreach (var t in entries)
        foreach (var e in t.Effects)
            if ((e.Type is EffectType.ApplyStatus or EffectType.RemoveStatus or EffectType.Shield or EffectType.RemoveShield
                     or EffectType.ModifyStat or EffectType.ModifyAccumulation) && Passes(t, e))
                _sim.Combat.ApplyUnitEffect(e, caster, t.Unit, origin, 1.0, cause);

        // 8. Displacement, facing, resources/AP.
        foreach (var t in entries)
        foreach (var e in t.Effects)
            if ((e.Type is EffectType.Push or EffectType.Pull or EffectType.ChangeFacing or EffectType.ModifyResource or EffectType.ModifyAp
                     or EffectType.SetRoute or EffectType.SetAiProfile) && Passes(t, e))
            {
                // Area blasts around a target point push away from that point; melee shoves from the caster.
                bool fromAnchor = (e.Type is EffectType.Push or EffectType.Pull) && stage.Area.Shape != AreaShape.Single && skill.Targeting == TargetingMode.Cell;
                var from = fromAnchor ? cast.Anchor : origin;
                _sim.Combat.ApplyUnitEffect(e, caster, t.Unit, from, 1.0, cause);
            }

        // 9. Interrupts of targets' preparations.
        foreach (var (victim, surprise) in deferred) _sim.Combat.TryDamageInterrupt(victim, surprise);

        // 10. Cell changes.
        foreach (var ac in cells)
        {
            var cell = _sim.Map.Get(ac.Cell);
            if (cell == null) continue;
            foreach (var e in stage.Effects.Concat(ac.Mask?.Effects ?? Enumerable.Empty<SkillEffectDef>()))
            {
                if (e.Type is not (EffectType.ChangeTerrain or EffectType.DestroyObstacle or EffectType.AddCellEffect or EffectType.RemoveCellEffect)) continue;
                if (e.Chance < 1 && !_sim.Rng.Chance(e.Chance)) continue;
                anyEffect = true;
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

        // 12. Operations without a target (message, visual/sound effect, flag, trigger): once per stage.
        foreach (var e in stage.Effects)
            if (e.IsGlobal)
                _sim.Combat.ApplyGlobal(e, caster, cast.Anchor, cause);

        // Series: the result of the hit, its costs on hit/miss and refunds.
        var rolled = entries.Where(t => t.Roll != null).ToList();
        bool landed = rolled.Count == 0 ? entries.Count > 0 : rolled.Any(t => t.Landed);
        bool killed = entries.Any(t => t.WasAlive && !t.Unit.Alive && t.Unit != caster);
        if (stage.HasDamage)
        {
            series.HitsDone++;
            foreach (var t in entries.Where(t => t.Unit != caster && t.Effects.Any(e => e.Type == EffectType.Damage)))
                series.Hits.Add(new HitRecord(stageIndex, repeat, series.HitsDone, t.Unit.Id, t.Unit.Name, t.Landed, t.Roll?.Crit == true, t.Dealt,
                    t.WasAlive && !t.Unit.Alive, t.Roll == null ? "" : t.Roll.Summary()));
            if (series.TotalHits > 1)
            {
                string res = entries.Count == 0 ? "нет целей"
                    : string.Join(", ", entries.Where(t => t.Unit != caster).Select(t => $"{t.Unit.Name} — {(t.Landed ? (t.Roll?.Crit == true ? "крит, " : "") + $"{t.Dealt:0} урона" : "промах")}{(t.WasAlive && !t.Unit.Alive ? ", погиб" : "")}"));
                _sim.Emit(SimEventType.SkillHit, LogLevel.Normal, $"«{cause}» · {label}{(stage.DamageMultiplier != 1 ? $" ({stage.DamageMultiplier:P0})" : "")}: {res}", caster.Id, target?.Id ?? -1, cast.Anchor);
            }
        }
        if (rolled.Count > 0 || stage.HasDamage) series.LastHit = landed;
        series.LastEffect = anyEffect;
        if (caster.Alive && stage.Costs.Count > 0)
        {
            string lack = PayStage(cast, stage, landed ? StageCostWhen.OnHit : StageCostWhen.OnMiss, label);
            if (lack != "") ResourceOut(cast, stage, lack, before: false);
            if (killed) PayStage(cast, stage, StageCostWhen.RefundOnKill, label);
            if (!landed) PayStage(cast, stage, StageCostWhen.RefundOnMiss, label);
        }

        _sim.Emit(SimEventType.StageResolved, LogLevel.Diagnostic, $"«{cause}» · {label}", caster.Id, target?.Id ?? -1, cast.Anchor,
            landed || rolled.Count == 0 ? stageIndex : -(stageIndex + 1), skill.Id);

        // 13. Reactions are processed by the reaction queue (depth-limited).
        _sim.Reactions.Process();
        AfterStage(cast);
    }

    private void AfterStage(SkillCast cast)
    {
        var series = cast.Series;
        if (series.StopAfterStage != "")
        {
            string why = series.StopAfterStage;
            series.StopAfterStage = "";
            Stop(cast, why, series.StopAfterSafe, beforeStage: false);
        }
    }

    /// <summary>«Удар 2 из 3» for damage stages of a series, «этап N» otherwise.</summary>
    private static string StageLabel(SkillCast cast, int stageIndex, int repeat)
    {
        var s = cast.Skill;
        var st = s.Stages[stageIndex];
        if (!st.HasDamage || cast.Series.TotalHits <= 1)
            return $"этап {stageIndex + 1}" + (string.IsNullOrEmpty(st.Name) ? "" : $" «{st.Name}»");
        int n = 0;
        for (int i = 0; i < stageIndex; i++)
            if (s.Stages[i].HasDamage)
                n += Math.Max(1, s.Stages[i].Repeat);
        return $"удар {n + repeat + 1} из {cast.Series.TotalHits}";
    }

    /// <summary>Stops the series: the remaining stages are cancelled (a safe stop keeps the «завершающие» ones).</summary>
    public void Stop(SkillCast cast, string reason, bool safe = false, bool beforeStage = true)
    {
        var series = cast.Series;
        if (series.Stopped) return;
        series.Stopped = true;
        series.SafeStop = safe;
        series.StopReason = reason;
        int removed = _pending.RemoveAll(p => p.Cast == cast && !(safe && p.Cast.Skill.Stages[p.StageIndex].Final));
        var u = cast.Caster;
        _sim.Emit(SimEventType.SeriesStopped, removed > 0 || beforeStage ? LogLevel.Normal : LogLevel.Detail,
            $"{u.Name}: «{cast.Skill.DisplayName}» прекращено — {reason}" + (removed > 0 ? $" (отменено этапов: {removed})" : ""), u.Id, cast.TargetUnitId);
        // The caster held by the series is free after the recovery.
        if (cast.Skill.HoldDuringSeries && u.Alive)
            u.BusyUntil = Math.Min(u.BusyUntil, _sim.Tick + _sim.TicksFor(cast.Skill.RecoveryTime));
        if (cast.Skill.Defense.Enabled && u.Stance?.Skill == cast.Skill && reason.StartsWith("не хватает", StringComparison.Ordinal))
            _sim.Defense.End(u, reason);
    }

    /// <summary>A new target of the series after the first one died: the nearest valid one, or by the skill's rules.</summary>
    private bool Retarget(SkillCast cast, RetargetRule rule, string deadName, out Unit? pick)
    {
        pick = null;
        var u = cast.Caster;
        var s = cast.Skill;
        if (!u.Alive) return false;
        var pool = new List<Unit>();
        foreach (var t in _sim.Units)
        {
            if (!t.Alive || t == u || t.Id == cast.TargetUnitId) continue;
            var chk = Validate(u, s, t.Pos, t.Id, null, u.Pos, u.Facing, checkCosts: false, checkState: false);
            if (chk.Ok) pool.Add(t);
        }
        if (pool.Count == 0) return false;
        string why;
        if (rule == RetargetRule.ByRules)
        {
            pick = _sim.Targets.Choose(u, pool, s.AutoTarget.Rules, s.AutoTarget.Mode, s.AutoTarget.RandomTies, s, -1, out why);
        }
        else
        {
            var at = cast.Anchor;
            pick = pool.OrderBy(t => HexCoord.Distance(t.Pos, at)).ThenBy(t => HexCoord.Distance(t.Pos, u.Pos)).ThenBy(t => t.Id).First();
            why = "ближайшая";
        }
        if (pick == null) return false;
        cast.TargetUnitId = pick.Id;
        cast.Anchor = pick.Pos;
        cast.Orientation = u.Pos.DirectionTo(pick.Pos) ?? cast.Orientation;
        _sim.Emit(SimEventType.Info, LogLevel.Normal, $"{u.Name}: «{s.DisplayName}» — {deadName} погибла, удар перенаправлен на {pick.Name} ({why})", u.Id, pick.Id, pick.Pos);
        return true;
    }

    /// <summary>
    /// Pays (or refunds) the stage costs of one moment. Before the hit the payment is atomic; on hit/miss it is
    /// paid as far as possible. Debt only with the «в долг» policy and down to the resource's minimum.
    /// Returns the name of the missing resource ("" — all paid).
    /// </summary>
    private string PayStage(SkillCast cast, SkillStageDef st, StageCostWhen when, string label)
    {
        var u = cast.Caster;
        var costs = st.Costs.Where(c => c.When == when && c.Amount > 0).ToList();
        if (costs.Count == 0) return "";
        bool refund = when is StageCostWhen.RefundOnKill or StageCostWhen.RefundOnMiss;
        bool debt = cast.Skill.OnResourceOut == ResourceOutPolicy.Debt;
        string lack = "";
        double Floor(StageCostDef c) => string.IsNullOrEmpty(c.Resource) ? 0
            : debt && u.Resources.TryGetValue(c.Resource, out var r) ? Math.Min(0, r.Def.MinValue) : 0;
        double Current(StageCostDef c) => string.IsNullOrEmpty(c.Resource) ? u.Ap : u.Resources.TryGetValue(c.Resource, out var r) ? r.Current : double.NaN;
        string Name(StageCostDef c) => string.IsNullOrEmpty(c.Resource) ? "ОД" : _sim.Content.Get<ResourceDef>(c.Resource)?.DisplayName ?? c.Resource;
        if (!refund)
            foreach (var c in costs)
            {
                double cur = Current(c);
                if (double.IsNaN(cur) || cur - c.Amount < Floor(c) - 1e-9)
                {
                    lack = Name(c);
                    break;
                }
            }
        // Before the hit: nothing is paid when something is missing (the policy decides what happens next).
        if (lack != "" && when == StageCostWhen.PerHit && cast.Skill.OnResourceOut != ResourceOutPolicy.FinishStage) return lack;
        foreach (var c in costs)
        {
            double cur = Current(c);
            if (double.IsNaN(cur)) continue;
            double next = refund ? cur + c.Amount : Math.Max(Floor(c), cur - c.Amount);
            if (string.IsNullOrEmpty(c.Resource))
            {
                next = Math.Min(next, u.ApMax);
                u.Ap = next;
            }
            else
            {
                var r = u.Resources[c.Resource];
                next = Math.Min(next, r.Max);
                r.Current = next;
                if (next < 0 && cur >= 0)
                    _sim.Emit(SimEventType.ResourceChanged, LogLevel.Normal, $"{u.Name}: {Name(c)} уходит в долг ({next:0.#})", u.Id, value: next);
            }
            double delta = next - cur;
            if (Math.Abs(delta) > 1e-9)
                _sim.Emit(refund ? SimEventType.ApRefund : SimEventType.ApSpent, LogLevel.Detail,
                    $"{u.Name}: {Name(c)} {delta:+0.#;-0.#} («{cast.Skill.DisplayName}», {label}{(refund ? ", возврат" : "")})", u.Id, value: Math.Abs(delta));
        }
        return lack;
    }

    /// <summary>
    /// A resource ran out during the series. Before the hit: true — the hit still happens (finish the stage, or
    /// a «завершающий» stage of a safe stop). After the hit: the series stops after this stage.
    /// </summary>
    private bool ResourceOut(SkillCast cast, SkillStageDef st, string lack, bool before)
    {
        string why = $"не хватает: {lack}";
        switch (cast.Skill.OnResourceOut)
        {
            case ResourceOutPolicy.FinishStage:
                cast.Series.StopAfterStage = why;
                return true;
            case ResourceOutPolicy.SafeStop:
                if (before)
                {
                    Stop(cast, why, safe: true);
                    return st.Final;
                }
                cast.Series.StopAfterStage = why;
                cast.Series.StopAfterSafe = true;
                return true;
            default:
                // «Cancel the remaining», and debt beyond the resource's minimum.
                if (before)
                {
                    Stop(cast, why);
                    return false;
                }
                cast.Series.StopAfterStage = why;
                return true;
        }
    }

    /// <summary>Dash towards the target (stops next to it) or retreat away from it, hex by hex, before the hit.</summary>
    private void MoveCaster(SkillCast cast, SkillStageDef st, Unit? target)
    {
        var u = cast.Caster;
        if (u.Flags.BlocksMovement || u.Flags.BlocksActions || u.StepTo != null) return;
        var goal = target is { Alive: true } ? target.Pos : cast.Anchor;
        bool dash = st.Move == StageMove.DashToTarget;
        var mc = MoveContext.For(_sim, u);
        int moved = 0;
        for (int i = 0; i < st.MoveDistance; i++)
        {
            var cur = _sim.Map.Get(u.Pos);
            if (cur == null) break;
            int d0 = HexCoord.Distance(u.Pos, goal);
            if (dash && d0 <= 1) break;
            Cell? best = null;
            int bestD = d0;
            foreach (var n in cur.Neighbors)
            {
                if (n == null || n.Coord == goal) continue;
                if (MovementRules.Check(mc, cur, n, false, out _) != StepCheck.Ok) continue;
                int d = HexCoord.Distance(n.Coord, goal);
                if (dash ? d < bestD : d > bestD)
                {
                    best = n;
                    bestD = d;
                }
            }
            if (best == null) break;
            if (st.MoveApPerHex > 0)
            {
                if (u.Ap + 1e-9 < st.MoveApPerHex) break;
                u.Ap -= st.MoveApPerHex;
            }
            if (!_sim.Relocate(u, best.Coord, u, cast.Skill.DisplayName)) break;
            _sim.CellEffects.OnUnitEntered(u, best);
            moved++;
            if (!u.Alive) return;
        }
        if (moved == 0) return;
        if (u.Def.Perception.HasFacing && !u.Flags.BlocksTurning) u.Facing = u.Pos.DirectionTo(goal) ?? u.Facing;
        cast.CasterPos = u.Pos;
        _sim.Vision.MarkDirty();
        _sim.Emit(SimEventType.Displaced, LogLevel.Normal, $"{u.Name}: {(dash ? "рывок" : "отступление")} на {moved} гекс. («{cast.Skill.DisplayName}»)", u.Id, cell: u.Pos);
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
                if (t.Kind != TerrainKind.Pit && cell.Occupants.Any(o => o.Alive && !MovementRules.CanPassTerrain(_sim, o, t) && o.LayerDef.PassesObstaclesUpTo <= 0)) return;
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
