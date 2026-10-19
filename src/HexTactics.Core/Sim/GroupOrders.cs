using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>Group commands of the bottom panel (docs/DECISIONS_3.md §44).</summary>
public enum GroupCommand
{
    [Ed("Двигаться к точке")] Move,
    [Ed("Атаковать цель")] Attack,
    [Ed("Навык по цели или гексу")] Skill,
    [Ed("Оборонительная позиция")] DefendPosition,
    [Ed("Удерживать позицию")] HoldPosition,
    [Ed("Следовать за юнитом")] Follow,
    [Ed("Повернуться")] Face,
    [Ed("Рассредоточиться")] Disperse,
    [Ed("Собраться в области")] Gather,
    [Ed("Окружить цель")] Surround,
    [Ed("Остановиться после текущего действия")] StopAfterCurrent,
    [Ed("Отменить очередь")] CancelQueue,
    [Ed("Очистить очередь")] ClearQueue,
    [Ed("Приоритет автоатаки")] AutoAttackPriority,
}

/// <summary>A group order with its parameters (docs/DECISIONS_3.md §44).</summary>
public sealed class GroupOrder
{
    public GroupCommand Command { get; set; }
    public HexCoord? Point { get; set; }
    public int TargetUnitId { get; set; } = -1;
    public string SkillId { get; set; } = "";
    /// <summary>Common facing at the end (null — the formation's or none).</summary>
    public HexDirection? Facing { get; set; }
    public bool IndividualFacing { get; set; }
    public string Formation { get; set; } = "";
    public GroupQueueMode QueueMode { get; set; } = GroupQueueMode.Replace;
    /// <summary>Keep the units' own orders: the group order goes after them.</summary>
    public bool KeepIndividual { get; set; }
    public List<string> ExcludeTags { get; set; } = new();
    /// <summary>Casters may pick their most effective skill instead of the basic attack.</summary>
    public bool AllowAutoSkills { get; set; } = true;
    public TargetDeathBehavior OnTargetDeath { get; set; } = TargetDeathBehavior.NextTarget;
    /// <summary>Maximal distance of the end hexes from the point (0 — no limit).</summary>
    public int MaxSpread { get; set; }
    public NoSpaceBehavior WhenNoSpace { get; set; } = NoSpaceBehavior.Partial;
    public RouteMode? Routing { get; set; }
    public AutoTargetRule AutoPriority { get; set; } = AutoTargetRule.Nearest;
}

public sealed class GroupOrderResult
{
    public List<int> Ordered { get; } = new();
    /// <summary>Units left out with the reason (excluded by tag, no place, cannot do it…).</summary>
    public List<(int Unit, string Reason)> Skipped { get; } = new();
    public GroupAssignment? Assignment { get; set; }
    public bool Partial => Skipped.Count > 0;
    public string Message { get; set; } = "";
}

/// <summary>Issues group orders as ordinary commands of each unit (docs/DECISIONS_3.md §44).</summary>
public sealed class GroupOrders
{
    private readonly Simulation _sim;

    public GroupOrders(Simulation sim)
    {
        _sim = sim;
    }

    public GroupPlanner Planner => _sim.Groups;

    private static bool Excluded(Unit u, GroupOrder o) => o.ExcludeTags.Any(t => u.Tag == t || u.Def.Tags.Contains(t));

    /// <summary>The skill a unit uses in a group attack: the most effective usable skill of a caster (if allowed), otherwise the basic attack.</summary>
    public SkillDef? AttackSkillOf(Unit u, Unit target, GroupOrder o)
    {
        if (!string.IsNullOrEmpty(o.SkillId) && u.KnowsSkill(o.SkillId)) return _sim.Content.Skill(o.SkillId);
        var basic = u.BasicAttack;
        if (!o.AllowAutoSkills || Planner.RoleOf(u) != UnitRole.Caster) return basic;
        SkillDef? best = basic;
        double bestDmg = basic == null ? -1 : _sim.Targets.ExpectedDamage(u, target, basic);
        foreach (var s in u.Skills)
        {
            if (!s.IsHarmful || s.Targeting is TargetingMode.Self) continue;
            if (_sim.Content.DisabledSkills.ContainsKey(s.Id)) continue;
            if (s.ResourceCosts.Any(rc => u.ResourceValue(rc.Resource) + 1e-9 < rc.Amount) || u.CooldownRemaining(s.Id, _sim.Tick) > 0) continue;
            double d = _sim.Targets.ExpectedDamage(u, target, s);
            if (d > bestDmg + 1e-9)
            {
                bestDmg = d;
                best = s;
            }
        }
        return best;
    }

    private int InsertIndex(Unit u, GroupOrder o) => o.KeepIndividual || o.QueueMode == GroupQueueMode.Append ? -1
        : o.QueueMode == GroupQueueMode.AfterCurrent ? (u.Queue.Current is { Status: not CommandStatus.Pending } ? 1 : 0)
        : -1;

    private bool Replace(GroupOrder o) => !o.KeepIndividual && o.QueueMode == GroupQueueMode.Replace;

    private string? Enqueue(Unit u, Command c, GroupOrder o, bool first)
    {
        if (first && Replace(o)) return _sim.Enqueue(u, c, -1, replaceQueue: true);
        if (first && o.QueueMode == GroupQueueMode.AfterCurrent && !o.KeepIndividual)
        {
            // After the current action: the pending orders give way to the group order.
            _sim.ClearPending(u, "групповой приказ");
            return _sim.Enqueue(u, c, -1);
        }
        return _sim.Enqueue(u, c, -1);
    }

    private static FailurePolicyDef GroupMovePolicy() => new() { Action = FailureAction.Repath, WaitSeconds = 0.5, MaxAttempts = 8, Then = FailureAction.Skip };

    private Command MoveCmd(HexCoord to, GroupOrder o, HexDirection? facing, bool reserve)
    {
        var c = Command.Move(to);
        c.Routing = o.Routing;
        c.ArrivalFacing = facing;
        c.PolicyOverride = GroupMovePolicy();
        c.ReservesDestination = reserve;
        return c;
    }

    private TargetLostPolicy LostPolicy(GroupOrder o) => o.OnTargetDeath switch
    {
        TargetDeathBehavior.Stop => TargetLostPolicy.Cancel,
        TargetDeathBehavior.ContinueToPoint => TargetLostPolicy.Cancel,
        _ => TargetLostPolicy.NewTarget,
    };

    /// <summary>Gives a group order to the units: each gets its own commands; the assignment and the left-out units are reported.</summary>
    public GroupOrderResult Issue(IReadOnlyList<Unit> all, GroupOrder o)
    {
        var res = new GroupOrderResult();
        var units = new List<Unit>();
        foreach (var u in all.Where(x => x.Alive).Distinct().OrderBy(x => x.Id))
        {
            if (Excluded(u, o)) res.Skipped.Add((u.Id, "исключён по тегу"));
            else units.Add(u);
        }
        if (units.Count == 0)
        {
            res.Message = "в группе нет юнитов для приказа";
            return res;
        }
        var target = _sim.GetUnit(o.TargetUnitId);
        var formation = string.IsNullOrEmpty(o.Formation) ? null : _sim.Content.Get<FormationDef>(o.Formation);
        switch (o.Command)
        {
            case GroupCommand.Move:
            case GroupCommand.Gather:
            case GroupCommand.DefendPosition:
            {
                if (o.Point is not { } point) return Fail(res, "не указана точка");
                if (o.Command == GroupCommand.Gather && formation == null) formation = _sim.Content.Get<FormationDef>("compact");
                var a = Planner.AssignMove(units, point, formation, o.MaxSpread);
                res.Assignment = a;
                foreach (var u in units)
                {
                    if (!a.Cells.TryGetValue(u.Id, out var cell))
                    {
                        if (o.WhenNoSpace == NoSpaceBehavior.Wait)
                        {
                            // Waits near the point until a place frees up (bounded by the queue's maximal wait).
                            var w = MoveCmd(point, o, null, false);
                            w.Optional = true;
                            w.PolicyOverride = new FailurePolicyDef { Action = FailureAction.Wait, WaitSeconds = _sim.Rules.Queue.MaxWaitSeconds, Then = FailureAction.Skip };
                            Add(res, u, Enqueue(u, w, o, true));
                        }
                        else
                        {
                            res.Skipped.Add((u.Id, "нет свободного места"));
                        }
                        continue;
                    }
                    HexDirection? face = o.Facing ?? (o.IndividualFacing ? null : formation != null && (formation.FaceOutward || formation.Kind == FormationKind.Perimeter) ? a.Facings[u.Id] : null);
                    var err = Enqueue(u, MoveCmd(cell, o, face, true), o, true);
                    if (err == null)
                    {
                        var c = _sim.Map.Get(cell);
                        if (c != null) c.DestinationOf = u.Id;
                        if (o.Command == GroupCommand.DefendPosition) _sim.Enqueue(u, Command.Defend());
                    }
                    Add(res, u, err);
                }
                break;
            }
            case GroupCommand.Attack:
            case GroupCommand.Skill:
            case GroupCommand.Surround:
            {
                if (target == null || !target.Alive)
                {
                    if (o.Command == GroupCommand.Skill && o.Point is { } cellTarget) return SkillAtCell(res, units, o, cellTarget);
                    return Fail(res, "не указана цель");
                }
                var fighters = units.Where(u => Planner.RoleOf(u) != UnitRole.Support || o.Command == GroupCommand.Surround).ToList();
                var support = units.Except(fighters).ToList();
                GroupAssignment a = o.Command == GroupCommand.Surround
                    ? Planner.AssignMove(fighters, target.Pos, new FormationDef { Kind = FormationKind.Surround, Radius = 1 }, 0, target.Pos)
                    : Planner.AssignAttack(fighters, target, u => AttackSkillOf(u, target, o));
                res.Assignment = a;
                foreach (var u in fighters)
                {
                    var s = AttackSkillOf(u, target, o);
                    if (s == null)
                    {
                        res.Skipped.Add((u.Id, "нет атаки"));
                        continue;
                    }
                    bool first = true;
                    string? err = null;
                    if (a.Cells.TryGetValue(u.Id, out var at) && at != u.Pos)
                    {
                        err = Enqueue(u, MoveCmd(at, o, a.Facings.GetValueOrDefault(u.Id, u.Facing), true), o, true);
                        if (err == null && _sim.Map.Get(at) is { } c) c.DestinationOf = u.Id;
                        first = false;
                    }
                    var atk = s == u.BasicAttack && s.Targeting == TargetingMode.Unit ? Command.Attack(target.Id)
                        : s.Targeting switch
                        {
                            TargetingMode.Unit => Command.Skill(s.Id, null, target.Id),
                            TargetingMode.Cell => Command.Skill(s.Id, target.Pos),
                            _ => Command.Skill(s.Id, null, -1, (a.Cells.GetValueOrDefault(u.Id, u.Pos)).DirectionTo(target.Pos) ?? u.Facing),
                        };
                    atk.Approach = true;
                    atk.OnTargetLost = LostPolicy(o);
                    if (err == null) err = Enqueue(u, atk, o, first);
                    if (err == null && o.OnTargetDeath == TargetDeathBehavior.ContinueToPoint) _sim.Enqueue(u, MoveCmd(o.Point ?? target.Pos, o, null, false));
                    Add(res, u, err);
                }
                // Support does not rush into melee: it keeps its place behind the fighters.
                foreach (var u in support) res.Skipped.Add((u.Id, "поддержка не идёт в ближний бой — остаётся на месте"));
                break;
            }
            case GroupCommand.HoldPosition:
                foreach (var u in units)
                {
                    _sim.ClearQueue(u, "удерживать позицию");
                    Add(res, u, _sim.Enqueue(u, Command.Defend()));
                }
                break;
            case GroupCommand.Follow:
                if (target == null || !target.Alive) return Fail(res, "не указан ведущий");
                foreach (var u in units)
                {
                    if (u == target) continue;
                    Add(res, u, Enqueue(u, Command.Follow(target.Id), o, true));
                }
                break;
            case GroupCommand.Face:
                if (o.Facing is not { } dir) return Fail(res, "не указано направление");
                foreach (var u in units) Add(res, u, Enqueue(u, Command.Turn(dir), o, true));
                break;
            case GroupCommand.Disperse:
            {
                var a = Planner.AssignDisperse(units);
                res.Assignment = a;
                foreach (var u in units)
                {
                    if (!a.Cells.TryGetValue(u.Id, out var cell))
                    {
                        res.Skipped.Add((u.Id, "нет места для рассредоточения"));
                        continue;
                    }
                    if (cell == u.Pos) res.Ordered.Add(u.Id);
                    else Add(res, u, Enqueue(u, MoveCmd(cell, o, null, true), o, true));
                }
                break;
            }
            case GroupCommand.StopAfterCurrent:
            case GroupCommand.ClearQueue:
                foreach (var u in units)
                {
                    _sim.ClearPending(u, o.Command == GroupCommand.StopAfterCurrent ? "остановиться после текущего действия" : "очистка очереди");
                    res.Ordered.Add(u.Id);
                }
                break;
            case GroupCommand.CancelQueue:
                foreach (var u in units)
                {
                    _sim.ClearQueue(u, "групповая отмена");
                    res.Ordered.Add(u.Id);
                }
                break;
            case GroupCommand.AutoAttackPriority:
                foreach (var u in units)
                {
                    if (u.Def.AutoAttacks.Count == 0)
                    {
                        res.Skipped.Add((u.Id, "нет автоатак"));
                        continue;
                    }
                    u.AutoAttackPriority = o.AutoPriority;
                    res.Ordered.Add(u.Id);
                }
                break;
        }
        int n = res.Ordered.Count;
        res.Message = res.Partial
            ? $"Приказ выполнен частично: {n} из {units.Count + res.Skipped.Count(s => s.Reason == "исключён по тегу")}" + (res.Assignment?.Note is { Length: > 0 } note ? $" ({note})" : "")
            : $"Приказ отдан: {n}";
        _sim.Emit(SimEventType.Info, LogLevel.Detail, $"Групповой приказ «{o.Command}»: {res.Message}");
        return res;
    }

    private GroupOrderResult SkillAtCell(GroupOrderResult res, List<Unit> units, GroupOrder o, HexCoord cell)
    {
        foreach (var u in units)
        {
            var s = _sim.Content.Skill(o.SkillId);
            if (s == null || !u.KnowsSkill(s.Id) || s.Targeting != TargetingMode.Cell)
            {
                res.Skipped.Add((u.Id, "навык не поддерживается"));
                continue;
            }
            var c = Command.Skill(s.Id, cell);
            c.Approach = true;
            Add(res, u, Enqueue(u, c, o, true));
        }
        res.Message = res.Partial ? $"Навык по гексу: {res.Ordered.Count} юнитов, остальные не умеют" : $"Навык по гексу: {res.Ordered.Count}";
        return res;
    }

    private static void Add(GroupOrderResult res, Unit u, string? err)
    {
        if (err == null) res.Ordered.Add(u.Id);
        else res.Skipped.Add((u.Id, err));
    }

    private static GroupOrderResult Fail(GroupOrderResult res, string why)
    {
        res.Message = why;
        return res;
    }
}
