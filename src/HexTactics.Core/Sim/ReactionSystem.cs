using System;
using System.Collections.Generic;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>
/// Passive abilities and reactions. Triggers are queued and processed after the current
/// action, each processed reaction may queue new ones with depth + 1. Depth and per-tick
/// event limits stop infinite chains (with a diagnostic log entry).
/// </summary>
public sealed class ReactionSystem
{
    private readonly Simulation _sim;
    private readonly Queue<(PassiveTrigger Trigger, int Owner, int Source, int Depth)> _queue = new();
    private int _currentDepth;
    private bool _processing;

    public ReactionSystem(Simulation sim)
    {
        _sim = sim;
    }

    public long Processed { get; private set; }
    public long DroppedByDepth { get; private set; }

    public void Enqueue(PassiveTrigger trigger, Unit owner, Unit? source)
    {
        if (!owner.Alive) return;
        bool has = false;
        foreach (var p in owner.Passives)
            if (p.Trigger == trigger)
            {
                has = true;
                break;
            }
        if (!has) return;
        int depth = _processing ? _currentDepth + 1 : 0;
        if (depth > _sim.Rules.Limits.MaxReactionDepth)
        {
            DroppedByDepth++;
            _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"Цепочка реакций превысила глубину {_sim.Rules.Limits.MaxReactionDepth} — реакция {owner.Name} ({trigger}) пропущена", owner.Id);
            return;
        }
        _queue.Enqueue((trigger, owner.Id, source?.Id ?? -1, depth));
    }

    public void Process()
    {
        if (_processing) return;
        _processing = true;
        try
        {
            int guard = 0;
            while (_queue.Count > 0)
            {
                if (++guard > 10000 || _sim.EventLimitReached)
                {
                    _sim.Emit(SimEventType.Diagnostic, LogLevel.Diagnostic, $"Очередь реакций остановлена (лимит), отброшено {_queue.Count}");
                    _queue.Clear();
                    break;
                }
                var (trigger, ownerId, sourceId, depth) = _queue.Dequeue();
                _currentDepth = depth;
                var owner = _sim.GetUnit(ownerId);
                if (owner == null || !owner.Alive) continue;
                var source = _sim.GetUnit(sourceId);
                foreach (var p in owner.Passives)
                {
                    if (p.Trigger != trigger) continue;
                    TryFire(owner, p, source);
                    if (!owner.Alive) break;
                }
            }
        }
        finally
        {
            _processing = false;
            _currentDepth = 0;
        }
    }

    public void UpdatePeriodic(Unit u)
    {
        foreach (var p in u.Passives)
        {
            if (p.Trigger != PassiveTrigger.Periodic) continue;
            long t = u.PassivePeriodicTimer.TryGetValue(p.Id, out var v) ? v + 1 : 1;
            if (t >= _sim.TicksFor(p.Interval))
            {
                t = 0;
                TryFire(u, p, null);
            }
            u.PassivePeriodicTimer[p.Id] = t;
        }
    }

    private void TryFire(Unit owner, PassiveDef p, Unit? source)
    {
        if (p.Action == PassiveAction.None) return;
        if (owner.Flags.BlocksActions && p.Action is PassiveAction.CounterAttack or PassiveAction.UseSkillOnSource) return;
        if (owner.PassiveCooldownUntil.TryGetValue(p.Id, out var until) && until > _sim.Tick) return;
        if ((p.Action is PassiveAction.EffectsOnSource or PassiveAction.CounterAttack or PassiveAction.UseSkillOnSource) && (source == null || !source.Alive || source == owner)) return;
        double available = _sim.Rules.Ap.ReactionsRespectReserve ? owner.FreeAp : owner.Ap;
        if (p.ApCost > 0 && available + 1e-9 < p.ApCost) return;
        if (p.Chance < 1 && !_sim.Rng.Chance(p.Chance)) return;

        bool fired = false;
        switch (p.Action)
        {
            case PassiveAction.EffectsOnSelf:
                _sim.Emit(SimEventType.Reaction, p.Trigger == PassiveTrigger.Periodic ? LogLevel.Detail : LogLevel.Normal, $"{owner.Name}: «{p.DisplayName}»", owner.Id);
                _sim.Combat.ApplyDirect(owner, owner, p.Effects, 1.0, p.DisplayName);
                fired = true;
                break;
            case PassiveAction.EffectsOnSource:
                _sim.Emit(SimEventType.Reaction, LogLevel.Normal, $"{owner.Name}: «{p.DisplayName}» → {source!.Name}", owner.Id, source.Id);
                _sim.Combat.ApplyDirect(owner, source, p.Effects, 1.0, p.DisplayName);
                fired = true;
                break;
            case PassiveAction.CounterAttack:
                if (owner.BasicAttack != null) fired = ImmediateSkill(owner, owner.BasicAttack, source!, p);
                break;
            case PassiveAction.UseSkillOnSource:
            {
                var s = _sim.Content.Skill(p.Skill);
                if (s != null) fired = ImmediateSkill(owner, s, source!, p);
                break;
            }
        }
        if (!fired) return;
        Processed++;
        if (p.ApCost > 0)
        {
            owner.Ap = Math.Max(0, owner.Ap - p.ApCost);
            _sim.Emit(SimEventType.ApSpent, LogLevel.Detail, $"{owner.Name}: −{p.ApCost:0.#} ОД (реакция «{p.DisplayName}»)", owner.Id, value: p.ApCost);
        }
        if (p.Cooldown > 0) owner.PassiveCooldownUntil[p.Id] = _sim.Tick + _sim.TicksFor(p.Cooldown);
    }

    /// <summary>Reaction skill use: no preparation, no AP cost of the skill itself (the passive's cost applies).</summary>
    private bool ImmediateSkill(Unit owner, SkillDef skill, Unit target, PassiveDef p)
    {
        var check = _sim.Skills.Validate(owner, skill, target.Pos, target.Id, null, owner.Pos, owner.Facing, checkCosts: false);
        if (!check.Ok) return false;
        _sim.Emit(SimEventType.Reaction, LogLevel.Normal, $"{owner.Name}: «{p.DisplayName}» — {skill.DisplayName} по {target.Name}", owner.Id, target.Id);
        var cast = new SkillCast
        {
            Id = -1,
            Caster = owner,
            Skill = skill,
            Anchor = check.Anchor,
            Orientation = check.Orientation,
            TargetUnitId = target.Id,
            CasterPos = owner.Pos,
            StartTick = _sim.Tick,
            ReleaseTick = _sim.Tick,
            Released = true,
        };
        for (int i = 0; i < skill.Stages.Count; i++) _sim.Skills.ResolveStage(cast, i);
        return true;
    }
}
