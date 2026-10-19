using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>
/// Executes unit command queues: gradual movement, turning, skills (prep → release →
/// recovery), stance and waiting; applies failure policies (wait / repath / skip / cancel)
/// with bounded waiting. AP is reserved at enqueue and spent when an action starts.
/// </summary>
public sealed class CommandSystem
{
    private readonly Simulation _sim;

    public CommandSystem(Simulation sim)
    {
        _sim = sim;
    }

    private RulesDef Rules => _sim.Rules;

    private enum Result
    {
        Continue,
        Done,
        Fail,
    }

    private FailureReason _failReason;
    private string _failMessage = "";

    private Result Fail(FailureReason reason, string message)
    {
        _failReason = reason;
        _failMessage = message;
        return Result.Fail;
    }

    // ------------------------------------------------------------------ queue API

    public string? Enqueue(Unit u, Command c, int insertIndex, bool replaceQueue)
    {
        if (!u.Alive) return "юнит погиб";
        var err = ValidateTemplate(u, c);
        if (err != null) return err;
        if (replaceQueue) ClearQueue(u, "заменено новой командой", CommandStatus.Cancelled);
        // Where the target was seen when the order was given (policy «last known position»).
        if (_sim.GetUnit(c.TargetUnitId) is { Alive: true } seen && _sim.Vision.CanFactionSee(u.FactionId, seen)) c.LastKnownTarget = seen.Pos;
        c.Id = _sim.NextCommandId();
        c.Status = CommandStatus.Pending;
        int idx = insertIndex < 0 ? u.Queue.Count : insertIndex;
        // Never insert before the active command.
        if (u.Queue.Current != null && u.Queue.Current.Status != CommandStatus.Pending && idx == 0) idx = 1;
        u.Queue.Insert(idx, c);
        RecomputeEstimates(u);
        if (c.EstimatedAp > u.ApMax + 1e-9 && c.Type != CommandType.Move)
        {
            u.Queue.Remove(c);
            RecomputeEstimates(u);
            return $"стоимость {c.EstimatedAp:0.#} ОД больше максимума {u.ApMax:0.#}";
        }
        _sim.Emit(SimEventType.Info, LogLevel.Diagnostic, $"{u.Name}: в очередь — {c.Describe(_sim)} (≈{c.EstimatedAp:0.#} ОД)", u.Id);
        return null;
    }

    private string? ValidateTemplate(Unit u, Command c)
    {
        switch (c.Type)
        {
            case CommandType.Move:
                if (!c.TargetCell.HasValue) return "не указана клетка";
                if (!_sim.Map.Contains(c.TargetCell.Value)) return "клетки нет на карте";
                return null;
            case CommandType.Turn:
                return c.Direction.HasValue ? null : "не указано направление";
            case CommandType.Attack:
                if (u.BasicAttack == null) return "у юнита нет обычной атаки";
                if (_sim.Content.DisabledSkills.ContainsKey(u.BasicAttack.Id)) return "обычная атака отключена из-за ошибки данных";
                c.SkillId = u.BasicAttack.Id;
                if (_sim.GetUnit(c.TargetUnitId) == null && !c.AutoTarget) return "не указана цель";
                return null;
            case CommandType.Skill:
            {
                var s = _sim.Content.Skill(c.SkillId);
                if (s == null || !u.KnowsSkill(c.SkillId)) return "юнит не знает этот навык";
                if (_sim.Content.DisabledSkills.TryGetValue(s.Id, out var why)) return "навык отключён: " + why;
                if (s.Targeting == TargetingMode.Unit && _sim.GetUnit(c.TargetUnitId) == null && !c.AutoTarget) return "не указана цель";
                if (s.Targeting == TargetingMode.Cell && (!c.TargetCell.HasValue || !_sim.Map.Contains(c.TargetCell.Value)) && !c.AutoTarget) return "не указана клетка";
                if (s.ApCost > u.ApMax + 1e-9) return $"стоимость {s.ApCost:0.#} ОД больше максимума {u.ApMax:0.#}";
                foreach (var rc in s.ResourceCosts)
                {
                    if (!u.Resources.TryGetValue(rc.Resource, out var r)) return $"нет ресурса «{rc.Resource}»";
                    if (rc.Amount > r.Max + 1e-9) return $"стоимость больше максимума ресурса «{r.Def.DisplayName}»";
                }
                return null;
            }
            case CommandType.Wait:
                if (c.Duration <= 0) return "длительность ожидания должна быть больше 0";
                c.Duration = Math.Min(c.Duration, Rules.Queue.MaxWaitSeconds);
                return null;
            case CommandType.Defend:
                if (Rules.Ap.DefendCost > u.ApMax + 1e-9) return "стойка дороже максимума ОД";
                return null;
        }
        return "неизвестная команда";
    }

    public bool Remove(Unit u, int commandId)
    {
        var c = u.Queue.Find(commandId);
        if (c == null) return false;
        if (c.Status != CommandStatus.Pending) CleanupActive(u, c, refundCast: true);
        c.Status = CommandStatus.Cancelled;
        c.StatusText = "удалено";
        u.Queue.Remove(c);
        RecomputeEstimates(u);
        return true;
    }

    public bool Reorder(Unit u, int commandId, int newIndex)
    {
        int idx = u.Queue.IndexOf(commandId);
        if (idx < 0) return false;
        var c = u.Queue.Items[idx];
        if (c.Status != CommandStatus.Pending) return false;
        int min = u.Queue.Current != null && u.Queue.Current.Status != CommandStatus.Pending ? 1 : 0;
        newIndex = Math.Clamp(newIndex, min, u.Queue.Count - 1);
        if (newIndex == idx) return false;
        u.Queue.Move(idx, newIndex);
        RecomputeEstimates(u);
        return true;
    }

    public void ClearQueue(Unit u, string reason, CommandStatus status)
    {
        if (u.Queue.Count == 0) return;
        var cur = u.Queue.Current;
        if (cur != null && cur.Status != CommandStatus.Pending) CleanupActive(u, cur, refundCast: status == CommandStatus.Cancelled && u.Alive);
        int n = u.Queue.Count;
        foreach (var c in u.Queue.Items)
        {
            c.Status = status;
            c.StatusText = reason;
        }
        u.Queue.Clear();
        if (u.Alive)
            _sim.Emit(SimEventType.QueueCancelled, LogLevel.Detail, $"{u.Name}: очередь очищена ({n}) — {reason}", u.Id);
    }

    /// <summary>Sets the facing after arrival of a route node (Move) that has not turned yet. False — not possible.</summary>
    public bool SetArrivalFacing(Unit u, int commandId, HexDirection? dir)
    {
        var c = u.Queue.Find(commandId);
        if (c == null || c.Type != CommandType.Move || c.IsFinished || c.NodePhase > 1) return false;
        c.ArrivalFacing = dir;
        RecomputeEstimates(u);
        return true;
    }

    /// <summary>Removes pending (not started) commands; the active one is kept.</summary>
    public int ClearPending(Unit u, string reason)
    {
        int n = 0;
        for (int i = u.Queue.Count - 1; i >= 0; i--)
        {
            var c = u.Queue.Items[i];
            if (c.Status != CommandStatus.Pending) continue;
            c.Status = CommandStatus.Cancelled;
            c.StatusText = reason;
            u.Queue.RemoveAt(i);
            n++;
        }
        if (n > 0)
        {
            RecomputeEstimates(u);
            _sim.Emit(SimEventType.QueueCancelled, LogLevel.Detail, $"{u.Name}: ожидающие приказы удалены ({n}) — {reason}", u.Id);
        }
        return n;
    }

    private void CleanupActive(Unit u, Command c, bool refundCast)
    {
        switch (c.Type)
        {
            case CommandType.Move:
                c.Path = null;
                break;
            case CommandType.Attack:
            case CommandType.Skill:
                if (u.Casting != null && !u.Casting.Released)
                    _sim.Skills.Interrupt(u, refundCast ? InterruptCause.Cancelled : InterruptCause.Death, "отмена");
                break;
            case CommandType.Defend:
                if (u.Defending)
                {
                    u.Defending = false;
                    _sim.Statuses.RecalculateModifiers(u);
                }
                break;
        }
    }

    /// <summary>Recomputes reserved AP estimates of pending commands along the planned route.</summary>
    public void RecomputeEstimates(Unit u)
    {
        var pos = u.StepTo ?? u.Pos;
        var facing = u.Facing;
        foreach (var c in u.Queue.Items)
        {
            if (c.Status == CommandStatus.Pending)
            {
                var est = _sim.Planner.Estimate(u, c, pos, facing);
                c.EstimatedAp = est.Ap;
                if (c.Type == CommandType.Move)
                {
                    c.PlannedPath = est.Feasible ? est.Path : null;
                    c.PlannedFrom = pos;
                    c.PlannedTick = _sim.Tick;
                }
                pos = est.EndPos;
                facing = est.EndFacing;
            }
            else
            {
                if (c.Type == CommandType.Move && c.Path != null)
                {
                    double rest = 0;
                    var prev = _sim.Map.Get(u.StepTo ?? u.Pos);
                    foreach (var p in c.Path)
                    {
                        var cell = _sim.Map.Get(p);
                        if (cell == null) break;
                        rest += MovementRules.EntryCost(MoveContext.For(_sim, u), prev, cell) * Rules.Ap.MovePerCost;
                        prev = cell;
                    }
                    c.EstimatedAp = c.SpentAp + rest;
                    if (c.TargetCell.HasValue) pos = c.TargetCell.Value;
                }
                else if (c.Type == CommandType.Move && c.TargetCell.HasValue)
                {
                    pos = c.TargetCell.Value;
                }
                if (c.Type == CommandType.Move && c.ArrivalFacing.HasValue) facing = c.ArrivalFacing.Value;
                if (c.Type == CommandType.Turn && c.Direction.HasValue) facing = c.Direction.Value;
            }
        }
    }

    // ------------------------------------------------------------------ hooks from other systems

    /// <summary>Called before a unit is moved by force: cancels the step in progress (its AP is refunded).</summary>
    public void OnForcedMove(Unit u)
    {
        if (u.StepTo.HasValue)
        {
            _sim.Map.Get(u.StepTo.Value)?.Reservations.Remove(u);
            u.StepTo = null;
            u.StepFrom = null;
            if (_stepAp.TryGetValue(u.Id, out var ap) && ap > 0)
            {
                u.Ap = Math.Min(u.ApMax, u.Ap + ap);
                var cur = u.Queue.Current;
                if (cur != null && cur.Type == CommandType.Move) cur.SpentAp = Math.Max(0, cur.SpentAp - ap);
            }
            _stepAp.Remove(u.Id);
        }
        var active = u.Queue.Current;
        if (active != null && active.Type == CommandType.Move) active.Path = null;
    }

    public void OnCastInterrupted(Unit u, string reason)
    {
        var c = u.Queue.Current;
        if (c == null || !c.IsAttackLike || c.Status == CommandStatus.Pending) return;
        Resolve(u, c, FailureAction.Skip, "прервано: " + reason, FailureReason.None);
    }

    private readonly Dictionary<int, double> _stepAp = new();

    // ------------------------------------------------------------------ tick

    public void Update()
    {
        var units = _sim.Units;
        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            if (!u.Alive) continue;

            if (u.StepTo.HasValue)
            {
                u.StepTicksDone++;
                if (u.StepTicksDone < u.StepTicksTotal) continue;
                CompleteStep(u);
                if (!u.Alive) continue;
            }

            if (u.BusyUntil > _sim.Tick) continue;
            // An auto-attack in preparation holds the queue until it is released (docs/DECISIONS_2.md §28).
            if (u.Casting != null && !u.Casting.Released && u.Casting.Source == DamageSource.AutoAttack) continue;
            // Several instantaneous commands may complete in one tick; cap the loop for safety.
            for (int guard = 0; guard < 8; guard++)
            {
                var cmd = u.Queue.Current;
                if (cmd == null || !u.Alive) break;
                bool finished = Process(u, cmd);
                if (!finished || u.StepTo.HasValue || u.BusyUntil > _sim.Tick) break;
            }
        }
    }

    private void CompleteStep(Unit u)
    {
        var to = u.StepTo!.Value;
        u.StepTo = null;
        u.StepFrom = null;
        _stepAp.Remove(u.Id);
        var from = u.Pos;
        _sim.Emit(SimEventType.Moved, LogLevel.Diagnostic, $"{u.Name}: {from} → {to}", u.Id, cell: to);
        if (!_sim.Relocate(u, to, null, "шаг")) return;
        var cell = _sim.Map.Get(to);
        if (cell != null) _sim.CellEffects.OnUnitEntered(u, cell);
    }

    /// <summary>Processes the current command; returns true if it finished (done/skipped/failed) this call.</summary>
    private bool Process(Unit u, Command cmd)
    {
        if (cmd.Status == CommandStatus.Pending)
        {
            cmd.Status = CommandStatus.Active;
            cmd.StartedTick = _sim.Tick;
            cmd.Phase = 0;
            // Reuse the path planned this very tick from the current position; otherwise recompute.
            cmd.Path = cmd.Type == CommandType.Move && cmd.PlannedPath != null && cmd.PlannedTick == _sim.Tick && cmd.PlannedFrom == u.Pos
                ? new List<HexCoord>(cmd.PlannedPath)
                : null;
            _sim.Emit(SimEventType.CommandStarted, LogLevel.Diagnostic, $"{u.Name}: начинает — {cmd.Describe(_sim)}", u.Id);
        }
        Result r = cmd.Type switch
        {
            CommandType.Move => ExecMove(u, cmd),
            CommandType.Turn => ExecTurn(u, cmd),
            CommandType.Attack => ExecSkill(u, cmd),
            CommandType.Skill => ExecSkill(u, cmd),
            CommandType.Defend => ExecDefend(u, cmd),
            CommandType.Wait => ExecWait(u, cmd),
            _ => Result.Done,
        };
        if (r == Result.Continue) return false;
        if (r == Result.Done)
        {
            Complete(u, cmd);
            return true;
        }
        return HandleFailure(u, cmd, _failReason, _failMessage);
    }

    private void ClearWait(Command cmd)
    {
        if (cmd.WaitStartTick >= 0)
        {
            cmd.WaitStartTick = -1;
            cmd.WaitReason = FailureReason.None;
            cmd.Status = CommandStatus.Active;
            cmd.StatusText = "";
        }
    }

    private void Complete(Unit u, Command cmd)
    {
        cmd.Status = CommandStatus.Done;
        cmd.StatusText = "выполнено";
        u.Queue.Remove(cmd);
        _sim.Emit(SimEventType.CommandCompleted, LogLevel.Diagnostic, $"{u.Name}: выполнено — {cmd.Describe(_sim)}", u.Id);
        RecomputeEstimates(u);
        if (u.Queue.Count == 0 && Rules.Autopause.QueueEmpty && _sim.IsPlayerControlled(u))
            _sim.RequestAutopause($"{u.Name}: очередь выполнена");
    }

    /// <summary>Applies the failure policy. Returns true if the command left the queue.</summary>
    private bool HandleFailure(Unit u, Command cmd, FailureReason reason, string message)
    {
        // A non-mandatory route node: close enough counts as reached, unreachable is skipped (docs/DECISIONS_3.md §41).
        if (cmd.Type == CommandType.Move && cmd.Optional && reason is FailureReason.NoPath or FailureReason.PathBlocked && cmd.TargetCell.HasValue)
        {
            if (HexCoord.Distance(u.Pos, cmd.TargetCell.Value) <= 1)
            {
                cmd.NodePhase = Math.Max(cmd.NodePhase, 1);
                cmd.Path = null;
                ClearWait(cmd);
                _sim.Emit(SimEventType.Info, LogLevel.Detail, $"{u.Name}: необязательная точка {cmd.TargetCell} занята — достаточно рядом", u.Id);
                return false;
            }
            return Resolve(u, cmd, FailureAction.Skip, "необязательная точка пропущена: " + message, reason);
        }
        var policy = cmd.PolicyOverride ?? Rules.Queue.For(reason);
        double maxWait = Math.Min(policy.WaitSeconds, Rules.Queue.MaxWaitSeconds);
        // Hard cap on the total waiting time of one command, whatever the reasons were.
        if (++cmd.TotalWaitTicks > _sim.TicksFor(Rules.Queue.MaxWaitSeconds))
            return Resolve(u, cmd, FailureAction.Skip, $"превышено максимальное ожидание ({Rules.Queue.MaxWaitSeconds:0} с): {message}", reason);
        switch (policy.Action)
        {
            case FailureAction.Wait:
            case FailureAction.Repath:
            {
                if (cmd.WaitStartTick < 0 || cmd.WaitReason != reason)
                {
                    cmd.WaitStartTick = _sim.Tick;
                    cmd.WaitReason = reason;
                    cmd.Status = CommandStatus.Waiting;
                    cmd.StatusText = "ожидание: " + message;
                    _sim.Emit(SimEventType.CommandWaiting, LogLevel.Detail, $"{u.Name}: ожидание — {message}", u.Id);
                    if (maxWait > 0) return false;
                }
                if (_sim.Tick - cmd.WaitStartTick < _sim.TicksFor(maxWait)) return false;

                if (policy.Action == FailureAction.Repath && cmd.Type == CommandType.Move)
                {
                    if (cmd.RepathAttempts < policy.MaxAttempts)
                    {
                        cmd.RepathAttempts++;
                        var path = _sim.Planner.Route(u, cmd, u.Pos, u.Facing, cmd.TargetCell!.Value);
                        if (path.Found)
                        {
                            cmd.Path = path.Path;
                            ClearWait(cmd);
                            _sim.Emit(SimEventType.Repath, LogLevel.Detail, $"{u.Name}: путь пересчитан (попытка {cmd.RepathAttempts})", u.Id);
                            RecomputeEstimates(u);
                            return false;
                        }
                        return Resolve(u, cmd, policy.Then, "путь заблокирован, обхода нет: " + path.Reason, FailureReason.NoPath);
                    }
                    return Resolve(u, cmd, policy.Then, $"путь заблокирован ({cmd.RepathAttempts} пересчётов)", reason);
                }
                return Resolve(u, cmd, policy.Then, "тайм-аут ожидания: " + message, reason);
            }
            default:
                return Resolve(u, cmd, policy.Action, message, reason);
        }
    }

    private bool Resolve(Unit u, Command cmd, FailureAction action, string message, FailureReason reason)
    {
        bool player = _sim.IsPlayerFaction(u.FactionId) || _sim.Options.AdminMode;
        u.StopReason = message;
        u.StopTick = _sim.Tick;
        if (action == FailureAction.CancelQueue)
        {
            CleanupActive(u, cmd, refundCast: true);
            cmd.Status = CommandStatus.Failed;
            cmd.StatusText = message;
            _sim.Emit(SimEventType.CommandFailed, player ? LogLevel.Normal : LogLevel.Detail, $"{u.Name}: команда «{cmd.Describe(_sim)}» не выполнена — {message}; очередь отменена", u.Id);
            u.Queue.Remove(cmd);
            ClearQueue(u, "после неудачи: " + message, CommandStatus.Cancelled);
            if (player && _sim.IsPlayerControlled(u))
            {
                if ((reason == FailureReason.NoPath || reason == FailureReason.PathBlocked) && Rules.Autopause.PathUnavailable)
                    _sim.RequestAutopause($"{u.Name}: маршрут недоступен");
                else if (Rules.Autopause.CommandFailed)
                    _sim.RequestAutopause($"{u.Name}: команда отменена");
            }
        }
        else
        {
            CleanupActive(u, cmd, refundCast: true);
            cmd.Status = CommandStatus.Skipped;
            cmd.StatusText = message;
            _sim.Emit(SimEventType.CommandSkipped, player ? LogLevel.Normal : LogLevel.Detail, $"{u.Name}: команда «{cmd.Describe(_sim)}» пропущена — {message}", u.Id);
            u.Queue.Remove(cmd);
            if (player && _sim.IsPlayerControlled(u) && Rules.Autopause.CommandFailed)
                _sim.RequestAutopause($"{u.Name}: команда пропущена");
        }
        RecomputeEstimates(u);
        return true;
    }

    // ------------------------------------------------------------------ command executors

    private Result ExecMove(Unit u, Command cmd)
    {
        var target = cmd.TargetCell!.Value;
        if (u.Pos == target || cmd.NodePhase > 0) return Arrive(u, cmd);
        return StepTowards(u, cmd, target);
    }

    /// <summary>One step of gradual movement towards a cell (route, approach before an attack). Done — already there.</summary>
    private Result StepTowards(Unit u, Command cmd, HexCoord target)
    {
        if (u.Pos == target) return Result.Done;
        if (u.Flags.BlocksActions) return Fail(FailureReason.Disabled, "юнит оглушён");
        if (u.Flags.BlocksMovement) return Fail(FailureReason.Disabled, "юнит обездвижен");
        if (u.Stat(Stats.MoveSpeed) <= 0) return Fail(FailureReason.Disabled, "скорость 0");

        // Blocked (docs/DECISIONS_2.md §27): cannot advance while the link holds; no AP spent, no timeout —
        // the link disappears as soon as the blocker is gone, pushed away, stunned or out of slots.
        if (_sim.Blocking.BlockerOf(u) is { } blocker)
        {
            if (u.Def.Blockable.WhenBlocked != BlockedBehavior.FindDetour || !TryDetour(u, cmd, blocker, target))
            {
                cmd.Status = CommandStatus.Waiting;
                cmd.StatusText = $"заблокирован: {blocker.Name}";
                return Result.Continue;
            }
            ClearWait(cmd);
        }

        if (cmd.Path == null || cmd.Path.Count == 0 || HexCoord.Distance(cmd.Path[0], u.Pos) != 1)
        {
            var path = _sim.Planner.Route(u, cmd, u.Pos, u.Facing, target);
            if (!path.Found && cmd.Optional)
            {
                // A non-mandatory node: a neighbouring hex is enough (docs/DECISIONS_3.md §41).
                var near = _sim.Pathfinder.Find(_sim, u, u.Pos, target, new PathOptions { GoalTolerance = 1 });
                if (near.Found)
                {
                    if (near.Path.Count == 0)
                    {
                        cmd.NodePhase = 1;
                        _sim.Emit(SimEventType.Info, LogLevel.Detail, $"{u.Name}: необязательная точка {target} недоступна — достаточно рядом", u.Id);
                        return Arrive(u, cmd);
                    }
                    path = near;
                }
            }
            if (!path.Found)
            {
                if (path.Failure == FailureReason.PathBlocked) return Fail(FailureReason.PathBlocked, path.Reason);
                // Distinguish "blocked by units" (wait/repath) from "no route at all".
                var ignoring = _sim.Pathfinder.Find(_sim, u, u.Pos, target, new PathOptions { IgnoreUnits = true });
                if (ignoring.Found) return Fail(FailureReason.PathBlocked, "путь перекрыт юнитами");
                return Fail(FailureReason.NoPath, path.Reason);
            }
            cmd.Path = path.Path;
            RecomputeEstimates(u);
        }

        var next = cmd.Path[0];
        var from = _sim.Map.Get(u.Pos);
        var to = _sim.Map.Get(next);
        var check = MovementRules.Check(MoveContext.For(_sim, u), from, to, false, out var cost);
        if (check != StepCheck.Ok)
        {
            cmd.Path = null;
            return Fail(FailureReason.PathBlocked, MovementRules.Describe(check, to));
        }

        var dir = u.Pos.DirectionTo(next)!.Value;
        double ap = cost * Rules.Ap.MovePerCost;
        int turnSteps = Rules.Turn.AutoFaceOnMove ? HexDirectionExt.StepsBetween(u.Facing, dir) : 0;
        if (Rules.Turn.AutoFaceCostsAp) ap += turnSteps * Rules.Turn.ApPerStep;
        if (u.Ap + 1e-9 < ap) return Fail(FailureReason.NotEnoughAp, $"нужно {ap:0.#} ОД на шаг, есть {u.Ap:0.#}");

        ClearWait(cmd);
        cmd.RepathAttempts = 0;
        u.Ap -= ap;
        cmd.SpentAp += ap;
        _stepAp[u.Id] = ap;
        _sim.Emit(SimEventType.ApSpent, LogLevel.Diagnostic, $"{u.Name}: −{ap:0.##} ОД (шаг)", u.Id, value: ap);
        if (Rules.Turn.AutoFaceOnMove && turnSteps > 0 && !u.Flags.BlocksTurning)
        {
            u.Facing = dir;
            _sim.Vision.MarkDirty();
        }
        if (!to!.Reservations.Contains(u)) to.Reservations.Add(u);
        u.StepFrom = u.Pos;
        u.StepTo = next;
        u.StepTicksDone = 0;
        u.StepTicksTotal = Math.Max(1, _sim.TicksFor(cost / u.Stat(Stats.MoveSpeed)));
        cmd.Path.RemoveAt(0);
        RecomputeEstimates(u);
        return Result.Continue;
    }

    /// <summary>Path around the blocker's zone; on success the enemy is not re-blocked by it for a moment.</summary>
    private bool TryDetour(Unit u, Command cmd, Unit blocker, HexCoord target)
    {
        var avoid = new HashSet<HexCoord>();
        foreach (var c in _sim.Map.InRadius(blocker.Pos, 1))
            if (c.Coord != u.Pos)
                avoid.Add(c.Coord);
        var path = _sim.Planner.Route(u, cmd, u.Pos, u.Facing, target, avoid: avoid);
        if (!path.Found) return false;
        cmd.Path = path.Path;
        RecomputeEstimates(u);
        _sim.Blocking.GrantDetour(u, 2.0);
        _sim.Emit(SimEventType.Info, LogLevel.Normal, $"{u.Name} обходит блок {blocker.Name}", u.Id, blocker.Id);
        return true;
    }

    private Result ExecTurn(Unit u, Command cmd) => ManualTurn(u, cmd.Direction!.Value, cmd);

    /// <summary>
    /// A player/AI-ordered turn (command «Поворот», arrival facing of a route node): free and instant by
    /// default; the rules may make it cost AP and/or take time per 60° step (docs/DECISIONS_3.md §40).
    /// </summary>
    private Result ManualTurn(Unit u, HexDirection target, Command cmd)
    {
        if (u.Facing == target) return Result.Done;
        if (!u.Def.Perception.HasFacing)
        {
            // No facing (omni / none): orientation is cosmetic — instant and free.
            u.Facing = target;
            return Result.Done;
        }
        if (u.Flags.BlocksActions) return Fail(FailureReason.Disabled, "юнит оглушён");
        if (u.Flags.BlocksTurning) return Fail(FailureReason.Disabled, "юнит не может поворачиваться");
        bool payAp = Rules.Turn.ManualCostsAp, takeTime = Rules.Turn.ManualTakesTime;
        if (!takeTime)
        {
            double ap = payAp ? HexDirectionExt.StepsBetween(u.Facing, target) * Rules.Turn.ApPerStep : 0;
            if (u.Ap + 1e-9 < ap) return Fail(FailureReason.NotEnoughAp, $"нужно {ap:0.##} ОД на поворот");
            u.Ap -= ap;
            cmd.SpentAp += ap;
            var old = u.Facing;
            u.Facing = target;
            _sim.Vision.MarkDirty();
            _sim.Emit(SimEventType.Turned, LogLevel.Diagnostic, $"{u.Name}: поворот {old.RuShortName()} → {target.RuShortName()}" + (ap > 0 ? $" (−{ap:0.##} ОД)" : ""), u.Id);
            ClearWait(cmd);
            return Result.Done;
        }
        if (!TurnOneStep(u, target, cmd, payAp)) return Fail(FailureReason.NotEnoughAp, $"нужно {Rules.Turn.ApPerStep:0.##} ОД на поворот");
        ClearWait(cmd);
        return u.Facing == target && u.BusyUntil <= _sim.Tick ? Result.Done : Result.Continue;
    }

    /// <summary>
    /// Route node after arrival (docs/DECISIONS_3.md §41): facing after arrival, waiting, then the
    /// condition to continue (bounded by the queue's maximal wait).
    /// </summary>
    private Result Arrive(Unit u, Command cmd)
    {
        if (cmd.NodePhase == 0) cmd.NodePhase = 1;
        if (cmd.NodePhase == 1)
        {
            if (cmd.ArrivalFacing is { } f && u.Facing != f)
            {
                var r = ManualTurn(u, f, cmd);
                if (r != Result.Done) return r;
            }
            cmd.NodePhase = 2;
            cmd.PhaseUntil = _sim.Tick + _sim.TicksFor(cmd.WaitAfter);
        }
        if (cmd.NodePhase == 2)
        {
            if (_sim.Tick < cmd.PhaseUntil)
            {
                cmd.StatusText = $"ожидание в точке ({(cmd.PhaseUntil - _sim.Tick) * _sim.Dt:0.#} с)";
                return Result.Continue;
            }
            cmd.NodePhase = 3;
            cmd.PhaseUntil = _sim.Tick + _sim.TicksFor(Rules.Queue.MaxWaitSeconds);
        }
        if (cmd.Until != null && !_sim.Scenario.Evaluate(cmd.Until, u))
        {
            if (_sim.Tick >= cmd.PhaseUntil)
            {
                // The command leaves the queue here; Continue only stops processing this unit for the tick.
                Resolve(u, cmd, FailureAction.Skip, $"условие «{cmd.Until.Describe()}» не выполнено за {Rules.Queue.MaxWaitSeconds:0} с", FailureReason.Invalid);
                return Result.Continue;
            }
            cmd.Status = CommandStatus.Waiting;
            cmd.StatusText = "ждёт условия: " + cmd.Until.Describe();
            return Result.Continue;
        }
        return Result.Done;
    }

    /// <summary>One turning step by the common turn rules (used by auto-attacks to face their target).</summary>
    internal bool TurnStep(Unit u, HexDirection target) => TurnOneStep(u, target, null);

    /// <summary>Turns one 60° step towards the target direction (AP + time). False if not enough AP.</summary>
    private bool TurnOneStep(Unit u, HexDirection target, Command? cmd, bool payAp = true)
    {
        int signed = HexDirectionExt.SignedSteps(u.Facing, target);
        if (signed == 0) return true;
        double ap = payAp ? Rules.Turn.ApPerStep : 0;
        if (u.Ap + 1e-9 < ap) return false;
        u.Ap -= ap;
        if (cmd != null) cmd.SpentAp += ap;
        var old = u.Facing;
        u.Facing = u.Facing.Rotate(Math.Sign(signed));
        u.BusyUntil = _sim.Tick + _sim.TicksFor(Rules.Turn.SecondsPerStep);
        _sim.Vision.MarkDirty();
        _sim.Emit(SimEventType.Turned, LogLevel.Diagnostic, $"{u.Name}: поворот {old.RuShortName()} → {u.Facing.RuShortName()}" + (ap > 0 ? $" (−{ap:0.##} ОД)" : ""), u.Id);
        return true;
    }

    private Result ExecSkill(Unit u, Command cmd)
    {
        var skill = cmd.Type == CommandType.Attack ? u.BasicAttack : _sim.Content.Skill(cmd.SkillId);
        if (skill == null) return Fail(FailureReason.Invalid, "навык не найден");

        if (cmd.Phase == 0)
        {
            if (cmd.StartedTick == _sim.Tick && cmd.ApproachSearches == 0) cmd.FacingBefore = u.Facing;
            if (TargetPhase(u, cmd, skill) is { } tr) return tr;
            var check = _sim.Skills.Validate(u, skill, cmd.TargetCell, cmd.TargetUnitId, cmd.Direction, u.Pos, u.Facing);
            if (!check.Ok && ApproachStep(u, cmd, skill, check) is { } approach) return approach;
            if (!check.Ok) return Fail(check.Reason == FailureReason.None ? FailureReason.Invalid : check.Reason, check.Message);
            cmd.ApproachCell = null;

            bool wantFace = skill.FaceTarget && Rules.Turn.AutoFaceTarget && !(skill.Targeting == TargetingMode.Self && !cmd.Direction.HasValue);
            if (wantFace && check.Orientation != u.Facing && !u.Flags.BlocksTurning)
            {
                // Make sure the turn does not eat the AP the skill itself needs.
                if (u.Ap + 1e-9 < skill.ApCost + Rules.Turn.ApPerStep)
                    return Fail(FailureReason.NotEnoughAp, $"нужно {skill.ApCost + Rules.Turn.ApPerStep:0.##} ОД (поворот + навык)");
                TurnOneStep(u, check.Orientation, cmd);
                return Result.Continue;
            }
            ClearWait(cmd);
            _sim.Skills.Begin(u, skill, check, cmd.TargetUnitId);
            cmd.SpentAp += skill.ApCost;
            cmd.Phase = 1;
        }

        if (cmd.Phase == 1)
        {
            var cast = u.Casting;
            if (cast == null) return Fail(FailureReason.Invalid, "подготовка прервана");
            if (_sim.Tick < cast.ReleaseTick) return Result.Continue;
            // Re-validate target before release (it may have moved or died during preparation).
            if (skill.Targeting is TargetingMode.Unit or TargetingMode.Cell)
            {
                var recheck = _sim.Skills.Validate(u, skill, cmd.TargetCell, cmd.TargetUnitId, cmd.Direction, u.Pos, u.Facing, checkCosts: false);
                if (!recheck.Ok)
                {
                    _sim.Skills.Interrupt(u, InterruptCause.Cancelled, recheck.Message);
                    cmd.SpentAp = Math.Max(0, cmd.SpentAp - skill.ApCost);
                    cmd.Phase = 0;
                    // The target was lost during the preparation: the policy may pick another one (nothing was spent).
                    var policy = LostPolicy(cmd, skill);
                    if (policy == TargetLostPolicy.NewTarget && recheck.Reason is FailureReason.TargetLost or FailureReason.OutOfRange or FailureReason.NoLineOfSight
                        && PickTarget(u, cmd, skill, "цель потеряна при подготовке"))
                        return Result.Continue;
                    // Waiting for the target / going to its last known position: the policy runs from the next tick.
                    if (policy is TargetLostPolicy.WaitReturn or TargetLostPolicy.LastKnown && recheck.Reason == FailureReason.TargetLost
                        && _sim.GetUnit(cmd.TargetUnitId) is { Alive: true })
                        return Result.Continue;
                    return Fail(recheck.Reason == FailureReason.None ? FailureReason.Invalid : recheck.Reason, recheck.Message);
                }
            }
            _sim.Skills.Release(cast);
            // Costs paid at the release were missing: the skill did not fire and the command is already resolved.
            if (cast.Interrupted) return Result.Continue;
            cmd.Phase = 2;
            u.BusyUntil = _sim.Tick + _sim.TicksFor(skill.RecoveryTime);
            AfterUse(u, cmd, skill);
            return u.BusyUntil > _sim.Tick ? Result.Continue : Result.Done;
        }

        return Result.Done;
    }

    private static TargetLostPolicy LostPolicy(Command cmd, SkillDef skill) => cmd.OnTargetLost ?? skill.OnTargetLost;

    /// <summary>
    /// The target of a skill order before it starts (docs/DECISIONS_3.md §43): automatic choice by the skill's
    /// rules, then the «target lost» policy — another target, waiting for its return (bounded), the last
    /// known position, or the queue's failure policy. Null — go on with validation.
    /// </summary>
    private Result? TargetPhase(Unit u, Command cmd, SkillDef skill)
    {
        if (skill.Targeting is not (TargetingMode.Unit or TargetingMode.Cell) && !cmd.AutoTarget) return null;
        var t = _sim.GetUnit(cmd.TargetUnitId);
        if (cmd.AutoTarget && (t == null || !t.Alive) && cmd.LastKnownTarget == null)
        {
            if (!PickTarget(u, cmd, skill, "")) return Fail(FailureReason.TargetLost, "нет допустимых целей");
            t = _sim.GetUnit(cmd.TargetUnitId);
        }
        if (t == null) return null; // a cell order without a unit
        bool visible = t.Alive && _sim.Vision.CanFactionSee(u.FactionId, t);
        if (visible)
        {
            cmd.LastKnownTarget = t.Pos;
            cmd.LostSince = -1;
            // A cell skill chosen against a unit follows it until it starts.
            if (skill.Targeting == TargetingMode.Cell && cmd.AutoTarget) cmd.TargetCell = t.Pos;
            if (skill.UsesChosenDirection && cmd.AutoTarget) cmd.Direction = u.Pos.DirectionTo(t.Pos) ?? cmd.Direction;
            return null;
        }
        // Lost: dead, gone or hidden.
        var policy = LostPolicy(cmd, skill);
        string why = t.Alive ? "цель не видна" : "цель погибла";
        if (cmd.LostSince < 0) cmd.LostSince = _sim.Tick;
        switch (policy)
        {
            case TargetLostPolicy.NewTarget:
                if (PickTarget(u, cmd, skill, why)) return null;
                return Fail(FailureReason.TargetLost, why + ", других целей нет");
            case TargetLostPolicy.WaitReturn when t.Alive:
                if (_sim.Tick - cmd.LostSince > _sim.TicksFor(Rules.Queue.MaxWaitSeconds))
                    return Fail(FailureReason.TargetLost, $"цель не вернулась за {Rules.Queue.MaxWaitSeconds:0} с");
                cmd.Status = CommandStatus.Waiting;
                cmd.StatusText = "ждёт возвращения цели";
                return Result.Continue;
            case TargetLostPolicy.LastKnown when t.Alive && cmd.LastKnownTarget is { } last:
            {
                if (u.Pos == last || HexCoord.Distance(u.Pos, last) <= 1 && !_sim.Map.Get(last)!.Occupants.All(o => o == t))
                {
                    cmd.LastKnownTarget = null;
                    return Fail(FailureReason.TargetLost, "у последней известной позиции цели нет");
                }
                var r = StepTowards(u, cmd, last);
                if (r == Result.Continue) cmd.StatusText = "идёт к последней известной позиции цели";
                return r == Result.Done ? Result.Continue : r;
            }
            default:
                return Fail(FailureReason.TargetLost, why);
        }
    }

    /// <summary>Chooses a (new) target by the skill's rules; true if found.</summary>
    private bool PickTarget(Unit u, Command cmd, SkillDef skill, string reason)
    {
        var (now, later) = _sim.Targets.Candidates(u, skill, u.Pos, u.Facing);
        int old = cmd.TargetUnitId;
        now.RemoveAll(x => x.Id == old && !x.Alive);
        var pool = now.Count > 0 ? now : cmd.Approach && skill.Approach && skill.AllowMoveBeforeUse ? later : now;
        if (pool.Count == 0) return false;
        var sel = skill.AutoTarget;
        var pick = _sim.Targets.Choose(u, pool, sel.Rules, sel.Mode, sel.RandomTies, skill, old, out var why);
        if (pick == null) return false;
        cmd.TargetUnitId = pick.Id;
        cmd.TargetCell = pick.Pos;
        if (skill.UsesChosenDirection) cmd.Direction = u.Pos.DirectionTo(pick.Pos) ?? u.Facing;
        cmd.LastKnownTarget = pick.Pos;
        cmd.LostSince = -1;
        cmd.ApproachCell = null;
        cmd.ApproachFor = null;
        cmd.TargetChoice = why;
        _sim.Emit(SimEventType.Info, LogLevel.Detail,
            $"{u.Name}: «{skill.DisplayName}» — {(reason != "" ? reason + ", новая цель" : "цель")} {pick.Name} ({why})", u.Id, pick.Id, pick.Pos);
        return true;
    }

    /// <summary>Facing after the attack and «continue the route» (docs/DECISIONS_3.md §42).</summary>
    private void AfterUse(Unit u, Command cmd, SkillDef skill)
    {
        if (!u.Alive) return;
        if (skill.FacingAfter == FacingAfterUse.Keep && u.Facing != cmd.FacingBefore && u.Def.Perception.HasFacing && !u.Flags.BlocksTurning)
        {
            u.Facing = cmd.FacingBefore;
            _sim.Vision.MarkDirty();
        }
        else if (skill.FacingAfter == FacingAfterUse.Set && !u.Flags.BlocksTurning)
        {
            u.Facing = skill.FacingAfterDirection;
            _sim.Vision.MarkDirty();
        }
        if (!skill.ContinueRoute) ClearPending(u, $"«{skill.DisplayName}» завершает маршрут");
    }

    /// <summary>
    /// Approach before an attack (docs/DECISIONS_3.md §42): the target is out of range or sight → walk to the
    /// cheapest position from which the skill can be used (melee — a free neighbouring hex, ranged — in range
    /// with line of sight). Null — no approach (not asked/allowed, or another problem).
    /// </summary>
    private Result? ApproachStep(Unit u, Command cmd, SkillDef skill, SkillCheck check)
    {
        if (!cmd.Approach || !skill.Approach || !skill.AllowMoveBeforeUse) return null;
        if (check.Reason is not (FailureReason.OutOfRange or FailureReason.NoLineOfSight)) return null;
        if (u.Flags.BlocksMovement || u.Flags.BlocksActions || u.Stat(Stats.MoveSpeed) <= 0) return null;
        var target = _sim.GetUnit(cmd.TargetUnitId);
        HexCoord? targetPos = skill.Targeting == TargetingMode.Unit ? target?.Pos : cmd.TargetCell;
        if (targetPos == null) return null;
        // Search again only when the target moved or the chosen position turned out wrong; a failed search waits for the target to move.
        bool searched = cmd.ApproachFor == targetPos;
        bool arrivedInvalid = cmd.ApproachCell != null && u.Pos == cmd.ApproachCell;
        if (searched && cmd.ApproachCell == null) return Fail(check.Reason, check.Message + " — нет позиции для атаки");
        if (!searched || arrivedInvalid)
        {
            if (++cmd.ApproachSearches > 50) return Fail(FailureReason.OutOfRange, "не удаётся подойти к цели");
            var pos = _sim.Planner.AttackPosition(u, skill, cmd.TargetCell, cmd.TargetUnitId, u.Pos, out var why);
            if (pos == null || pos == u.Pos)
            {
                cmd.ApproachFor = targetPos;
                cmd.ApproachCell = null;
                return Fail(check.Reason, check.Message + (why != "" ? " — " + why : ""));
            }
            cmd.ApproachCell = pos;
            cmd.ApproachFor = targetPos;
            cmd.Path = null;
            _sim.Emit(SimEventType.Info, LogLevel.Detail, $"{u.Name}: подходит к позиции {pos} для «{skill.DisplayName}»", u.Id, cmd.TargetUnitId, pos);
        }
        var r = StepTowards(u, cmd, cmd.ApproachCell!.Value);
        if (r == Result.Done)
        {
            cmd.ApproachCell = null;
            return Result.Continue;
        }
        if (r == Result.Continue) cmd.StatusText = "подходит к цели";
        return r;
    }

    private Result ExecDefend(Unit u, Command cmd)
    {
        if (cmd.Phase == 0)
        {
            if (u.Flags.BlocksActions) return Fail(FailureReason.Disabled, "юнит оглушён");
            double ap = Rules.Ap.DefendCost;
            if (u.Ap + 1e-9 < ap) return Fail(FailureReason.NotEnoughAp, $"нужно {ap:0.#} ОД на стойку");
            ClearWait(cmd);
            u.Ap -= ap;
            cmd.SpentAp += ap;
            u.Defending = true;
            _sim.Statuses.RecalculateModifiers(u);
            cmd.PhaseUntil = cmd.Duration > 0 ? _sim.Tick + _sim.TicksFor(cmd.Duration) : long.MaxValue;
            cmd.Phase = 1;
            _sim.Emit(SimEventType.Info, LogLevel.Normal, $"{u.Name} занимает защитную стойку", u.Id);
            return Result.Continue;
        }
        bool timeUp = cmd.Duration > 0 && _sim.Tick >= cmd.PhaseUntil;
        bool superseded = cmd.Duration <= 0 && u.Queue.Count > 1;
        if (!timeUp && !superseded) return Result.Continue;
        u.Defending = false;
        _sim.Statuses.RecalculateModifiers(u);
        _sim.Emit(SimEventType.Info, LogLevel.Detail, $"{u.Name} выходит из стойки", u.Id);
        return Result.Done;
    }

    private Result ExecWait(Unit u, Command cmd)
    {
        if (cmd.Phase == 0)
        {
            cmd.PhaseUntil = _sim.Tick + _sim.TicksFor(cmd.Duration);
            cmd.Phase = 1;
        }
        return _sim.Tick >= cmd.PhaseUntil ? Result.Done : Result.Continue;
    }
}
