using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>An active defensive stance of a unit (a defensive skill with «Защита» enabled).</summary>
public sealed class DefenseState
{
    public SkillDef Skill { get; init; } = null!;
    public DefenseStanceDef Def => Skill.Defense;
    public long StartTick { get; init; }
    /// <summary>End tick; long.MaxValue — until another order is given.</summary>
    public long UntilTick { get; set; } = long.MaxValue;
    /// <summary>Order that created the stance (it does not end the stance by itself).</summary>
    public int CommandId { get; init; } = -1;
    /// <summary>Attacks it can still stop or weaken; −1 — unlimited.</summary>
    public int BlocksLeft { get; set; } = -1;
    public int Avoided { get; set; }
    public int Reduced { get; set; }
    public double Prevented { get; set; }
    public HexCoord StartPos { get; init; }
}

/// <summary>
/// Defensive skills (docs/DECISIONS_3.md §45): the stance lives on the unit and works inside the damage
/// pipeline — at the hit step (dodge, parry with a counter-attack) and at the armor step (reduction of the
/// chosen damage types, blocks). It ends by time, a new order (duration 0), stun, missing upkeep, used-up
/// blocks or movement (when moving is not allowed).
/// </summary>
public sealed class DefenseSystem
{
    private readonly Simulation _sim;

    public DefenseSystem(Simulation sim)
    {
        _sim = sim;
    }

    public void Activate(Unit u, SkillDef skill, double durationOverride = 0, int commandId = -1)
    {
        if (!skill.Defense.Enabled || !u.Alive) return;
        if (u.Stance != null) End(u, "новая защита");
        var d = skill.Defense;
        double dur = durationOverride > 0 ? durationOverride : d.Duration;
        u.Stance = new DefenseState
        {
            Skill = skill,
            StartTick = _sim.Tick,
            UntilTick = dur > 0 ? _sim.Tick + _sim.TicksFor(dur) : long.MaxValue,
            CommandId = commandId,
            BlocksLeft = d.MaxBlocks > 0 ? d.MaxBlocks : -1,
            StartPos = u.Pos,
        };
        u.Defending = true;
        _sim.Statuses.RecalculateModifiers(u);
        _sim.Emit(SimEventType.DefenseStarted, LogLevel.Normal, $"{u.Name}: «{skill.DisplayName}» — {d.Describe()}", u.Id);
    }

    public void End(Unit u, string reason)
    {
        var st = u.Stance;
        if (st == null) return;
        u.Stance = null;
        u.Defending = false;
        _sim.Statuses.RecalculateModifiers(u);
        string stats = st.Avoided + st.Reduced > 0 ? $" (отражено атак: {st.Avoided}, ослаблено: {st.Reduced}, предотвращено урона: {st.Prevented:0})" : "";
        _sim.Emit(SimEventType.DefenseEnded, LogLevel.Normal, $"{u.Name}: «{st.Skill.DisplayName}» закончилась — {reason}{stats}", u.Id);
    }

    /// <summary>Per tick: duration, stun, upkeep, a new order, movement.</summary>
    public void Update(Unit u)
    {
        var st = u.Stance;
        if (st == null) return;
        var d = st.Def;
        if (_sim.Tick >= st.UntilTick)
        {
            End(u, "время вышло");
            return;
        }
        if (d.BrokenByStun && u.Flags.BlocksActions)
        {
            End(u, "оглушение");
            return;
        }
        // «Until another order»: the stance's own order holds it; any other order ends it.
        if (st.UntilTick == long.MaxValue && u.Queue.Items.Any(c => c.Id != st.CommandId))
        {
            End(u, "новый приказ");
            return;
        }
        if (!d.AllowMove && u.StepTo != null)
        {
            End(u, "движение");
            return;
        }
        foreach (var c in d.Upkeep)
        {
            double need = c.Amount * _sim.Dt;
            if (need <= 0) continue;
            if (string.IsNullOrEmpty(c.Resource))
            {
                if (u.Ap + 1e-9 < need)
                {
                    End(u, "не хватает ОД на поддержание");
                    return;
                }
                u.Ap -= need;
            }
            else
            {
                if (!u.Resources.TryGetValue(c.Resource, out var r) || r.Current - need < Math.Min(0, r.Def.MinValue) - 1e-9)
                {
                    End(u, $"не хватает ресурса «{_sim.Content.Get<ResourceDef>(c.Resource)?.DisplayName ?? c.Resource}» на поддержание");
                    return;
                }
                r.Current -= need;
            }
        }
    }

    /// <summary>Strength of the stance against the attack: 0 — does not apply, 0.5 — halved by piercing, 1 — full.</summary>
    public double Factor(DamageRequest q)
    {
        var target = q.Target;
        var st = target.Stance;
        var attacker = q.Attacker;
        if (st == null || attacker == null || attacker == target || q.ReflectDepth > 0) return 0;
        if (q.Source is not (DamageSource.Skill or DamageSource.AutoAttack)) return 0;
        var d = st.Def;
        if (d.Sectors.Count > 0)
        {
            var sector = q.Roll?.Sector ?? CombatSystem.SectorFor(target, attacker.Pos);
            if (sector != AttackSector.None && !d.Sectors.Contains(sector)) return 0;
        }
        if (d.AttackerLayers.Count > 0 && !d.AttackerLayers.Contains(attacker.Layer)) return 0;
        if (q.Skill?.PiercesDefense == true)
            return d.Pierce switch
            {
                PierceInteraction.Ignore => 0,
                PierceInteraction.Half => 0.5,
                _ => 1,
            };
        return 1;
    }

    public enum Avoid
    {
        None,
        Dodge,
        Parry,
    }

    /// <summary>Hit step: dodge, then parry (fixed draw order; no draw when the chance is zero).</summary>
    public Avoid TryAvoid(DamageRequest q)
    {
        double k = Factor(q);
        if (k <= 0) return Avoid.None;
        var target = q.Target;
        var st = target.Stance!;
        var d = st.Def;
        var attacker = q.Attacker!;
        if (d.DodgeChance > 0 && _sim.Rng.Chance(Math.Clamp(d.DodgeChance * k, 0, 1)))
        {
            Count(target, st, true, 0);
            _sim.Emit(SimEventType.DefenseAvoided, LogLevel.Normal, $"{target.Name} уклоняется от удара {attacker.Name} («{st.Skill.DisplayName}»)", attacker.Id, target.Id);
            return Avoid.Dodge;
        }
        if (d.ParryChance > 0 && _sim.Rng.Chance(Math.Clamp(d.ParryChance * k, 0, 1)))
        {
            Count(target, st, true, 0);
            _sim.Emit(SimEventType.DefenseAvoided, LogLevel.Normal, $"{target.Name} парирует удар {attacker.Name} («{st.Skill.DisplayName}»)", attacker.Id, target.Id);
            return Avoid.Parry;
        }
        return Avoid.None;
    }

    /// <summary>Armor step: the reduction of the matching damage parts. Returns the prevented amount.</summary>
    public double Reduce(DamageRequest q, List<DamagePart> parts, DamageTaxonomy taxonomy)
    {
        double k = Factor(q);
        var st = q.Target.Stance;
        if (k <= 0 || st == null || st.Def.DamageReduction <= 0) return 0;
        double share = Math.Clamp(st.Def.DamageReduction * k / 100.0, 0, 1);
        double prevented = 0;
        foreach (var p in parts)
        {
            if (p.Amount <= 0) continue;
            if (st.Def.DamageTypes.Count > 0 && !taxonomy.MatchesAny(st.Def.DamageTypes, p.Type)) continue;
            double x = p.Amount * share;
            p.Amount -= x;
            prevented += x;
        }
        if (prevented > 0) Count(q.Target, st, false, prevented);
        return prevented;
    }

    private void Count(Unit u, DefenseState st, bool avoided, double prevented)
    {
        if (avoided) st.Avoided++;
        else st.Reduced++;
        st.Prevented += prevented;
        if (st.BlocksLeft > 0 && --st.BlocksLeft == 0) _pendingEnd.Add(u);
    }

    private readonly List<Unit> _pendingEnd = new();

    /// <summary>After the hit: reflection, the counter-attack of a parry, and stances whose blocks are used up.</summary>
    public void AfterHit(DamageRequest q, DamageResult r, Avoid avoided)
    {
        var target = q.Target;
        var st = target.Stance;
        var attacker = q.Attacker;
        if (st != null && attacker != null && attacker.Alive && target.Alive)
        {
            if (avoided == Avoid.None && st.Def.ReflectPercent > 0 && Factor(q) > 0 && r.Dealt > 0)
            {
                double amount = r.Dealt * st.Def.ReflectPercent / 100.0;
                r.Reflected += amount;
                r.Step("отражение стойкой", amount, $"→ {attacker.Name}");
                _sim.Damage.Apply(new DamageRequest
                {
                    Attacker = target, Target = attacker, Amount = amount, DamageType = q.DamageType, Source = DamageSource.Reflection,
                    Cause = $"отражение ({st.Skill.DisplayName})", ReflectDepth = q.ReflectDepth + 1,
                });
            }
            if (avoided == Avoid.Parry && st.Def.CounterOnParry) Counter(target, attacker, q.ReflectDepth + 1);
        }
        foreach (var u in _pendingEnd.ToList())
            if (u.Stance != null && u.Stance.BlocksLeft == 0)
                End(u, $"защита исчерпана ({u.Stance.Def.MaxBlocks} атак)");
        _pendingEnd.Clear();
    }

    /// <summary>Counter-attack of a parry: the basic attack's damage on the attacker, if it is within its range.</summary>
    private void Counter(Unit defender, Unit attacker, int depth)
    {
        var basic = defender.BasicAttack;
        if (basic == null || basic.Stages.Count == 0) return;
        int dist = HexCoord.Distance(defender.Pos, attacker.Pos);
        if (dist > Math.Max(1, basic.RangeMax)) return;
        _sim.Emit(SimEventType.Reaction, LogLevel.Normal, $"{defender.Name} контратакует {attacker.Name}", defender.Id, attacker.Id);
        var roll = _sim.Combat.Roll(defender, attacker, basic, defender.Pos);
        foreach (var e in basic.Stages[0].Effects)
        {
            if (e.Type != EffectType.Damage || !attacker.Alive) continue;
            double amount = _sim.Combat.EffectAmount(e, defender, attacker, 1.0, dist);
            _sim.Damage.Apply(new DamageRequest
            {
                Attacker = defender, Target = attacker, Amount = amount, DamageType = e.DamageType, Source = DamageSource.Skill, Skill = basic, Roll = roll,
                Cause = "контратака", ReflectDepth = depth,
            });
        }
    }
}
