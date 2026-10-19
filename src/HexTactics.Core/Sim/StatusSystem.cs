using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;

namespace HexTactics.Core.Sim;

/// <summary>
/// States and accumulations (docs/DECISIONS.md §11). Generic: every status is data — stat
/// modifiers, control flags and periodic effects per level.
/// </summary>
public sealed class StatusSystem
{
    private readonly Simulation _sim;

    public StatusSystem(Simulation sim)
    {
        _sim = sim;
    }

    public double ResistanceTo(Unit u, StatusDef def) =>
        Math.Clamp(u.Def.StatusResistances.TryGetValue(def.Id, out var r) ? r : 0.0, 0.0, 1.0);

    public bool IsImmune(Unit u, StatusDef def, out string reason)
    {
        reason = "";
        if (def.ImmuneCreatureCategories.Contains(u.Def.CreatureCategory))
        {
            reason = $"иммунитет ({u.Def.CreatureCategory})";
            return true;
        }
        if (u.IsImmuneTo(def.Id, def.Tags))
        {
            reason = "иммунитет";
            return true;
        }
        if (ResistanceTo(u, def) >= 1.0)
        {
            reason = "полное сопротивление";
            return true;
        }
        foreach (var b in def.BlockedBy)
        {
            var other = u.FindStatus(b);
            if (other != null)
            {
                reason = $"заблокировано состоянием «{other.Def.DisplayName}»";
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Applies a status. amount = points (accumulating) or stacks (timed). duration 0 = status default.
    /// Returns true if the status was applied or changed.
    /// </summary>
    public bool Apply(Unit target, StatusDef def, double amount, double duration, int sourceUnit, bool force = false)
    {
        if (!target.Alive) return false;
        if (IsImmune(target, def, out var why))
        {
            _sim.Emit(SimEventType.StatusResisted, LogLevel.Detail, $"{target.Name}: «{def.DisplayName}» не действует — {why}", sourceUnit, target.Id);
            return false;
        }
        double resist = ResistanceTo(target, def);
        int countBefore = target.Statuses.Count(x => x.Def == def);
        bool changed;
        if (def.Kind == StatusKind.Accumulating)
            changed = ApplyAccumulating(target, def, amount * (1.0 - resist), sourceUnit);
        else
        {
            if (!force && resist > 0 && !_sim.Rng.Chance(1.0 - resist))
            {
                _sim.Emit(SimEventType.StatusResisted, LogLevel.Normal, $"{target.Name} сопротивляется «{def.DisplayName}»", sourceUnit, target.Id);
                return false;
            }
            changed = ApplyTimed(target, def, amount, duration, sourceUnit);
        }
        if (!changed) return false;

        // Periodic power fixed at (re)application; optional immediate first tick for a new instance.
        var applied = target.Statuses.LastOrDefault(x => x.Def == def);
        if (applied != null)
        {
            if (target.Statuses.Count(x => x.Def == def) > countBefore) applied.AppliedCell = target.Pos;
            TakeSnapshot(target, applied);
            if (def.TickOnApply && target.Statuses.Count(x => x.Def == def) > countBefore) PeriodicTick(target, applied);
        }

        foreach (var c in def.Cancels)
        {
            var other = target.FindStatus(c);
            while (other != null)
            {
                Remove(target, other, $"снято «{def.DisplayName}»");
                other = target.FindStatus(c);
            }
        }
        RecalculateModifiers(target);
        return true;
    }

    private bool ApplyAccumulating(Unit target, StatusDef def, double gain, int source)
    {
        if (gain <= 0) return false;
        var inst = target.FindStatus(def.Id);
        bool isNew = inst == null;
        if (inst == null)
        {
            inst = new StatusInstance { Def = def, Points = 0, SourceUnit = source };
            target.Statuses.Add(inst);
        }
        inst.Points = Math.Min(def.MaxPoints, inst.Points + gain);
        inst.LastGain += gain;
        if (source >= 0) inst.SourceUnit = source;
        if (isNew)
            _sim.Emit(SimEventType.StatusApplied, LogLevel.Detail, $"{target.Name} начинает накапливать «{def.DisplayName}»", source, target.Id);
        UpdateLevel(target, inst);
        return true;
    }

    private bool ApplyTimed(Unit target, StatusDef def, double amount, double duration, int source)
    {
        // Only «в течение времени» counts down; the other modes end by their own rule (docs/DECISIONS_2.md §33).
        double dur = def.Lasts != StatusLasts.Timed ? 0 : duration > 0 ? duration : def.Duration;
        long ticks = dur > 0 ? _sim.TicksFor(dur) : -1;
        int stacks = Math.Max(1, (int)Math.Round(amount));
        int maxStacks = Math.Min(Math.Max(1, def.MaxStacks), _sim.Rules.Limits.MaxStatusStacks);
        var existing = target.FindStatus(def.Id);

        if (existing == null)
        {
            var inst = new StatusInstance
            {
                Def = def, Stacks = Math.Min(stacks, maxStacks), RemainingTicks = ticks, TotalTicks = ticks, SourceUnit = source,
            };
            target.Statuses.Add(inst);
            UpdateLevel(target, inst, announce: false);
            _sim.Emit(SimEventType.StatusApplied, LogLevel.Normal,
                $"{target.Name}: «{inst.LevelName}»{(ticks > 0 ? $" на {dur:0.#} с" : "")}", source, target.Id);
            OnLevelEntered(target, inst);
            return true;
        }

        switch (def.Reapply)
        {
            case ReapplyPolicy.Ignore:
                return false;
            case ReapplyPolicy.Refresh:
                if (ticks < 0) existing.RemainingTicks = -1;
                else if (existing.RemainingTicks >= 0 && ticks > existing.RemainingTicks) existing.RemainingTicks = existing.TotalTicks = ticks;
                else return false;
                _sim.Emit(SimEventType.StatusApplied, LogLevel.Detail, $"{target.Name}: «{def.DisplayName}» обновлено", source, target.Id);
                return true;
            case ReapplyPolicy.Extend:
            {
                long max = _sim.TicksFor(Math.Max(def.MaxDuration, dur));
                if (existing.RemainingTicks < 0 || ticks < 0) return false;
                long before = existing.RemainingTicks;
                existing.RemainingTicks = Math.Min(max, existing.RemainingTicks + ticks);
                existing.TotalTicks = Math.Max(existing.TotalTicks, existing.RemainingTicks);
                if (existing.RemainingTicks == before) return false;
                _sim.Emit(SimEventType.StatusApplied, LogLevel.Detail, $"{target.Name}: «{def.DisplayName}» продлено", source, target.Id);
                return true;
            }
            case ReapplyPolicy.Stack:
            {
                int count = 0;
                StatusInstance? shortest = null;
                foreach (var s in target.Statuses)
                {
                    if (s.Def != def) continue;
                    count++;
                    if (shortest == null || (s.RemainingTicks >= 0 && s.RemainingTicks < shortest.RemainingTicks)) shortest = s;
                }
                if (count < maxStacks)
                {
                    var inst = new StatusInstance { Def = def, Stacks = 1, RemainingTicks = ticks, TotalTicks = ticks, SourceUnit = source };
                    target.Statuses.Add(inst);
                    UpdateLevel(target, inst, announce: false);
                    _sim.Emit(SimEventType.StatusApplied, LogLevel.Normal, $"{target.Name}: «{def.DisplayName}» ×{count + 1}", source, target.Id);
                    return true;
                }
                if (shortest != null && ticks > shortest.RemainingTicks)
                {
                    shortest.RemainingTicks = shortest.TotalTicks = ticks;
                    return true;
                }
                return false;
            }
            case ReapplyPolicy.Intensify:
            {
                int before = existing.Stacks;
                existing.Stacks = Math.Min(maxStacks, existing.Stacks + stacks);
                if (ticks > existing.RemainingTicks || ticks < 0) existing.RemainingTicks = existing.TotalTicks = ticks;
                if (existing.Stacks == before) return ticks > 0;
                UpdateLevel(target, existing);
                _sim.Emit(SimEventType.StatusApplied, LogLevel.Normal, $"{target.Name}: «{def.DisplayName}» усилено до {existing.Stacks}", source, target.Id);
                return true;
            }
            case ReapplyPolicy.ReplaceIfStronger:
                if (stacks > existing.Stacks || (ticks > existing.RemainingTicks && existing.RemainingTicks >= 0))
                {
                    existing.Stacks = Math.Min(stacks, maxStacks);
                    existing.RemainingTicks = existing.TotalTicks = ticks;
                    existing.SourceUnit = source;
                    UpdateLevel(target, existing);
                    _sim.Emit(SimEventType.StatusApplied, LogLevel.Detail, $"{target.Name}: «{def.DisplayName}» заменено более сильным", source, target.Id);
                    return true;
                }
                return false;
        }
        return false;
    }

    public void Remove(Unit u, StatusInstance inst, string reason)
    {
        if (!u.Statuses.Remove(inst)) return;
        _sim.Emit(SimEventType.StatusRemoved, LogLevel.Normal, $"{u.Name}: «{inst.Def.DisplayName}» снято ({reason})", -1, u.Id);
        RecalculateModifiers(u);
    }

    public int RemoveById(Unit u, string id, string reason)
    {
        int n = 0;
        for (var s = u.FindStatus(id); s != null; s = u.FindStatus(id))
        {
            Remove(u, s, reason);
            n++;
        }
        return n;
    }

    private int ComputeLevel(StatusInstance inst)
    {
        double value = inst.Def.Kind == StatusKind.Accumulating ? inst.Points : inst.Stacks;
        int idx = -1;
        for (int i = 0; i < inst.Def.Levels.Count; i++)
            if (value + 1e-9 >= inst.Def.Levels[i].Threshold)
                idx = i;
        if (idx < 0 && inst.Def.Kind == StatusKind.Timed && inst.Def.Levels.Count > 0) idx = 0;
        return idx;
    }

    private void UpdateLevel(Unit u, StatusInstance inst, bool announce = true)
    {
        int lvl = ComputeLevel(inst);
        if (lvl == inst.LevelIndex) return;
        bool up = lvl > inst.LevelIndex;
        inst.LevelIndex = lvl;
        inst.TickTimer = 0;
        if (announce)
        {
            string text = lvl < 0 ? $"{u.Name}: «{inst.Def.DisplayName}» ниже порога" : $"{u.Name}: {inst.LevelName}";
            _sim.Emit(SimEventType.StatusLevel, LogLevel.Normal, text, inst.SourceUnit, u.Id, value: lvl);
        }
        RecalculateModifiers(u);
        if (up && announce) OnLevelEntered(u, inst);
    }

    private void OnLevelEntered(Unit u, StatusInstance inst)
    {
        var level = inst.Level;
        if (level == null) return;
        if (level.OnEnter.Count > 0) _sim.Combat.ApplyDirect(_sim.GetUnit(inst.SourceUnit), u, level.OnEnter, 1.0, inst.Def.DisplayName);
        if ((level.Flags.BlocksActions || level.Flags.InterruptsCast || level.Flags.MindControl || level.Flags.BlocksSkills) && u.Casting != null)
            _sim.Skills.Interrupt(u, InterruptCause.Control, inst.Def.DisplayName);
    }

    /// <summary>
    /// Changes the points of an accumulating status (or the stacks of a timed one) by delta, also downwards
    /// (operation «изменить накопление»).
    /// </summary>
    public bool ModifyAccumulation(Unit target, StatusDef def, double delta, int source)
    {
        if (!target.Alive || Math.Abs(delta) < 1e-9) return false;
        if (delta > 0) return Apply(target, def, delta, 0, source, force: true);
        var inst = target.FindStatus(def.Id);
        if (inst == null) return false;
        if (def.Kind == StatusKind.Accumulating)
        {
            inst.Points = Math.Max(0, inst.Points + delta);
            if (inst.Points <= 1e-9) Remove(target, inst, "накопление снижено");
            else
            {
                UpdateLevel(target, inst);
                RecalculateModifiers(target);
            }
        }
        else
        {
            inst.Stacks += (int)Math.Round(delta);
            if (inst.Stacks <= 0) Remove(target, inst, "стаки сняты");
            else
            {
                UpdateLevel(target, inst);
                RecalculateModifiers(target);
            }
        }
        _sim.Emit(SimEventType.StatusLevel, LogLevel.Detail, $"{target.Name}: «{def.DisplayName}» {delta:+0.#;-0.#}", source, target.Id);
        return true;
    }

    /// <summary>A status of a mode other than «в течение времени» ends when its rule stops holding.</summary>
    private string? EndReason(Unit u, StatusInstance inst)
    {
        var d = inst.Def;
        switch (d.Lasts)
        {
            case StatusLasts.WhileCondition:
                return d.WhileCondition != null && !_sim.Scenario.Evaluate(d.WhileCondition, u) ? "условие больше не выполняется" : null;
            case StatusLasts.WhileOnCell:
                return u.Pos != inst.AppliedCell ? "юнит ушёл с клетки" : null;
            case StatusLasts.WhileEffectActive:
                if (!string.IsNullOrEmpty(d.WhileStatus) && u.FindStatus(d.WhileStatus) == null) return $"«{_sim.Content.Status(d.WhileStatus)?.DisplayName ?? d.WhileStatus}» закончилось";
                if (!string.IsNullOrEmpty(d.WhileCellEffect) && _sim.Map.Get(u.Pos)?.Effects.Any(e => e.Def.Id == d.WhileCellEffect) != true)
                    return $"на клетке нет «{_sim.Content.CellEffect(d.WhileCellEffect)?.DisplayName ?? d.WhileCellEffect}»";
                return null;
            default:
                return null;
        }
    }

    /// <summary>Per-tick update: durations, decay, level changes and periodic effects.</summary>
    public void UpdateUnit(Unit u)
    {
        if (u.StatChanges.Count > 0 && u.StatChanges.RemoveAll(c => c.UntilTick >= 0 && c.UntilTick <= _sim.Tick) > 0) RecalculateModifiers(u);
        if (u.Statuses.Count == 0) return;
        var snapshot = u.Statuses.ToArray();
        foreach (var inst in snapshot)
        {
            if (!u.Alive) return;
            if (!u.Statuses.Contains(inst)) continue;
            if (inst.Def.Lasts != StatusLasts.Timed && EndReason(u, inst) is { } why)
            {
                Remove(u, inst, why);
                continue;
            }
            if (inst.Def.Kind == StatusKind.Timed)
            {
                if (inst.RemainingTicks > 0)
                {
                    inst.RemainingTicks--;
                    if (inst.RemainingTicks == 0)
                    {
                        Remove(u, inst, "время истекло");
                        continue;
                    }
                }
            }
            else
            {
                inst.Points -= inst.Def.DecayPerSecond * _sim.Dt;
                inst.LastGain = 0;
                if (inst.Points <= 1e-9)
                {
                    Remove(u, inst, "очки исчерпаны");
                    continue;
                }
                UpdateLevel(u, inst);
            }

            var level = inst.Level;
            if (level != null && level.OnTick.Count > 0)
            {
                inst.TickTimer++;
                if (inst.TickTimer >= _sim.TicksFor(level.TickInterval))
                {
                    inst.TickTimer = 0;
                    PeriodicTick(u, inst);
                }
            }
        }
    }

    /// <summary>
    /// One DoT/HoT tick (docs/DECISIONS_2.md §29): effects of the level with the source's current power or
    /// the snapshot taken at application; a status with a tick count ends after its last tick.
    /// </summary>
    private void PeriodicTick(Unit u, StatusInstance inst)
    {
        var level = inst.Level;
        if (level == null || level.OnTick.Count == 0) return;
        int li = inst.LevelIndex;
        Func<int, double?>? fixedAmounts = inst.Def.Power == PeriodicPower.Snapshot && inst.Snapshot != null
            ? i => inst.Snapshot.TryGetValue((li, i), out var v) ? v : null
            : null;
        _sim.Combat.ApplyDirect(_sim.GetUnit(inst.SourceUnit), u, level.OnTick, 1.0, inst.Def.DisplayName, DamageSource.Periodic, fixedAmounts);
        inst.TicksDone++;
        if (inst.Def.TickCount > 0 && inst.TicksDone >= inst.Def.TickCount && u.Statuses.Contains(inst))
            Remove(u, inst, $"все тики ({inst.Def.TickCount})");
    }

    /// <summary>Snapshot of periodic amounts at application (source stats now), for «по моменту наложения».</summary>
    private void TakeSnapshot(Unit target, StatusInstance inst)
    {
        if (inst.Def.Power != PeriodicPower.Snapshot) return;
        var src = _sim.GetUnit(inst.SourceUnit);
        inst.Snapshot = new Dictionary<(int, int), double>();
        for (int l = 0; l < inst.Def.Levels.Count; l++)
        {
            var ticks = inst.Def.Levels[l].OnTick;
            for (int i = 0; i < ticks.Count; i++)
                if (ticks[i].Type is EffectType.Damage or EffectType.Heal or EffectType.Shield)
                    inst.Snapshot[(l, i)] = _sim.Combat.EffectAmount(ticks[i], src, target, 1.0);
        }
    }

    private void RemoveWhere(Unit u, Func<StatusRemoveDef, bool> pred, string reason)
    {
        if (u.Statuses.Count == 0) return;
        foreach (var inst in u.Statuses.ToArray())
            if (inst.Def.RemoveOn.Any(pred) && u.Statuses.Contains(inst))
                Remove(u, inst, reason);
    }

    public void OnMoved(Unit u) => RemoveWhere(u, r => r.When == StatusRemoveTrigger.Moved, "движение");

    public void OnHealed(Unit u) => RemoveWhere(u, r => r.When == StatusRemoveTrigger.Healed, "лечение");

    public void OnDamaged(Unit u, string damageType) =>
        RemoveWhere(u, r => r.When == StatusRemoveTrigger.DamagedBy && _sim.Damage.Taxonomy.Matches(r.Damage, damageType), "урон");

    public void OnEnteredCellEffect(Unit u, string cellEffect) =>
        RemoveWhere(u, r => r.When == StatusRemoveTrigger.EnteredCellEffect && r.CellEffect == cellEffect, "эффект клетки");

    public void OnSourceDied(Unit dead)
    {
        foreach (var u in _sim.Units)
        {
            if (!u.Alive || u.Statuses.Count == 0) continue;
            foreach (var inst in u.Statuses.ToArray())
                if (inst.SourceUnit == dead.Id && inst.Def.RemoveOn.Any(r => r.When == StatusRemoveTrigger.SourceDied) && u.Statuses.Contains(inst))
                    Remove(u, inst, $"источник {dead.Name} погиб");
        }
    }

    /// <summary>Rebuilds stat modifiers and control flags from passives, statuses and stance.</summary>
    public void RecalculateModifiers(Unit u)
    {
        u.Stats.ClearModifiers();
        u.Flags.Clear();
        u.Defenses.Clear();
        u.Offenses.Clear();
        if (!u.Def.Defense.IsEmpty) u.Defenses.Add(u.Def.Defense);
        if (!u.Def.Offense.IsEmpty) u.Offenses.Add(u.Def.Offense);
        foreach (var p in u.Passives)
        {
            // Defense/offense of passives act permanently (like their stat modifiers).
            if (!p.Defense.IsEmpty) u.Defenses.Add(p.Defense);
            if (!p.Offense.IsEmpty) u.Offenses.Add(p.Offense);
            if (p.Trigger == PassiveTrigger.Always)
                foreach (var m in p.StatMods)
                    u.Stats.AddModifier(m);
        }
        foreach (var s in u.Statuses)
        {
            var level = s.Level;
            if (level == null) continue;
            foreach (var m in level.StatMods) u.Stats.AddModifier(m);
            u.Flags.OrWith(level.Flags);
            if (!level.Defense.IsEmpty) u.Defenses.Add(level.Defense);
            if (!level.Offense.IsEmpty) u.Offenses.Add(level.Offense);
        }
        foreach (var (mod, _) in u.StatChanges) u.Stats.AddModifier(mod);
        if (u.Defending)
        {
            u.Stats.AddFlat(Content.Stats.Evasion, _sim.Rules.Ap.DefendEvasionBonus);
            u.Stats.AddFlat(Content.Stats.BlockChance, _sim.Rules.Ap.DefendBlockBonus);
        }
        if (u.Hp > u.HpMax) u.Hp = u.HpMax;
        if (u.Ap > u.ApMax) u.Ap = u.ApMax;
        UpdateMindControl(u);
        _sim.Vision.MarkDirty();
    }

    /// <summary>Mind control: the unit temporarily fights for the faction of the status source.</summary>
    private void UpdateMindControl(Unit u)
    {
        string faction = u.BaseFactionId;
        if (u.Flags.MindControl)
        {
            foreach (var s in u.Statuses)
            {
                if (s.Level?.Flags.MindControl != true) continue;
                var src = _sim.GetUnit(s.SourceUnit);
                if (src != null) faction = src.BaseFactionId;
            }
        }
        if (string.IsNullOrEmpty(faction) || faction == u.FactionId) return;
        u.FactionId = faction;
        u.AiControlled = !_sim.IsPlayerFaction(faction) || _sim.Options.AiFactions.Contains(faction);
        _sim.Commands.ClearQueue(u, "смена стороны (контроль разума)", CommandStatus.Cancelled);
        _sim.Emit(SimEventType.Info, LogLevel.Normal, $"{u.Name} теперь сражается за «{_sim.Content.Faction(faction)?.DisplayName ?? faction}»", -1, u.Id);
    }
}
