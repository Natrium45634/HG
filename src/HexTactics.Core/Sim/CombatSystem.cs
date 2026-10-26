using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;
using HexTactics.Core.Expr;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Sim;

/// <summary>Result of the per-target rolls of an attack (docs/DECISIONS.md §7–8).</summary>
public sealed class AttackRoll
{
    public AttackSector Sector { get; init; }
    public bool TargetSeesAttacker { get; init; }
    public double NoticeChance { get; init; }
    public double SurpriseChance { get; init; }
    public bool Noticed { get; init; }
    public bool Surprise { get; init; }
    public double HitChance { get; init; }
    public bool Hit { get; init; }
    public double BlockChance { get; init; }
    public bool Blocked { get; init; }
    public double CritChance { get; init; }
    public bool Crit { get; init; }
    public int Distance { get; init; }

    public string SectorName => Sector switch
    {
        AttackSector.Front => "фронт",
        AttackSector.NearFlank => "ближний фланг",
        AttackSector.FarFlank => "дальний фланг",
        AttackSector.None => "без секторов",
        _ => "тыл",
    };

    public string Summary()
    {
        var parts = new List<string> { SectorName };
        parts.Add(Noticed ? $"замечена ({NoticeChance:P0})" : $"не замечена ({NoticeChance:P0})");
        if (Surprise) parts.Add("неожиданная");
        parts.Add(Hit ? $"попадание ({HitChance:P0})" : $"промах ({HitChance:P0})");
        if (Blocked) parts.Add("блок");
        if (Crit) parts.Add("КРИТ");
        return string.Join(", ", parts);
    }
}

/// <summary>Combat math and the generic unit-effect executor used by skills, statuses, cells and passives.</summary>
/// <summary>
/// Parameters of one hit of a series (docs/DECISIONS_3.md §45): attack kind, hit mode and accuracy, crit rule
/// and bonus; ForcedCrit — the series' single crit roll for this target.
/// </summary>
public readonly record struct HitSpec(StageAttackKind Kind, StageHitMode Mode, double Accuracy, CritRule Crit, double CritBonus, bool? ForcedCrit)
{
    public static HitSpec Of(SkillStageDef st) => new(st.AttackKind, st.HitMode, st.AccuracyBonus, st.Crit, st.CritChanceBonus, null);

    /// <summary>Is the hit ranged (cover, crit bonus): the stage's kind or the skill's.</summary>
    public static bool IsRanged(SkillDef? skill, HitSpec? spec) =>
        spec?.Kind is StageAttackKind.Ranged || (spec?.Kind is null or StageAttackKind.Inherit && skill?.Ranged == true);
}

public sealed class CombatSystem
{
    private readonly Simulation _sim;
    private SafeExpression? _hitFormula;
    private SafeExpression? _damageFormula;
    private string? _hitFormulaSrc;
    private string? _damageFormulaSrc;
    private readonly Dictionary<string, SafeExpression?> _effectFormulas = new();

    public CombatSystem(Simulation sim)
    {
        _sim = sim;
    }

    public Dictionary<string, int> KillsByFaction { get; } = new();
    /// <summary>Every kill: killer faction (empty — no killer) and the victim (scenario conditions by category).</summary>
    public List<(string KillerFaction, Unit Victim)> KillLog { get; } = new();

    private RulesDef Rules => _sim.Rules;

    // ------------------------------------------------------------------ sectors & rolls

    /// <summary>Sector of the attacker relative to the target's facing. Ties (wedge boundary) favour the defender.</summary>
    public static AttackSector SectorOf(HexCoord targetPos, HexDirection targetFacing, HexCoord attackerPos)
    {
        var wedges = targetPos.WedgesTo(attackerPos);
        if (wedges.Count == 0) return AttackSector.Front;
        int best = 3;
        foreach (var w in wedges) best = Math.Min(best, HexDirectionExt.StepsBetween(w, targetFacing));
        return (AttackSector)best;
    }

    /// <summary>
    /// Sector of an attack against a target with its perception model (docs/DECISIONS_2.md §23):
    /// several facings — the most frontal; omni / none — no sectors.
    /// </summary>
    public static AttackSector SectorFor(Unit target, HexCoord attackerPos)
    {
        var p = target.Def.Perception;
        switch (p.Model)
        {
            case FacingModel.Omni:
            case FacingModel.None:
                return AttackSector.None;
            case FacingModel.Multiple:
            {
                var best = AttackSector.Rear;
                foreach (var f in p.FacingsOf(target.Facing))
                {
                    var s = SectorOf(target.Pos, f, attackerPos);
                    if (s < best) best = s;
                }
                return best;
            }
            default:
                return SectorOf(target.Pos, target.Facing, attackerPos);
        }
    }

    public double NoticeChance(Unit attacker, Unit target, HexCoord attackerPos, out AttackSector sector, out bool sees)
    {
        var det = Rules.Detection;
        sector = SectorFor(target, attackerPos);
        var sr = det.For(sector);
        var perception = target.Def.Perception;
        if (sector == AttackSector.None && perception.OwnNoSectorRule)
            sr = new SectorRule { Sector = sector, NoticeChance = perception.NoticeChance, SurpriseChance = perception.SurpriseChance };
        sees = _sim.Vision.CanSee(target, attacker, attackerPos);
        double p = sr.NoticeChance + target.Stat(Stats.Perception) - attacker.Stat(Stats.Stealth);
        int dist = HexCoord.Distance(target.Pos, attackerPos);
        p -= det.NoticePenaltyPerHex * Math.Max(0, dist - det.NoticeFreeDistance);
        p += CellNoticeModifier(target.Pos) + CellNoticeModifier(attackerPos);
        p -= _sim.Map.Darkness * det.DarknessNoticePenalty;
        if (!sees) p *= det.UnseenNoticeFactor;
        if (target.Flags.Blind) p *= det.BlindNoticeFactor;
        if (target.Flags.BlocksActions) p = 0;
        return Math.Clamp(p, 0, 1);
    }

    private double CellNoticeModifier(HexCoord c)
    {
        var cell = _sim.Map.Get(c);
        if (cell == null) return 0;
        double s = 0;
        foreach (var e in cell.Effects) s += e.Def.NoticeModifier;
        return s;
    }

    public double SurpriseChance(AttackSector sector, bool targetSeesAttacker, Unit? target = null)
    {
        var det = Rules.Detection;
        double baseChance = det.For(sector).SurpriseChance;
        if (sector == AttackSector.None && target != null && target.Def.Perception.OwnNoSectorRule) baseChance = target.Def.Perception.SurpriseChance;
        double s = baseChance + (targetSeesAttacker ? 0 : det.SurpriseBonusVsUnaware);
        return Math.Clamp(s, 0, 1);
    }

    /// <summary>Creatures sharing the unit's cell (same layer) — crowding penalty (docs/DECISIONS_2.md §26).</summary>
    public int CellMates(Unit u)
    {
        var cell = _sim.Map.Get(u.Pos);
        if (cell == null) return 0;
        int n = 0;
        foreach (var o in cell.Occupants)
            if (o != u && o.Alive && o.Layer == u.Layer)
                n++;
        return n;
    }

    public double HitChance(Unit attacker, Unit target, SkillDef? skill, HexCoord attackerPos, bool noticed, bool surprise, HitSpec? spec = null)
    {
        var c = Rules.Combat;
        var det = Rules.Detection;
        double acc = attacker.Stat(Stats.Accuracy) - CellMates(attacker) * attacker.Def.Stacking.CrowdAccuracyPenalty * (attacker.Def.Stacking.CanShare ? 1 : 0);
        double eva = target.Stat(Stats.Evasion) * (noticed ? 1.0 : det.UnnoticedEvasionFactor)
                     - CellMates(target) * target.Def.Stacking.CrowdAccuracyPenalty * (target.Def.Stacking.CanShare ? 1 : 0);
        if (target.Flags.BlocksActions) eva = 0;
        var ac = _sim.Map.Get(attackerPos);
        var tc = _sim.Map.Get(target.Pos);
        double height = ((ac?.Height ?? 0) - (tc?.Height ?? 0)) * Rules.Height.AccuracyPerLevel;
        double sur = surprise ? det.SurpriseAccuracy : 0;
        int dist = HexCoord.Distance(attackerPos, target.Pos);
        double cover = HitSpec.IsRanged(skill, spec) && dist > 1 ? (tc?.Cover ?? 0) * c.CoverFactor : 0;
        double p;
        var f = HitFormula();
        if (f != null)
        {
            p = f.Evaluate(name => name switch
            {
                "base" => c.BaseHit,
                "accuracy" => acc,
                "evasion" => eva,
                "height" => height,
                "surprise" => sur,
                "cover" => cover,
                "distance" => dist,
                _ => 0,
            });
        }
        else
        {
            p = c.BaseHit + acc - eva + height + sur - cover;
        }
        p += spec?.Accuracy ?? 0;
        return Math.Clamp(p, c.MinHit, Math.Max(c.MinHit, c.MaxHit));
    }

    private SafeExpression? HitFormula()
    {
        var src = Rules.Combat.HitFormula;
        if (src != _hitFormulaSrc)
        {
            _hitFormulaSrc = src;
            _hitFormula = string.IsNullOrWhiteSpace(src) ? null :
                SafeExpression.TryParse(src, new[] { "base", "accuracy", "evasion", "height", "surprise", "cover", "distance" }, out var e, out _) ? e : null;
        }
        return _hitFormula;
    }

    private SafeExpression? DamageFormula()
    {
        var src = Rules.Combat.DamageFormula;
        if (src != _damageFormulaSrc)
        {
            _damageFormulaSrc = src;
            _damageFormula = string.IsNullOrWhiteSpace(src) ? null :
                SafeExpression.TryParse(src, new[] { "raw", "armor", "resist", "crit", "surprise", "block" }, out var e, out _) ? e : null;
        }
        return _damageFormula;
    }

    /// <summary>
    /// Rolls an attack against one target: one draw for notice/surprise, then hit, block, crit.
    /// Draw order is fixed for determinism.
    /// </summary>
    public AttackRoll Roll(Unit attacker, Unit target, SkillDef? skill, HexCoord attackerPos, HitSpec? spec = null)
    {
        double pNotice = NoticeChance(attacker, target, attackerPos, out var sector, out var sees);
        double pSurprise = SurpriseChance(sector, sees, target);
        double u = _sim.Rng.NextDouble();
        bool noticed = u < pNotice;
        bool surprise = !noticed && u >= 1.0 - pSurprise;

        double pHit = HitChance(attacker, target, skill, attackerPos, noticed, surprise, spec);
        bool always = spec?.Mode switch
        {
            StageHitMode.AlwaysHit => true,
            StageHitMode.Roll => false,
            _ => skill?.HitMode == HitMode.AlwaysHit,
        };
        bool hit = always || _sim.Rng.Chance(pHit);
        if (always) pHit = 1.0;

        double pBlock = 0;
        bool blocked = false;
        if (hit && noticed && !target.Flags.BlocksActions)
        {
            pBlock = Math.Clamp(target.Stat(Stats.BlockChance), 0, 1);
            if (pBlock > 0) blocked = _sim.Rng.Chance(pBlock);
        }
        double pCrit = 0;
        bool crit = false;
        if (hit)
        {
            // Crit chance: attacker, skill, attack kind and sector bonuses, target protection (docs/DECISIONS_2.md §29).
            pCrit = _sim.Damage.CritChance(attacker, target, skill, sector, surprise, spec);
            if (spec?.ForcedCrit is { } forced)
            {
                // One crit roll for the whole series: the first hit's result, for this target.
                crit = forced && pCrit > 0;
            }
            else if (pCrit > 0) crit = _sim.Rng.Chance(pCrit);
        }

        var roll = new AttackRoll
        {
            Sector = sector,
            TargetSeesAttacker = sees,
            NoticeChance = pNotice,
            SurpriseChance = pSurprise,
            Noticed = noticed,
            Surprise = surprise,
            HitChance = pHit,
            Hit = hit,
            BlockChance = pBlock,
            Blocked = blocked,
            CritChance = pCrit,
            Crit = crit,
            Distance = HexCoord.Distance(attackerPos, target.Pos),
        };

        string who = $"{attacker.Name} → {target.Name}";
        _sim.Emit(noticed ? SimEventType.AttackNoticed : SimEventType.AttackUnnoticed, LogLevel.Detail,
            noticed ? $"{target.Name} замечает атаку ({roll.SectorName}, {pNotice:P0})" : $"{target.Name} не замечает атаку ({roll.SectorName}, {pNotice:P0})",
            attacker.Id, target.Id);
        if (surprise) _sim.Emit(SimEventType.Surprise, LogLevel.Normal, $"Неожиданная атака! {who} ({roll.SectorName})", attacker.Id, target.Id);
        if (!hit) _sim.Emit(SimEventType.Miss, LogLevel.Normal, $"{who}: промах ({pHit:P0}{(noticed ? ", уклонение" : "")})", attacker.Id, target.Id);
        if (blocked) _sim.Emit(SimEventType.Block, LogLevel.Normal, $"{target.Name} блокирует удар", attacker.Id, target.Id);
        if (crit) _sim.Emit(SimEventType.Crit, LogLevel.Normal, $"{who}: критический удар!", attacker.Id, target.Id);
        if (crit) _sim.Reactions.Enqueue(PassiveTrigger.OnCrit, attacker, target);
        if (noticed) _sim.Reactions.Enqueue(PassiveTrigger.OnAttackNoticed, target, attacker);
        if (hit) _sim.Reactions.Enqueue(PassiveTrigger.OnHitEnemy, attacker, target);
        return roll;
    }

    // ------------------------------------------------------------------ damage & healing

    /// <summary>Custom damage formula of the rules (replaces steps 5–7 of the pipeline when set).</summary>
    public double? CustomDamage(double raw, double armor, double resist, bool crit, bool surprise, bool block)
    {
        var f = DamageFormula();
        if (f == null) return null;
        return f.Evaluate(name => name switch
        {
            "raw" => raw,
            "armor" => armor,
            "resist" => resist,
            "crit" => crit ? 1 : 0,
            "surprise" => surprise ? 1 : 0,
            "block" => block ? 1 : 0,
            _ => 0,
        });
    }

    /// <summary>
    /// Deals damage through the unified pipeline (<see cref="DamagePipeline"/>). Interrupt checks of the
    /// target's preparation happen here unless deferredInterrupts is given (skill resolution checks them
    /// in its own phase). Returns the health actually removed.
    /// </summary>
    public double DealDamage(Unit? source, Unit target, double raw, string damageType, AttackRoll? roll, string cause,
        List<(Unit Target, bool Surprise)>? deferredInterrupts = null, DamageSource kind = DamageSource.Other, SkillDef? skill = null)
    {
        if (!target.Alive) return 0;
        if (kind == DamageSource.Other && roll != null) kind = DamageSource.Skill;
        var r = _sim.Damage.Apply(new DamageRequest
        {
            Attacker = source, Target = target, Amount = raw, DamageType = damageType, Source = kind, Skill = skill, Roll = roll,
            Cause = cause, DeferredInterrupts = deferredInterrupts,
        });
        return r.HealthLost + r.Overkill;
    }

    /// <summary>After a hit the target survived: low-HP passives and interruption of its preparation.</summary>
    internal void OnDamagedAlive(Unit target, Unit? source, AttackRoll? roll, List<(Unit Target, bool Surprise)>? deferredInterrupts)
    {
        CheckLowHpPassives(target, source);
        if (target.Casting != null && !target.Casting.Released)
        {
            if (deferredInterrupts != null) deferredInterrupts.Add((target, roll?.Surprise == true));
            else TryDamageInterrupt(target, roll?.Surprise == true);
        }
    }

    private void CheckLowHpPassives(Unit u, Unit? source)
    {
        foreach (var p in u.Passives.ToArray())
        {
            if (p.Trigger != PassiveTrigger.OnLowHp) continue;
            if (u.HpFraction < p.HpThreshold && !u.LowHpTriggered)
            {
                u.LowHpTriggered = true;
                _sim.Reactions.Enqueue(PassiveTrigger.OnLowHp, u, source);
            }
        }
    }

    public void TryDamageInterrupt(Unit target, bool surprise)
    {
        var cast = target.Casting;
        if (cast == null || cast.Released) return;
        var ir = cast.Skill.Interrupt;
        if (!ir.ByDamage) return;
        double p = ir.ChanceOnDamage + (surprise ? Rules.Detection.SurpriseInterrupt : 0) - target.Stat(Stats.InterruptResist);
        p = Math.Clamp(p, 0, 1);
        if (p <= 0) return;
        if (_sim.Rng.Chance(p)) _sim.Skills.Interrupt(target, InterruptCause.Damage, $"урон ({p:P0})");
    }

    /// <summary>
    /// Healing (docs/DECISIONS_2.md §29): flat, % of max or % of missing health; × (1 + heal power of the
    /// source) × (1 + heal_received of the target); blocked by «нельзя лечить»; overheal may become a shield.
    /// </summary>
    /// <summary>Every heal: the healer (or null), the target, the effective part and the overheal (reports).</summary>
    public event Action<Unit?, Unit, double, double>? Healed;

    public double Heal(Unit? source, Unit target, double amount, string cause, HealMode mode = HealMode.Flat, bool applyPower = true, bool overhealShield = true)
    {
        if (!target.Alive || amount <= 0) return 0;
        double baseAmount = mode switch
        {
            HealMode.PercentMax => target.HpMax * amount / 100.0,
            HealMode.PercentMissing => Math.Max(0, target.HpMax - target.Hp) * amount / 100.0,
            _ => amount,
        };
        if (applyPower && source != null) baseAmount *= 1.0 + source.Stat(Stats.HealPower);
        baseAmount *= Math.Max(0, 1.0 + target.Stat(Stats.HealReceived));
        if (target.Flags.BlocksHealing)
        {
            _sim.Emit(SimEventType.Heal, LogLevel.Normal, $"{target.Name}: лечение не действует — {cause}", source?.Id ?? -1, target.Id, target.Pos, 0);
            return 0;
        }
        if (baseAmount <= 0) return 0;
        double before = target.Hp;
        target.Hp = Math.Min(target.HpMax, target.Hp + baseAmount);
        double healed = target.Hp - before;
        double over = baseAmount - healed;
        Healed?.Invoke(source, target, healed, Math.Max(0, over));
        var dr = Rules.Damage;
        if (overhealShield && over > 0.5 && dr.OverhealToShieldPercent > 0)
        {
            double cap = target.HpMax * dr.OverhealShieldMaxPercent / 100.0;
            double sh = Math.Min(over * dr.OverhealToShieldPercent / 100.0, cap);
            var existing = target.Shields.FirstOrDefault(x => x.Spec.Name == "Избыточное лечение");
            if (existing != null) sh = Math.Min(cap, existing.Amount + sh);
            if (sh > 0)
                _sim.Damage.AddShield(target, new ShieldSpecDef { Name = "Избыточное лечение", Stacking = ShieldStacking.Refresh, Color = "#b8ffb0" }, sh,
                    dr.OverhealShieldDuration, source?.Id ?? -1);
        }
        if (target.HpFraction >= Rules.Autopause.CriticalHpFraction) target.CriticalReported = false;
        if (healed > 0)
        {
            _sim.Emit(SimEventType.Heal, LogLevel.Normal, $"{target.Name} восстанавливает {healed:0} HP — {cause}", source?.Id ?? -1, target.Id, target.Pos, healed);
            _sim.Statuses.OnHealed(target);
        }
        foreach (var p in target.Passives)
            if (p.Trigger == PassiveTrigger.OnLowHp && target.HpFraction >= p.HpThreshold)
                target.LowHpTriggered = false;
        return healed;
    }

    public void Kill(Unit u, Unit? killer, string cause)
    {
        if (!u.Alive) return;
        u.Alive = false;
        u.Hp = 0;
        u.DiedAtTick = _sim.Tick;
        if (u.Casting != null) _sim.Skills.Interrupt(u, InterruptCause.Death, "гибель");
        _sim.Commands.ClearQueue(u, "гибель юнита", CommandStatus.Cancelled);
        if (u.StepTo.HasValue)
        {
            _sim.Map.Get(u.StepTo.Value)?.Reservations.Remove(u);
            u.StepTo = null;
            u.StepFrom = null;
        }
        var cell = _sim.Map.Get(u.Pos);
        cell?.Occupants.Remove(u);
        if (u.IsStructure) _sim.Structures.OnKilled(u);
        _sim.Blocking.OnRemoved(u);
        _sim.Squad.OnRemoved(u, died: true);
        u.Shields.Clear();
        _sim.Map.Touch(false);
        _sim.Vision.MarkDirty(urgent: true);

        _sim.Emit(SimEventType.Death, LogLevel.Normal, killer != null && killer != u ? $"{u.Name} погибает от руки {killer.Name} ({cause})" : $"{u.Name} погибает ({cause})",
            killer?.Id ?? -1, u.Id, u.Pos);
        if (Rules.Autopause.UnitDied && (_sim.IsPlayerFaction(u.BaseFactionId) || _sim.Vision.IsVisibleToPlayer(u) || _sim.Options.AdminMode))
            _sim.RequestAutopause($"{u.Name} погиб");

        KillLog.Add((killer != null && killer != u ? killer.FactionId : "", u));
        if (killer != null && killer != u)
        {
            killer.Kills++;
            if (_sim.RelationOf(killer, u) != Relation.Ally)
                KillsByFaction[killer.FactionId] = (KillsByFaction.TryGetValue(killer.FactionId, out var k) ? k : 0) + 1;
            _sim.Reactions.Enqueue(PassiveTrigger.OnKill, killer, u);
        }
        foreach (var ally in _sim.Units)
            if (ally.Alive && ally != u && ally.FactionId == u.FactionId)
                foreach (var p in ally.Passives)
                    if (p.Trigger == PassiveTrigger.OnAllyDied)
                    {
                        _sim.Reactions.Enqueue(PassiveTrigger.OnAllyDied, ally, killer);
                        break;
                    }

        _sim.Statuses.OnSourceDied(u);
        if (Rules.Death.SummonsDieWithOwner)
            foreach (var s in _sim.Units)
                if (s.Alive && s.SummonerId == u.Id)
                    Kill(s, null, "призыватель погиб");

        if (!string.IsNullOrEmpty(Rules.Death.CorpseCellEffect) && cell != null)
        {
            var ce = _sim.Content.CellEffect(Rules.Death.CorpseCellEffect);
            if (ce != null) _sim.CellEffects.Add(cell, ce, 1, 0, u.Id, 0, 0);
        }
    }

    // ------------------------------------------------------------------ generic effect executor

    public double EffectAmount(SkillEffectDef e, Unit? caster, Unit? target, double scale, int distance = 0, int level = 1)
    {
        double amount = e.Amount;
        if (caster != null && !string.IsNullOrEmpty(e.ScalingStat)) amount += caster.Stat(e.ScalingStat) * e.ScalingFactor;
        if (!string.IsNullOrWhiteSpace(e.Formula))
        {
            if (!_effectFormulas.TryGetValue(e.Formula, out var f))
            {
                var vars = new[] { "amount", "level", "distance", "caster_power", "caster_hp", "caster_hp_max", "target_hp", "target_hp_max", "target_armor" };
                f = SafeExpression.TryParse(e.Formula, vars, out var parsed, out _) ? parsed : null;
                _effectFormulas[e.Formula] = f;
            }
            if (f != null)
            {
                double baseAmount = amount;
                amount = f.Evaluate(name => name switch
                {
                    "amount" => baseAmount,
                    "level" => level,
                    "distance" => distance,
                    "caster_power" => caster?.Stat(Stats.Power) ?? 0,
                    "caster_hp" => caster?.Hp ?? 0,
                    "caster_hp_max" => caster?.HpMax ?? 0,
                    "target_hp" => target?.Hp ?? 0,
                    "target_hp_max" => target?.HpMax ?? 0,
                    "target_armor" => target?.Stat(Stats.Armor) ?? 0,
                    _ => 0,
                });
            }
        }
        return amount * scale;
    }

    /// <summary>
    /// Applies non-damage/heal unit effects of one effect definition (statuses, displacement,
    /// facing, resources, AP). Damage/heal are handled separately to respect the phase order.
    /// </summary>
    public void ApplyUnitEffect(SkillEffectDef e, Unit? source, Unit target, HexCoord origin, double scale, string cause)
    {
        if (!target.Alive) return;
        switch (e.Type)
        {
            case EffectType.ApplyStatus:
            {
                var sd = _sim.Content.Status(e.Status);
                if (sd != null) _sim.Statuses.Apply(target, sd, e.Amount * scale, e.Duration, source?.Id ?? -1);
                break;
            }
            case EffectType.RemoveStatus:
                _sim.Statuses.RemoveById(target, e.Status, cause);
                break;
            case EffectType.Push:
            case EffectType.Pull:
                Displace(target, origin, e.Type == EffectType.Push, e.Distance, source, cause);
                break;
            case EffectType.ChangeFacing:
            {
                HexDirection? nd = e.Facing switch
                {
                    FacingMode.TowardSource => target.Pos.DirectionTo(origin),
                    FacingMode.AwayFromSource => origin.DirectionTo(target.Pos),
                    FacingMode.Opposite => target.Facing.Opposite(),
                    FacingMode.Random => (HexDirection)_sim.Rng.NextInt(0, 6),
                    _ => e.SetDirection,
                };
                if (nd.HasValue && nd.Value != target.Facing)
                {
                    target.Facing = nd.Value;
                    _sim.Emit(SimEventType.FacingChanged, LogLevel.Normal, $"{target.Name} развёрнут на {nd.Value.RuName()} — {cause}", source?.Id ?? -1, target.Id);
                    _sim.Vision.MarkDirty();
                }
                break;
            }
            case EffectType.ModifyResource:
                if (target.Resources.TryGetValue(e.Resource, out var r))
                {
                    double before = r.Current;
                    r.Current = Math.Clamp(r.Current + e.Amount * scale, 0, r.Max);
                    if (Math.Abs(r.Current - before) > 1e-9)
                        _sim.Emit(SimEventType.ResourceChanged, LogLevel.Detail, $"{target.Name}: {r.Def.DisplayName} {r.Current - before:+0.#;-0.#} — {cause}", source?.Id ?? -1, target.Id);
                }
                break;
            case EffectType.Shield:
                _sim.Damage.AddShield(target, e.Shield, EffectAmount(e, source, target, scale), e.Duration, source?.Id ?? -1);
                break;
            case EffectType.RemoveShield:
                _sim.Damage.RemoveShields(target, e.ShieldName, cause);
                break;
            case EffectType.ModifyStat:
            {
                if (string.IsNullOrEmpty(e.Stat)) break;
                var mod = new StatModDef { Stat = e.Stat, Add = e.StatAdd * scale, Mul = e.StatMul * scale };
                long until = e.Duration > 0 ? _sim.Tick + _sim.TicksFor(e.Duration) : -1;
                target.StatChanges.Add((mod, until));
                _sim.Statuses.RecalculateModifiers(target);
                _sim.Emit(SimEventType.Info, LogLevel.Normal, $"{target.Name}: {_sim.Content.Stat(e.Stat)?.DisplayName ?? e.Stat} " +
                    $"{(mod.Add != 0 ? $"{mod.Add:+0.##;-0.##}" : "")}{(mod.Mul != 0 ? $" {mod.Mul:+0%;-0%}" : "")} " + (until < 0 ? "до конца боя" : $"на {e.Duration:0.#} с") + $" — {cause}",
                    source?.Id ?? -1, target.Id);
                break;
            }
            case EffectType.ModifyAccumulation:
            {
                var sd = _sim.Content.Status(e.Status);
                if (sd != null) _sim.Statuses.ModifyAccumulation(target, sd, e.Amount * scale, source?.Id ?? -1);
                break;
            }
            case EffectType.SetRoute:
            {
                var points = _sim.Scenario.RoutePoints(e.Route);
                if (points == null) break;
                _sim.Routes.Assign(target, e.Route, fromStart: false);
                if (_sim.Scenario.Def?.Route(e.Route) is { Target: not "" } sr) target.TargetObject = sr.Target;
                string rname = _sim.Scenario.Def?.Route(e.Route)?.DisplayName ?? _sim.Map.Def.FindRoute(e.Route)?.Name ?? e.Route;
                _sim.Emit(SimEventType.AiDecision, LogLevel.Detail, $"{target.Name}: новый маршрут «{rname}» — {cause}", source?.Id ?? -1, target.Id);
                break;
            }
            case EffectType.SetAiProfile:
            {
                var p = _sim.Content.Get<AiProfileDef>(e.AiProfile);
                if (p == null) break;
                target.AiProfile = p;
                _sim.Emit(SimEventType.AiDecision, LogLevel.Detail, $"{target.Name}: поведение «{p.DisplayName}» — {cause}", source?.Id ?? -1, target.Id);
                break;
            }
            case EffectType.ModifyAp:
            {
                double before = target.Ap;
                target.Ap = Math.Clamp(target.Ap + e.Amount * scale, 0, target.ApMax);
                if (Math.Abs(target.Ap - before) > 1e-9)
                    _sim.Emit(SimEventType.ResourceChanged, LogLevel.Normal, $"{target.Name}: ОД {target.Ap - before:+0.#;-0.#} — {cause}", source?.Id ?? -1, target.Id);
                break;
            }
        }
    }

    /// <summary>Operations without a target: message, visual/sound effect, scenario flag, scenario trigger.</summary>
    public void ApplyGlobal(SkillEffectDef e, Unit? source, HexCoord at, string cause)
    {
        switch (e.Type)
        {
            case EffectType.Message:
                _sim.Emit(SimEventType.Message, LogLevel.Normal, string.IsNullOrEmpty(e.Text) ? cause : e.Text, source?.Id ?? -1, cell: at);
                break;
            case EffectType.PlayVfx:
                _sim.Emit(SimEventType.Vfx, LogLevel.Diagnostic, e.Text, source?.Id ?? -1, cell: at);
                break;
            case EffectType.PlaySound:
                _sim.Emit(SimEventType.Sound, LogLevel.Diagnostic, e.Text, source?.Id ?? -1, cell: at);
                break;
            case EffectType.SetFlag:
                _sim.Scenario.SetFlag(e.Text);
                break;
            case EffectType.ActivateTrigger:
                _sim.Scenario.ActivateTrigger(e.Text, source);
                break;
        }
    }

    /// <summary>Push (away from origin) or pull (towards origin) a unit cell by cell; stops at obstacles.</summary>
    public void Displace(Unit target, HexCoord origin, bool push, int distance, Unit? source, string cause) =>
        Displace(target, origin, push, distance, source, cause, 0);

    private void Displace(Unit target, HexCoord origin, bool push, int distance, Unit? source, string cause, int depth)
    {
        if (target.IsStructure) return; // structures of the scenario do not move
        if (target.Pos == origin && !push) return;
        HexDirection? dir = push ? origin.DirectionTo(target.Pos) : target.Pos.DirectionTo(origin);
        if (dir == null) dir = push ? target.Facing.Opposite() : target.Facing;
        var start = target.Pos;
        var cur = target.Pos;
        var mc = MoveContext.For(_sim, target);
        int impulse = distance;
        distance = KnockbackHexes(target, distance);
        if (distance < impulse && depth == 0)
            _sim.Emit(SimEventType.Info, LogLevel.Detail, $"{target.Name}: масса {target.Stat(Stats.Weight):0.#} — сдвиг {distance} из {impulse}", source?.Id ?? -1, target.Id);
        var mv = Rules.Move;
        Unit? hit = null;
        int lost = 0;
        for (int i = 0; i < distance; i++)
        {
            var next = cur.Neighbor(dir.Value);
            if (!push && next == origin) break;
            var from = _sim.Map.Get(cur);
            var to = _sim.Map.Get(next);
            // Forced movement: ledges can be descended, pits can be entered (and the unit falls).
            var st = MovementRules.Check(mc, from, to, false, out _, forced: true);
            if (st == StepCheck.Occupied && to != null)
            {
                // A unit on the way (ТЗ ч. 5, 15.3): the collision policy of the rules.
                var blocker = to.Occupants.FirstOrDefault(o => o.Alive && o != target);
                if (blocker != null && mv.Collision == CollisionPolicy.ChainPush && depth < Math.Max(1, mv.ChainDepth))
                {
                    Displace(blocker, cur, true, distance - i, source, cause + " (цепочка)", depth + 1);
                    st = MovementRules.Check(mc, from, to, false, out _, forced: true);
                }
                else if (blocker != null && mv.Collision == CollisionPolicy.ImpactDamage)
                {
                    hit = blocker;
                    lost = distance - i;
                }
                else if (mv.Collision == CollisionPolicy.DisplaceIfLegal)
                {
                    foreach (var side in new[] { dir.Value.Rotate(1), dir.Value.Rotate(-1) })
                    {
                        var aside = cur.Neighbor(side);
                        if (MovementRules.Check(mc, from, _sim.Map.Get(aside), false, out _, forced: true) != StepCheck.Ok) continue;
                        cur = aside;
                        break;
                    }
                    break;
                }
            }
            if (st != StepCheck.Ok) break;
            cur = next;
            if (to != null && _sim.Falls.Affects(target, to)) break;
        }
        if (cur != start)
        {
            _sim.Commands.OnForcedMove(target);
            _sim.Emit(SimEventType.Displaced, LogLevel.Normal, $"{target.Name} {(push ? "отброшен" : "притянут")} в {cur} — {cause}", source?.Id ?? -1, target.Id, cur);
            if (!_sim.Relocate(target, cur, source, cause)) return;
            if (target.Casting != null && !target.Casting.Released && target.Casting.Skill.Interrupt.ByForcedMove)
                _sim.Skills.Interrupt(target, InterruptCause.ForcedMove, "перемещение");
            _sim.CellEffects.OnUnitEntered(target, _sim.Map.Get(cur)!);
        }
        // «Урон от удара»: both take the impact of the hexes the push did not pass.
        if (hit != null && lost > 0 && mv.ImpactDamage > 0)
            foreach (var u in new[] { target, hit })
                if (u.Alive)
                    _sim.Damage.Apply(new DamageRequest { Attacker = source, Target = u, Amount = mv.ImpactDamage * lost, Cause = "удар при толчке: " + cause });
    }

    private SafeExpression? KnockbackFormula()
    {
        var src = Rules.Move.KnockbackFormula;
        if (src != _knockFormulaSrc)
        {
            _knockFormulaSrc = src;
            _knockFormula = string.IsNullOrWhiteSpace(src) ? null :
                SafeExpression.TryParse(src, new[] { "impulse", "mass", "reference", "resist", "cost" }, out var e, out _) ? e : null;
        }
        return _knockFormula;
    }

    private string? _knockFormulaSrc;
    private SafeExpression? _knockFormula;

    /// <summary>
    /// The hexes a push or pull of <paramref name="impulse"/> moves the unit (ТЗ ч. 5, 15.2): the formula of the rules, or
    /// impulse × reference / mass (docs/DECISIONS_4.md §60); rounded down, from 0 to the impulse.
    /// </summary>
    public int KnockbackHexes(Unit target, int impulse)
    {
        double weight = target.Stat(Stats.Weight);
        var f = KnockbackFormula();
        if (f == null) return weight > 0 ? Math.Max(0, (int)Math.Floor(impulse * Rules.Move.ReferenceWeight / weight + 1e-9)) : impulse;
        double v = f.Evaluate(name => name switch
        {
            "impulse" => impulse,
            "mass" => weight,
            "reference" => Rules.Move.ReferenceWeight,
            "resist" => target.Stat(Stats.ControlResist),
            "cost" => _sim.Map.Get(target.Pos) is { } here ? (here.MoveCostOverride ?? here.Terrain.MoveCost) + here.EffectMoveCost : 1,
            _ => 0,
        });
        return Math.Clamp((int)Math.Floor(v + 1e-9), 0, impulse);
    }

    /// <summary>
    /// Direct effects without attack rolls (status ticks, cell effects, passives, synergies).
    /// Armor and resistances still apply to damage.
    /// </summary>
    /// <param name="kind">Damage source for the pipeline rules (periodic, cell effect, terrain, synergy, passive…).</param>
    /// <param name="fixedAmounts">Snapshot amounts by effect index (periodic effects with power fixed at application).</param>
    public void ApplyDirect(Unit? source, Unit target, IReadOnlyList<SkillEffectDef> effects, double scale, string cause,
        DamageSource kind = DamageSource.Other, Func<int, double?>? fixedAmounts = null)
    {
        if (!target.Alive) return;
        var origin = source != null && source.Alive ? source.Pos : target.Pos;
        for (int i = 0; i < effects.Count; i++)
        {
            var e = effects[i];
            if (!target.Alive) return;
            if (e.Chance < 1 && !_sim.Rng.Chance(e.Chance)) continue;
            double Amount() => fixedAmounts?.Invoke(i) * scale ?? EffectAmount(e, source, target, scale);
            if (e.IsGlobal)
            {
                ApplyGlobal(e, source, target.Pos, cause);
                continue;
            }
            switch (e.Type)
            {
                case EffectType.Damage:
                    DealDamage(source, target, Amount(), e.DamageType, null, cause, null, kind);
                    break;
                case EffectType.Heal:
                    Heal(source, target, Amount(), cause, e.HealMode);
                    break;
                default:
                    if (e.TargetsUnits) ApplyUnitEffect(e, source, target, origin, scale, cause);
                    break;
            }
        }
    }
}
