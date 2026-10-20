using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

public sealed class CommandEstimate
{
    public double Ap { get; init; }
    public double Seconds { get; init; }
    public HexCoord EndPos { get; init; }
    public HexDirection EndFacing { get; init; }
    public List<HexCoord> Path { get; init; } = new();
    public bool Feasible { get; init; } = true;
    public string Problem { get; init; } = "";
    public HexCoord? Suggestion { get; init; }
}

public sealed class ProjectedCommand
{
    public Command Command { get; init; } = null!;
    public int Index { get; init; }
    public HexCoord StartPos { get; init; }
    public HexCoord EndPos { get; init; }
    public HexDirection StartFacing { get; init; }
    public HexDirection EndFacing { get; init; }
    public CommandEstimate Estimate { get; init; } = null!;
    /// <summary>Reserved AP of all commands up to and including this one.</summary>
    public double CumulativeAp { get; init; }
    /// <summary>Current AP − cumulative reserve (without regeneration).</summary>
    public double RemainingNoRegen { get; init; }
    /// <summary>Estimated waiting for AP regeneration before this command can start.</summary>
    public double WaitForApSeconds { get; init; }
    /// <summary>Estimated start time from now (seconds) including movement and waits.</summary>
    public double StartSeconds { get; init; }
}

/// <summary>Route highlight marks of a hex (docs/DECISIONS_3.md §41) — shown as icons and patterns, not only colour.</summary>
[System.Flags]
public enum CellHazard
{
    None = 0,
    /// <summary>Fire, poison, lava and other damaging cell effects or terrain.</summary>
    Damage = 1,
    /// <summary>Inside the detection zone of a visible enemy.</summary>
    Detection = 2,
    /// <summary>Inside the auto-attack range of a visible enemy.</summary>
    AutoAttack = 4,
    /// <summary>Passable only with another movement ability (flying, swimming…).</summary>
    NeedsAbility = 8,
    /// <summary>Wall, obstacle or blocking effect.</summary>
    Obstacle = 16,
    Ally = 32,
    Enemy = 64,
    /// <summary>The unit's layer cannot be there (pit for a ground walker, etc.).</summary>
    WrongLayer = 128,
    /// <summary>Movement cost changed by an effect or override.</summary>
    CostChanged = 256,
}

public sealed record ActionInfo(string Key, string Label, bool Enabled, string Reason, string Warning = "", SkillDef? Skill = null);

public sealed class SkillPreviewCell
{
    public HexCoord Cell { get; init; }
    public bool Blocked { get; init; }
    public int StageIndex { get; init; }
    public bool HasMaskEffects { get; init; }
}

public sealed class SkillPreview
{
    public bool Valid { get; init; }
    public string Problem { get; init; } = "";
    public FailureReason Reason { get; init; }
    public HexCoord Anchor { get; init; }
    public HexDirection Orientation { get; init; }
    public List<SkillPreviewCell> Cells { get; } = new();
    public List<Unit> Enemies { get; } = new();
    public List<Unit> Allies { get; } = new();
    public List<string> Warnings { get; } = new();
    public int RangeMax { get; init; }
    public int RangeMin { get; init; }
    public List<(Unit Target, double Hit, double Notice, double Surprise, AttackSector Sector)> Odds { get; } = new();
}

/// <summary>
/// Read-only planning helpers for the UI and AI: estimates, queue projection, action
/// availability and skill area previews. Never changes the simulation state.
/// </summary>
public sealed class Planner
{
    private readonly Simulation _sim;

    public Planner(Simulation sim)
    {
        _sim = sim;
    }

    private RulesDef Rules => _sim.Rules;

    public SkillDef? SkillOf(Unit u, Command c) => c.Type == CommandType.Attack ? u.BasicAttack : _sim.Content.Skill(c.SkillId);

    // ------------------------------------------------------------------ player routes (docs/DECISIONS_3.md §41)

    /// <summary>
    /// The path of a move command from a planned position: the player's routing mode (weights and
    /// tie-breaks) when the command has one, otherwise the fast cell search used by the AI.
    /// </summary>
    public PathResult Route(Unit u, Command c, HexCoord from, HexDirection facing, HexCoord to, bool ignoreUnits = false, bool computeNearest = false,
        HashSet<HexCoord>? avoid = null)
    {
        if (c.Routing is { } mode)
        {
            var w = RouteWeights.For(mode, c.RouteWeights);
            if (mode == RouteMode.MinCost) w.Hazard = Rules.Move.HazardPenalty;
            var threats = w.Threat > 0 ? Threats(u) : null;
            return _sim.Pathfinder.FindRoute(_sim.Map, MoveContext.For(_sim, u), from, facing, to,
                new RouteSpec { Weights = w, TieBreak = Rules.Move.TieBreak, Threats = threats, IgnoreUnits = ignoreUnits, ComputeNearest = computeNearest, Avoid = avoid });
        }
        double hazard = u.AiControlled && u.AiProfile != null ? u.AiProfile.HazardAvoidance : Rules.Move.HazardPenalty;
        return _sim.Pathfinder.Find(_sim.Map, MoveContext.For(_sim, u), from, to,
            new PathOptions { HazardPenalty = hazard, IgnoreUnits = ignoreUnits, ComputeNearest = computeNearest, Avoid = avoid });
    }

    private readonly Dictionary<string, (long Tick, int Version, Dictionary<HexCoord, ThreatKind> Map)> _threats = new();

    /// <summary>
    /// Cells threatened by enemies the unit's side can see: their detection range/zone and the range of
    /// their auto-attacks. Hidden enemies add nothing (fog of war is not revealed). Cached ~0.5 s.
    /// </summary>
    public IReadOnlyDictionary<HexCoord, ThreatKind> Threats(Unit u)
    {
        string key = u.FactionId;
        long bucket = _sim.Tick / Math.Max(1, _sim.TicksPerSecond / 2);
        if (_threats.TryGetValue(key, out var cached) && cached.Tick == bucket && cached.Version == _sim.Map.Version) return cached.Map;
        var map = new Dictionary<HexCoord, ThreatKind>();
        foreach (var e in _sim.Units)
        {
            if (!e.Alive || _sim.RelationOf(u, e) != Relation.Enemy) continue;
            if (!_sim.Vision.CanFactionSee(u.FactionId, e)) continue;
            int det = (int)Math.Floor(e.Stat(Stats.DetectionRange) + 1e-9);
            foreach (var c in HexCoord.Spiral(e.Pos, det))
            {
                if (!_sim.Map.Contains(c)) continue;
                int d = HexCoord.Distance(e.Pos, c);
                if (d > 1 && !_sim.Vision.InZone(e, c)) continue;
                map[c] = map.GetValueOrDefault(c) | ThreatKind.Detection;
            }
            for (int i = 0; i < e.Def.AutoAttacks.Count; i++)
            {
                var a = e.Def.AutoAttacks[i];
                if (!a.Enabled || !Rules.AutoAttacks.Enabled) continue;
                var sk = _sim.AutoAttacks.SkillOf(e, i);
                int range = a.Range > 0 ? a.Range : sk?.RangeMax ?? 1;
                foreach (var c in HexCoord.Spiral(e.Pos, range))
                    if (c != e.Pos && _sim.Map.Contains(c))
                        map[c] = map.GetValueOrDefault(c) | ThreatKind.AutoAttack;
            }
        }
        _threats[key] = (bucket, _sim.Map.Version, map);
        return map;
    }

    /// <summary>
    /// What the route highlight marks on a hex for this unit (docs/DECISIONS_3.md §41): damage, threats,
    /// special ability needed, obstacle, occupants, wrong layer, changed cost. Uses only what the side sees.
    /// </summary>
    public CellHazard HazardsOf(Unit u, Cell cell, IReadOnlyDictionary<HexCoord, ThreatKind>? threats = null)
    {
        var h = CellHazard.None;
        if (cell.IsHazard) h |= CellHazard.Damage;
        if (threats != null && threats.TryGetValue(cell.Coord, out var tk))
        {
            if ((tk & ThreatKind.Detection) != 0) h |= CellHazard.Detection;
            if ((tk & ThreatKind.AutoAttack) != 0) h |= CellHazard.AutoAttack;
        }
        var mc = MoveContext.For(_sim, u);
        var st = MovementRules.Check(mc, null, cell, true, out _);
        switch (st)
        {
            case StepCheck.Impassable:
                h |= Rules.Move.MovementCategories.Any(cat => cat != u.Def.MoveCategory && cell.Terrain.IsPassableFor(cat)) ? CellHazard.NeedsAbility : CellHazard.Obstacle;
                break;
            case StepCheck.Blocked:
            case StepCheck.Barrier:
                h |= CellHazard.Obstacle;
                break;
            case StepCheck.Pit:
                h |= CellHazard.WrongLayer;
                break;
        }
        foreach (var o in cell.Occupants)
        {
            if (!o.Alive || o == u) continue;
            if (!_sim.IsHostile(u, o)) h |= CellHazard.Ally;
            else if (_sim.Vision.CanFactionSee(u.FactionId, o)) h |= CellHazard.Enemy;
        }
        if (st == StepCheck.Ok && (cell.EffectMoveCost > 0 || cell.MoveCostOverride.HasValue)) h |= CellHazard.CostChanged;
        return h;
    }

    /// <summary>Estimate of a command executed from a planned position and facing.</summary>
    public CommandEstimate Estimate(Unit u, Command c, HexCoord pos, HexDirection facing)
    {
        switch (c.Type)
        {
            case CommandType.Move:
            {
                var target = c.TargetCell ?? pos;
                if (target == pos) return new CommandEstimate { EndPos = pos, EndFacing = facing };
                var path = Route(u, c, pos, facing, target, ignoreUnits: pos != u.Pos && pos != (u.StepTo ?? u.Pos), computeNearest: !u.AiControlled);
                if (!path.Found)
                    return new CommandEstimate { EndPos = pos, EndFacing = facing, Feasible = false, Problem = path.Reason, Suggestion = path.Nearest };
                double ap = path.Cost * Rules.Ap.MovePerCost;
                double secs = 0;
                double speed = Math.Max(0.01, u.Stat(Stats.MoveSpeed));
                var f = facing;
                var prev = pos;
                foreach (var (step, cost) in path.Path.Zip(path.StepCosts))
                {
                    secs += Math.Max(_sim.Dt, cost / speed);
                    var d = prev.DirectionTo(step)!.Value;
                    if (Rules.Turn.AutoFaceOnMove)
                    {
                        if (Rules.Turn.AutoFaceCostsAp) ap += HexDirectionExt.StepsBetween(f, d) * Rules.Turn.ApPerStep;
                        f = d;
                    }
                    prev = step;
                }
                if (c.ArrivalFacing is { } af)
                {
                    var (tap, tsecs) = ManualTurnCost(f, af);
                    ap += tap;
                    secs += tsecs;
                    f = af;
                }
                secs += c.WaitAfter;
                return new CommandEstimate { Ap = ap, Seconds = secs, EndPos = target, EndFacing = f, Path = path.Path };
            }
            case CommandType.Turn:
            {
                var d = c.Direction ?? facing;
                var (tap, tsecs) = ManualTurnCost(facing, d);
                return new CommandEstimate { Ap = tap, Seconds = tsecs, EndPos = pos, EndFacing = d };
            }
            case CommandType.Attack:
            case CommandType.Skill:
            {
                var s = SkillOf(u, c);
                if (s == null) return new CommandEstimate { EndPos = pos, EndFacing = facing, Feasible = false, Problem = "навык не найден" };
                if (c.AutoTarget && _sim.GetUnit(c.TargetUnitId) is not { Alive: true })
                {
                    // Automatic target: estimate against the target the rules would pick now (no random draws in planning).
                    var (now, later) = _sim.Targets.Candidates(u, s, pos, facing);
                    var pool = now.Count > 0 ? now : c.Approach ? later : now;
                    var pick = pool.Count == 0 ? null : _sim.Targets.Choose(u, pool, s.AutoTarget.Rules, s.AutoTarget.Mode, false, s, -1, out _, allowRandom: false);
                    if (pick == null) return new CommandEstimate { EndPos = pos, EndFacing = facing, Feasible = false, Problem = "сейчас нет допустимых целей — приказ подождёт" };
                    var probe = c.CloneTemplate();
                    probe.AutoTarget = false;
                    probe.TargetUnitId = pick.Id;
                    probe.TargetCell = pick.Pos;
                    if (s.UsesChosenDirection) probe.Direction = pos.DirectionTo(pick.Pos) ?? facing;
                    var e = Estimate(u, probe, pos, facing);
                    return new CommandEstimate
                    {
                        Ap = e.Ap, Seconds = e.Seconds, EndPos = e.EndPos, EndFacing = e.EndFacing, Path = e.Path, Feasible = e.Feasible,
                        Problem = $"цель сейчас: {pick.Name}" + (e.Problem != "" ? " · " + e.Problem : ""),
                    };
                }
                var target = TargetPosOf(u, s, c, pos);
                var orient = s.UsesChosenDirection ? c.Direction ?? facing : pos.DirectionTo(target) ?? facing;
                int steps = s.FaceTarget && Rules.Turn.AutoFaceTarget ? HexDirectionExt.StepsBetween(facing, orient) : 0;
                var end = steps > 0 || s.FaceTarget ? orient : facing;
                var chk = _sim.Skills.Validate(u, s, c.TargetCell, c.TargetUnitId, c.Direction, pos, end, checkCosts: false, checkState: false);
                if (!chk.Ok && c.Approach && s.Approach && s.AllowMoveBeforeUse && chk.Reason is FailureReason.OutOfRange or FailureReason.NoLineOfSight)
                {
                    // Approach first: the estimate includes the walk to the attack position (docs/DECISIONS_3.md §42).
                    var at = AttackPosition(u, s, c.TargetCell, c.TargetUnitId, pos, out var why);
                    if (at == null)
                        return new CommandEstimate { EndPos = pos, EndFacing = facing, Feasible = false, Problem = chk.Message + " — " + why };
                    var walk = _sim.Pathfinder.Find(_sim.Map, MoveContext.For(_sim, u), pos, at.Value, new PathOptions { IgnoreUnits = pos != u.Pos && pos != (u.StepTo ?? u.Pos) });
                    double speed = Math.Max(0.01, u.Stat(Stats.MoveSpeed));
                    var face = at.Value.DirectionTo(TargetPosOf(u, s, c, at.Value)) ?? facing;
                    return new CommandEstimate
                    {
                        Ap = walk.Cost * Rules.Ap.MovePerCost + s.ApCost,
                        Seconds = walk.StepCosts.Sum(x => Math.Max(_sim.Dt, x / speed)) + s.PrepTime + s.RecoveryTime + (s.HoldDuringSeries ? SkillText.Duration(s) : 0),
                        EndPos = at.Value,
                        EndFacing = s.FaceTarget ? face : facing,
                        Path = walk.Path,
                        Problem = "подойдёт к " + at.Value,
                    };
                }
                return new CommandEstimate
                {
                    Ap = s.ApCost + steps * Rules.Turn.ApPerStep,
                    Seconds = s.PrepTime + s.RecoveryTime + (s.HoldDuringSeries ? SkillText.Duration(s) : 0) + steps * Rules.Turn.SecondsPerStep,
                    EndPos = pos,
                    EndFacing = end,
                    Feasible = chk.Ok,
                    Problem = chk.Message,
                };
            }
            case CommandType.Defend:
                return new CommandEstimate
                {
                    Ap = u.DefenseSkill?.ApCost ?? Rules.Ap.DefendCost,
                    Seconds = c.Duration > 0 ? c.Duration : u.DefenseSkill?.Defense.Duration ?? 0,
                    EndPos = pos, EndFacing = facing,
                };
            case CommandType.Wait:
                return new CommandEstimate { Seconds = c.Duration, EndPos = pos, EndFacing = facing };
        }
        return new CommandEstimate { EndPos = pos, EndFacing = facing };
    }

    // ------------------------------------------------------------------ attack on a target (docs/DECISIONS_3.md §42)

    /// <summary>
    /// The cheapest reachable position from which the skill can be used on the target (melee — a free
    /// neighbouring hex, ranged — in range with line of sight). Null with the reason if there is none.
    /// </summary>
    public HexCoord? AttackPosition(Unit u, SkillDef s, HexCoord? targetCell, int targetUnitId, HexCoord from, out string why)
    {
        why = "";
        var t = _sim.GetUnit(targetUnitId);
        HexCoord? tpos = s.Targeting == TargetingMode.Unit ? (t != null && t.Alive ? t.Pos : null) : targetCell;
        if (tpos == null)
        {
            why = "нет цели";
            return null;
        }
        var reach = _sim.Pathfinder.Flood(_sim.Map, MoveContext.For(_sim, u), from, 80, new PathOptions { MaxExpanded = 8000 });
        int maxR = s.RangeMax + 4;
        HexCoord? best = null;
        double bestCost = double.MaxValue;
        foreach (var (c, cost) in reach)
        {
            if (HexCoord.Distance(c, tpos.Value) > maxR || HexCoord.Distance(c, tpos.Value) < s.RangeMin) continue;
            if (cost > bestCost + 1e-9) continue;
            var face = c.DirectionTo(tpos.Value) ?? u.Facing;
            var chk = _sim.Skills.Validate(u, s, targetCell, targetUnitId, null, c, face, checkCosts: false, checkState: false);
            if (!chk.Ok) continue;
            if (cost < bestCost - 1e-9 || best == null || c.Q < best.Value.Q || (c.Q == best.Value.Q && c.R < best.Value.R))
            {
                best = c;
                bestCost = cost;
            }
        }
        if (best == null) why = "нет доступной позиции в дальности и с линией видимости";
        return best;
    }

    /// <summary>Use of a skill against a chosen target from a planned position: now, after approaching, or never — with the reason.</summary>
    public (bool Now, bool AfterApproach, string Reason) CheckAgainst(Unit u, SkillDef s, Unit target, HexCoord from, HexDirection facing)
    {
        var chk = _sim.Skills.Validate(u, s, target.Pos, target.Id, from.DirectionTo(target.Pos), from, facing, checkCosts: true);
        if (chk.Ok) return (true, false, "");
        // Damage the target is immune to (all damage effects of the skill).
        var dmg = s.Stages.SelectMany(st => st.Effects).Where(e => e.Type == EffectType.Damage).ToList();
        if (dmg.Count > 0 && dmg.All(e => _sim.Damage.IsImmune(target, e.DamageType)))
            return (false, false, $"цель невосприимчива к урону «{_sim.Content.Get<DamageTypeDef>(dmg[0].DamageType)?.DisplayName ?? dmg[0].DamageType}»");
        if (chk.Reason is FailureReason.OutOfRange or FailureReason.NoLineOfSight && s.Approach && s.AllowMoveBeforeUse && !u.Flags.BlocksMovement)
        {
            var pos = AttackPosition(u, s, target.Pos, target.Id, from, out var why);
            if (pos != null) return (false, true, chk.Message + " — подойдёт к " + pos);
            return (false, false, chk.Message + " — " + why);
        }
        if (chk.Reason is FailureReason.OutOfRange or FailureReason.NoLineOfSight && !s.AllowMoveBeforeUse)
            return (false, false, chk.Message + " (навык применяется только с места)");
        return (false, chk.Reason is FailureReason.NotEnoughAp or FailureReason.NotEnoughResource or FailureReason.Cooldown, chk.Message);
    }

    /// <summary>
    /// Strength of a skill for ordering the radial menu (weak → strong): expected damage and healing of one
    /// use with the unit's stats, statuses and areas counted lightly. Not a balance metric.
    /// </summary>
    public double SkillPower(Unit u, SkillDef s)
    {
        double p = 0;
        foreach (var st in s.Stages)
        {
            int cells = st.Area.Shape switch
            {
                AreaShape.Single => 1,
                AreaShape.Chain => 1 + st.Area.ChainJumps,
                AreaShape.Mask => Math.Max(1, st.Area.Cells.Count),
                AreaShape.Global => 10,
                _ => 1 + 2 * st.Area.Size,
            };
            double area = 1 + 0.25 * (cells - 1);
            foreach (var e in st.Effects)
            {
                double amount = e.Amount + (string.IsNullOrEmpty(e.ScalingStat) ? 0 : u.Stat(e.ScalingStat) * e.ScalingFactor);
                p += e.Type switch
                {
                    EffectType.Damage => amount * e.Chance * area,
                    EffectType.Heal => amount * 0.8 * area,
                    EffectType.Shield => amount * 0.5,
                    EffectType.ApplyStatus => 4 * e.Chance * area,
                    EffectType.Push or EffectType.Pull or EffectType.ChangeFacing => 2,
                    EffectType.Summon => 15,
                    _ => 1,
                };
            }
        }
        return p + s.ApCost * 0.01;
    }

    /// <summary>
    /// Skills of the radial menu (docs/DECISIONS_3.md §42): the defensive skill of the centre and the rest in
    /// the order weak → strong (or the manual priority); «second ring» skills and the overflow of the first
    /// ring (capacity) go to the second ring.
    /// </summary>
    public (SkillDef? Defense, List<SkillDef> First, List<SkillDef> Second) RadialSkills(Unit u, int firstCapacity = 8)
    {
        var all = new List<SkillDef>();
        if (u.BasicAttack != null) all.Add(u.BasicAttack);
        foreach (var s in u.Skills)
            if (!all.Contains(s) && s != u.DefenseSkill)
                all.Add(s);
        var ordered = all.Select((s, i) => (s, i)).OrderBy(x => x.s.MenuPriority > 0 ? 0 : 1).ThenBy(x => x.s.MenuPriority)
            .ThenBy(x => x.s.MenuPriority > 0 ? 0 : SkillPower(u, x.s)).ThenBy(x => x.i).Select(x => x.s).ToList();
        var first = new List<SkillDef>();
        var second = new List<SkillDef>();
        foreach (var s in ordered)
        {
            if (s.Ring == MenuRing.Second || (s.Ring == MenuRing.Auto && first.Count >= firstCapacity)) second.Add(s);
            else first.Add(s);
        }
        // Manually placed into the first ring beyond its capacity: the weakest overflow goes outward.
        while (first.Count > firstCapacity)
        {
            second.Insert(0, first[^1]);
            first.RemoveAt(first.Count - 1);
        }
        return (u.DefenseSkill, first, second);
    }

    /// <summary>AP and seconds of a manual turn (free and instant unless the rules say otherwise).</summary>
    public (double Ap, double Seconds) ManualTurnCost(HexDirection from, HexDirection to)
    {
        int steps = HexDirectionExt.StepsBetween(from, to);
        return (Rules.Turn.ManualCostsAp ? steps * Rules.Turn.ApPerStep : 0, Rules.Turn.ManualTakesTime ? steps * Rules.Turn.SecondsPerStep : 0);
    }

    private HexCoord TargetPosOf(Unit u, SkillDef s, Command c, HexCoord pos)
    {
        if (s.Targeting == TargetingMode.Unit)
        {
            var t = _sim.GetUnit(c.TargetUnitId);
            return t?.Pos ?? pos;
        }
        if (s.Targeting == TargetingMode.Cell) return c.TargetCell ?? pos;
        return pos;
    }

    /// <summary>Projection of the whole queue for the UI (positions, AP reserve, waiting estimates).</summary>
    public List<ProjectedCommand> ProjectQueue(Unit u)
    {
        var list = new List<ProjectedCommand>();
        var pos = u.StepTo ?? u.Pos;
        var facing = u.Facing;
        double cumulative = 0;
        double ap = u.Ap;
        double regen = u.Flags.BlocksApRegen ? 0 : u.Stat(Stats.ApRegen);
        double elapsed = 0;
        int i = 0;
        foreach (var c in u.Queue.Items)
        {
            CommandEstimate est;
            if (c.Status == CommandStatus.Pending)
            {
                est = Estimate(u, c, pos, facing);
            }
            else
            {
                // Active command: remaining part.
                var end = c.Type == CommandType.Move && c.TargetCell.HasValue ? c.TargetCell.Value : pos;
                var remainingPath = c.Path != null ? new List<HexCoord>(c.Path) : new List<HexCoord>();
                est = new CommandEstimate
                {
                    Ap = c.RemainingReserve,
                    EndPos = end,
                    EndFacing = c.Type == CommandType.Turn && c.Direction.HasValue ? c.Direction.Value : facing,
                    Path = remainingPath,
                    Seconds = c.Type == CommandType.Move ? remainingPath.Count / Math.Max(0.01, u.Stat(Stats.MoveSpeed)) : 0,
                };
            }
            double need = c.Status == CommandStatus.Pending ? est.Ap : c.RemainingReserve;
            double wait = 0;
            if (ap + 1e-9 < need)
            {
                wait = regen > 0 ? (need - ap) / regen : double.PositiveInfinity;
                if (!double.IsInfinity(wait))
                {
                    ap = need;
                    elapsed += wait;
                }
            }
            double start = elapsed;
            ap -= need;
            cumulative += need;
            if (!double.IsInfinity(wait))
            {
                ap = Math.Min(u.ApMax, ap + regen * est.Seconds);
                elapsed += est.Seconds;
            }
            list.Add(new ProjectedCommand
            {
                Command = c,
                Index = i++,
                StartPos = pos,
                EndPos = est.EndPos,
                StartFacing = facing,
                EndFacing = est.EndFacing,
                Estimate = est,
                CumulativeAp = cumulative,
                RemainingNoRegen = u.Ap - cumulative,
                WaitForApSeconds = wait,
                StartSeconds = start,
            });
            pos = est.EndPos;
            facing = est.EndFacing;
        }
        return list;
    }

    /// <summary>Planned position/facing at the end of the queue (where newly appended commands start).</summary>
    public (HexCoord Pos, HexDirection Facing) EndOfQueue(Unit u)
    {
        var proj = ProjectQueue(u);
        if (proj.Count == 0) return (u.StepTo ?? u.Pos, u.Facing);
        return (proj[^1].EndPos, proj[^1].EndFacing);
    }

    /// <summary>Planned state after the command at index (for inserting actions at an intermediate waypoint).</summary>
    public (HexCoord Pos, HexDirection Facing) StateAfter(Unit u, int index)
    {
        var proj = ProjectQueue(u);
        if (proj.Count == 0 || index < 0) return (u.StepTo ?? u.Pos, u.Facing);
        index = Math.Min(index, proj.Count - 1);
        return (proj[index].EndPos, proj[index].EndFacing);
    }

    /// <summary>Main action menu with availability and reasons (docs: unavailable actions explain why).</summary>
    public List<ActionInfo> Actions(Unit u)
    {
        var list = new List<ActionInfo>();
        string stun = u.Flags.BlocksActions ? "юнит оглушён" : "";
        string control = _sim.CanPlayerCommand(u) ? "" : "юнит не под вашим управлением";
        string block = control != "" ? control : stun;
        double free = u.FreeAp;

        string ApWarn(double cost)
        {
            if (free + 1e-9 >= cost) return "";
            double regen = u.Stat(Stats.ApRegen);
            return regen > 0 ? $"ОД сейчас {Math.Max(0, free):0.#}/{cost:0.#} — команда подождёт ≈{(cost - free) / regen:0.#} с" : "ОД не восстанавливаются";
        }

        list.Add(new ActionInfo("move", "Движение", block == "" && !u.Flags.BlocksMovement, block != "" ? block : u.Flags.BlocksMovement ? "юнит обездвижен" : "",
            free < Rules.Ap.MovePerCost ? ApWarn(Rules.Ap.MovePerCost) : ""));
        list.Add(new ActionInfo("turn", "Поворот", block == "" && !u.Flags.BlocksTurning, block != "" ? block : u.Flags.BlocksTurning ? "юнит не может поворачиваться" : "",
            ApWarn(Rules.Turn.ApPerStep)));
        if (u.BasicAttack != null)
            list.Add(SkillAction(u, u.BasicAttack, "attack", "Атака", block, ApWarn));
        else
            list.Add(new ActionInfo("attack", "Атака", false, "нет обычной атаки"));
        foreach (var s in u.Skills) list.Add(SkillAction(u, s, "skill:" + s.Id, s.DisplayName, block, ApWarn));
        if (u.DefenseSkill is { } ds)
        {
            // The unit's defensive skill: its own costs and cooldown (docs/DECISIONS_3.md §45).
            long cd = u.CooldownRemaining(ds.Id, _sim.Tick);
            string why = block != "" ? block : cd > 0 ? $"перезарядка {cd * _sim.Dt:0.#} с" : "";
            list.Add(new ActionInfo("defend", ds.DisplayName, why == "", why, ApWarn(ds.ApCost), Skill: ds));
        }
        else
            list.Add(new ActionInfo("defend", "Стойка", block == "", block, ApWarn(Rules.Ap.DefendCost)));
        list.Add(new ActionInfo("wait", "Ожидание", control == "", control));
        list.Add(new ActionInfo("queue", "Очередь", control == "" && u.Queue.Count > 0, control != "" ? control : u.Queue.Count == 0 ? "очередь пуста" : ""));
        list.Add(new ActionInfo("info", "Характеристики", true, ""));
        return list;
    }

    private ActionInfo SkillAction(Unit u, SkillDef s, string key, string label, string block, Func<double, string> apWarn)
    {
        if (block != "") return new ActionInfo(key, label, false, block, Skill: s);
        if (_sim.Content.DisabledSkills.TryGetValue(s.Id, out var why)) return new ActionInfo(key, label, false, "ошибка данных: " + why, Skill: s);
        if (u.Flags.BlocksSkills && u.BasicAttack != s) return new ActionInfo(key, label, false, "немота", Skill: s);
        if (u.Flags.BlocksAttacks && s.IsHarmful) return new ActionInfo(key, label, false, "атаки запрещены состоянием", Skill: s);
        if (s.ApCost > u.ApMax + 1e-9) return new ActionInfo(key, label, false, $"стоимость {s.ApCost:0.#} ОД больше максимума", Skill: s);
        var warn = new List<string>();
        foreach (var rc in s.ResourceCosts)
        {
            if (!u.Resources.TryGetValue(rc.Resource, out var r))
                return new ActionInfo(key, label, false, $"нет ресурса «{_sim.Content.Get<ResourceDef>(rc.Resource)?.DisplayName ?? rc.Resource}»", Skill: s);
            if (r.Current + 1e-9 < rc.Amount)
            {
                if (r.Regen <= 0) return new ActionInfo(key, label, false, $"недостаточно: {r.Def.DisplayName} ({r.Current:0.#}/{rc.Amount:0.#})", Skill: s);
                warn.Add($"{r.Def.DisplayName} {r.Current:0.#}/{rc.Amount:0.#} — подождёт восстановления");
            }
        }
        long cd = u.CooldownRemaining(s.Id, _sim.Tick);
        if (cd > 0) warn.Add($"перезарядка {cd * _sim.Dt:0.#} с");
        var apw = apWarn(s.ApCost);
        if (apw != "") warn.Add(apw);
        return new ActionInfo(key, label, true, "", string.Join("; ", warn), s);
    }

    /// <summary>
    /// Area preview of a skill used from a planned position. Shows affected cells, units under
    /// the area (allies flagged), blocked cells, odds and warnings. Not a guarantee — targets move.
    /// </summary>
    public SkillPreview PreviewSkill(Unit u, SkillDef s, HexCoord fromPos, HexDirection facing, HexCoord? targetCell, int targetUnitId, HexDirection? direction)
    {
        var chk = _sim.Skills.Validate(u, s, targetCell, targetUnitId, direction, fromPos, facing, checkCosts: false, checkState: false);
        HexCoord anchor = chk.Anchor;
        HexDirection orient = chk.Orientation;
        if (!chk.Ok && (s.Targeting == TargetingMode.Cell || s.Targeting == TargetingMode.Unit))
        {
            anchor = targetUnitId >= 0 && _sim.GetUnit(targetUnitId) is { } tu ? tu.Pos : targetCell ?? fromPos;
            orient = fromPos.DirectionTo(anchor) ?? facing;
        }
        int bonus = targetCell.HasValue ? _sim.Skills.RangeBonus(s, fromPos, targetCell.Value) : 0;
        var p = new SkillPreview
        {
            Valid = chk.Ok,
            Problem = chk.Message,
            Reason = chk.Reason,
            Anchor = anchor,
            Orientation = orient,
            RangeMin = s.RangeMin,
            RangeMax = s.RangeMax + bonus,
        };
        var seenUnits = new HashSet<int>();
        for (int si = 0; si < s.Stages.Count; si++)
        {
            var st = s.Stages[si];
            var raw = st.Area.Shape == AreaShape.Chain ? new List<AreaCell> { new(anchor, null, 0) } : AreaResolver.Resolve(st.Area, anchor, orient, _sim.Map);
            var kept = _sim.Skills.StageCells(u, s, st, anchor, orient, targetUnitId, preview: true).Select(c => c.Cell).ToHashSet();
            foreach (var ac in raw)
            {
                bool blocked = !kept.Contains(ac.Cell);
                p.Cells.Add(new SkillPreviewCell { Cell = ac.Cell, Blocked = blocked, StageIndex = si, HasMaskEffects = ac.Mask?.Effects.Count > 0 });
                if (blocked) continue;
                var cell = _sim.Map.Get(ac.Cell);
                if (cell == null) continue;
                foreach (var o in cell.Occupants)
                {
                    if (!o.Alive || !seenUnits.Add(o.Id)) continue;
                    if (o != u && !_sim.Vision.CanFactionSee(u.FactionId, o)) continue;
                    var effects = st.Effects.Concat(ac.Mask?.Effects ?? Enumerable.Empty<SkillEffectDef>()).Where(e => e.TargetsUnits && _sim.FilterAccepts(e.Filter, u, o)).ToList();
                    if (effects.Count == 0) continue;
                    bool harmful = effects.Any(e => e.Type == EffectType.Damage || e.RequiresHit);
                    var rel = _sim.RelationOf(u, o);
                    if (rel == Relation.Ally)
                    {
                        p.Allies.Add(o);
                        if (harmful && o != u) p.Warnings.Add($"задевает союзника: {o.Name}");
                        if (harmful && o == u) p.Warnings.Add("задевает самого исполнителя");
                    }
                    else
                    {
                        p.Enemies.Add(o);
                    }
                    if (harmful && o != u)
                    {
                        double notice = _sim.Combat.NoticeChance(u, o, fromPos, out var sector, out var sees);
                        double surprise = _sim.Combat.SurpriseChance(sector, sees, o);
                        double hit = s.HitMode == HitMode.AlwaysHit ? 1.0 : _sim.Combat.HitChance(u, o, s, fromPos, true, false);
                        p.Odds.Add((o, hit, notice, surprise, sector));
                    }
                }
            }
        }
        if (s.ResourceCosts.Count > 0)
            foreach (var rc in s.ResourceCosts)
                if (u.Resources.TryGetValue(rc.Resource, out var r) && r.Current < rc.Amount)
                    p.Warnings.Add($"{r.Def.DisplayName}: {r.Current:0.#}/{rc.Amount:0.#}");
        if (p.Cells.Any(c => c.Blocked)) p.Warnings.Add("часть области закрыта препятствиями");
        return p;
    }

    /// <summary>Cells reachable with the given AP budget from a position (move range highlight).</summary>
    public Dictionary<HexCoord, double> ReachableWithAp(Unit u, HexCoord from, double ap)
    {
        double maxCost = Rules.Ap.MovePerCost > 0 ? ap / Rules.Ap.MovePerCost : 1000;
        return _sim.Pathfinder.Flood(_sim.Map, MoveContext.For(_sim, u), from, maxCost, new PathOptions());
    }
}
