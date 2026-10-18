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
                if (_sim.GetUnit(c.TargetUnitId) == null) return "не указана цель";
                return null;
            case CommandType.Skill:
            {
                var s = _sim.Content.Skill(c.SkillId);
                if (s == null || !u.KnowsSkill(c.SkillId)) return "юнит не знает этот навык";
                if (_sim.Content.DisabledSkills.TryGetValue(s.Id, out var why)) return "навык отключён: " + why;
                if (s.Targeting == TargetingMode.Unit && _sim.GetUnit(c.TargetUnitId) == null) return "не указана цель";
                if (s.Targeting == TargetingMode.Cell && (!c.TargetCell.HasValue || !_sim.Map.Contains(c.TargetCell.Value))) return "не указана клетка";
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
                        rest += MovementRules.EntryCost(Rules, u.Def.MoveCategory, prev, cell) * Rules.Ap.MovePerCost;
                        prev = cell;
                    }
                    c.EstimatedAp = c.SpentAp + rest;
                    if (c.TargetCell.HasValue) pos = c.TargetCell.Value;
                }
                else if (c.Type == CommandType.Move && c.TargetCell.HasValue)
                {
                    pos = c.TargetCell.Value;
                }
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
            var c = _sim.Map.Get(u.StepTo.Value);
            if (c != null && c.ReservedBy == u) c.ReservedBy = null;
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
        _sim.Relocate(u, to);
        _sim.Emit(SimEventType.Moved, LogLevel.Diagnostic, $"{u.Name}: {from} → {to}", u.Id, cell: to);
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
                        var path = _sim.Pathfinder.Find(_sim, u, u.Pos, cmd.TargetCell!.Value, new PathOptions { HazardPenalty = HazardPenaltyFor(u) });
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

    private double HazardPenaltyFor(Unit u)
    {
        if (u.AiControlled && u.AiProfile != null) return u.AiProfile.HazardAvoidance;
        return Rules.Move.HazardPenalty;
    }

    // ------------------------------------------------------------------ command executors

    private Result ExecMove(Unit u, Command cmd)
    {
        var target = cmd.TargetCell!.Value;
        if (u.Pos == target) return Result.Done;
        if (u.Flags.BlocksActions) return Fail(FailureReason.Disabled, "юнит оглушён");
        if (u.Flags.BlocksMovement) return Fail(FailureReason.Disabled, "юнит обездвижен");
        if (u.Stat(Stats.MoveSpeed) <= 0) return Fail(FailureReason.Disabled, "скорость 0");

        if (cmd.Path == null || cmd.Path.Count == 0 || HexCoord.Distance(cmd.Path[0], u.Pos) != 1)
        {
            var path = _sim.Pathfinder.Find(_sim, u, u.Pos, target, new PathOptions { HazardPenalty = HazardPenaltyFor(u) });
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
        var check = MovementRules.Check(Rules, u.Def.MoveCategory, from, to, u, false, _sim.AllowStacking, out var cost);
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
        to!.ReservedBy = u;
        u.StepFrom = u.Pos;
        u.StepTo = next;
        u.StepTicksDone = 0;
        u.StepTicksTotal = Math.Max(1, _sim.TicksFor(cost / u.Stat(Stats.MoveSpeed)));
        cmd.Path.RemoveAt(0);
        RecomputeEstimates(u);
        return Result.Continue;
    }

    private Result ExecTurn(Unit u, Command cmd)
    {
        var target = cmd.Direction!.Value;
        if (u.Facing == target) return Result.Done;
        if (u.Flags.BlocksActions) return Fail(FailureReason.Disabled, "юнит оглушён");
        if (u.Flags.BlocksTurning) return Fail(FailureReason.Disabled, "юнит не может поворачиваться");
        if (!TurnOneStep(u, target, cmd)) return Fail(FailureReason.NotEnoughAp, $"нужно {Rules.Turn.ApPerStep:0.##} ОД на поворот");
        ClearWait(cmd);
        return u.Facing == target && u.BusyUntil <= _sim.Tick ? Result.Done : Result.Continue;
    }

    /// <summary>Turns one 60° step towards the target direction (AP + time). False if not enough AP.</summary>
    private bool TurnOneStep(Unit u, HexDirection target, Command? cmd)
    {
        int signed = HexDirectionExt.SignedSteps(u.Facing, target);
        if (signed == 0) return true;
        double ap = Rules.Turn.ApPerStep;
        if (u.Ap + 1e-9 < ap) return false;
        u.Ap -= ap;
        if (cmd != null) cmd.SpentAp += ap;
        var old = u.Facing;
        u.Facing = u.Facing.Rotate(Math.Sign(signed));
        u.BusyUntil = _sim.Tick + _sim.TicksFor(Rules.Turn.SecondsPerStep);
        _sim.Vision.MarkDirty();
        _sim.Emit(SimEventType.Turned, LogLevel.Diagnostic, $"{u.Name}: поворот {old.RuShortName()} → {u.Facing.RuShortName()} (−{ap:0.##} ОД)", u.Id);
        return true;
    }

    private Result ExecSkill(Unit u, Command cmd)
    {
        var skill = cmd.Type == CommandType.Attack ? u.BasicAttack : _sim.Content.Skill(cmd.SkillId);
        if (skill == null) return Fail(FailureReason.Invalid, "навык не найден");

        if (cmd.Phase == 0)
        {
            var check = _sim.Skills.Validate(u, skill, cmd.TargetCell, cmd.TargetUnitId, cmd.Direction, u.Pos, u.Facing);
            if (!check.Ok) return Fail(check.Reason == FailureReason.None ? FailureReason.Invalid : check.Reason, check.Message);

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
                    return Fail(recheck.Reason == FailureReason.None ? FailureReason.Invalid : recheck.Reason, recheck.Message);
                }
            }
            _sim.Skills.Release(cast);
            cmd.Phase = 2;
            u.BusyUntil = _sim.Tick + _sim.TicksFor(skill.RecoveryTime);
            return u.BusyUntil > _sim.Tick ? Result.Continue : Result.Done;
        }

        return Result.Done;
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
