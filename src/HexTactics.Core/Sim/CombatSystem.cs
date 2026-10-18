using System;
using System.Collections.Generic;
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

public sealed class DamageBreakdown
{
    public double Raw { get; init; }
    public double AfterMultipliers { get; init; }
    public double Armor { get; init; }
    public double Resist { get; init; }
    public double Final { get; init; }
    public bool Immune { get; init; }
}

/// <summary>Combat math and the generic unit-effect executor used by skills, statuses, cells and passives.</summary>
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

    public double NoticeChance(Unit attacker, Unit target, HexCoord attackerPos, out AttackSector sector, out bool sees)
    {
        var det = Rules.Detection;
        sector = SectorOf(target.Pos, target.Facing, attackerPos);
        var sr = det.For(sector);
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

    public double SurpriseChance(AttackSector sector, bool targetSeesAttacker)
    {
        var det = Rules.Detection;
        double s = det.For(sector).SurpriseChance + (targetSeesAttacker ? 0 : det.SurpriseBonusVsUnaware);
        return Math.Clamp(s, 0, 1);
    }

    public double HitChance(Unit attacker, Unit target, SkillDef? skill, HexCoord attackerPos, bool noticed, bool surprise)
    {
        var c = Rules.Combat;
        var det = Rules.Detection;
        double acc = attacker.Stat(Stats.Accuracy);
        double eva = target.Stat(Stats.Evasion) * (noticed ? 1.0 : det.UnnoticedEvasionFactor);
        if (target.Flags.BlocksActions) eva = 0;
        var ac = _sim.Map.Get(attackerPos);
        var tc = _sim.Map.Get(target.Pos);
        double height = ((ac?.Height ?? 0) - (tc?.Height ?? 0)) * Rules.Height.AccuracyPerLevel;
        double sur = surprise ? det.SurpriseAccuracy : 0;
        int dist = HexCoord.Distance(attackerPos, target.Pos);
        double cover = skill != null && skill.Ranged && dist > 1 ? (tc?.Cover ?? 0) * c.CoverFactor : 0;
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
    public AttackRoll Roll(Unit attacker, Unit target, SkillDef? skill, HexCoord attackerPos)
    {
        double pNotice = NoticeChance(attacker, target, attackerPos, out var sector, out var sees);
        double pSurprise = SurpriseChance(sector, sees);
        double u = _sim.Rng.NextDouble();
        bool noticed = u < pNotice;
        bool surprise = !noticed && u >= 1.0 - pSurprise;

        double pHit = HitChance(attacker, target, skill, attackerPos, noticed, surprise);
        bool hit = skill?.HitMode == HitMode.AlwaysHit || _sim.Rng.Chance(pHit);
        if (skill?.HitMode == HitMode.AlwaysHit) pHit = 1.0;

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
            pCrit = Math.Clamp(attacker.Stat(Stats.CritChance) + (surprise ? Rules.Detection.SurpriseCrit : 0), 0, 1);
            if (pCrit > 0) crit = _sim.Rng.Chance(pCrit);
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
        if (noticed) _sim.Reactions.Enqueue(PassiveTrigger.OnAttackNoticed, target, attacker);
        if (hit) _sim.Reactions.Enqueue(PassiveTrigger.OnHitEnemy, attacker, target);
        return roll;
    }

    // ------------------------------------------------------------------ damage & healing

    public DamageBreakdown ComputeDamage(Unit? attacker, Unit target, double raw, string damageType, AttackRoll? roll)
    {
        double mult = 1.0;
        bool crit = roll?.Crit == true, surprise = roll?.Surprise == true, block = roll?.Blocked == true;
        if (crit) mult *= attacker?.Stat(Stats.CritMult) ?? 1.5;
        if (surprise) mult *= Rules.Detection.SurpriseDamageMul;
        if (block) mult *= 1.0 - Math.Clamp(target.Stat(Stats.BlockReduction), 0, 1);
        double dmg = raw * mult;
        var dt = _sim.Content.Get<DamageTypeDef>(damageType);
        double armor = dt?.ArmorApplies == true ? Math.Max(0, target.Stat(Stats.Armor)) : 0;
        double resist = target.Resist(damageType);
        double before = dmg;
        var f = DamageFormula();
        if (f != null)
        {
            dmg = f.Evaluate(name => name switch
            {
                "raw" => before,
                "armor" => armor,
                "resist" => resist,
                "crit" => crit ? 1 : 0,
                "surprise" => surprise ? 1 : 0,
                "block" => block ? 1 : 0,
                _ => 0,
            });
        }
        else
        {
            if (armor > 0) dmg = Math.Max(dmg - armor, dmg * Rules.Combat.MinArmorFraction);
            dmg *= 1.0 - resist;
        }
        bool immune = resist >= 1.0 || target.IsImmuneTo(damageType, Array.Empty<string>());
        double final = immune ? 0 : Math.Max(0, Math.Round(dmg, MidpointRounding.AwayFromZero));
        if (!immune && before > 0 && final < Rules.Combat.MinDamageOnHit) final = Rules.Combat.MinDamageOnHit;
        return new DamageBreakdown { Raw = raw, AfterMultipliers = before, Armor = armor, Resist = resist, Final = final, Immune = immune };
    }

    /// <summary>
    /// Deals damage. Interrupt checks of the target's preparation happen here unless
    /// deferredInterrupts is given (skill resolution checks them in its own phase).
    /// Returns the damage actually dealt.
    /// </summary>
    public double DealDamage(Unit? source, Unit target, double raw, string damageType, AttackRoll? roll, string cause,
        List<(Unit Target, bool Surprise)>? deferredInterrupts = null)
    {
        if (!target.Alive) return 0;
        var b = ComputeDamage(source, target, raw, damageType, roll);
        var dtName = _sim.Content.Get<DamageTypeDef>(damageType)?.DisplayName ?? damageType;
        if (b.Immune)
        {
            _sim.Emit(SimEventType.Damage, LogLevel.Normal, $"{target.Name}: иммунитет к урону «{dtName}» ({cause})", source?.Id ?? -1, target.Id, target.Pos, 0, damageType);
            return 0;
        }
        double dmg = b.Final;
        if (dmg <= 0) return 0;
        target.Hp -= dmg;
        string detail = $"{dmg:0} ({dtName}{(b.Armor > 0 ? $", броня {b.Armor:0.#}" : "")}{(b.Resist != 0 ? $", сопр. {b.Resist:P0}" : "")})";
        _sim.Emit(SimEventType.Damage, LogLevel.Normal,
            source != null && source != target ? $"{source.Name} наносит {target.Name} {detail} — {cause}" : $"{target.Name} получает {detail} — {cause}",
            source?.Id ?? -1, target.Id, target.Pos, dmg, damageType);

        _sim.Reactions.Enqueue(PassiveTrigger.OnDamaged, target, source);

        if (target.Hp > 0)
        {
            if (!target.CriticalReported && target.HpFraction < Rules.Autopause.CriticalHpFraction && _sim.IsPlayerFaction(target.FactionId))
            {
                target.CriticalReported = true;
                if (Rules.Autopause.AllyCriticalDamage) _sim.RequestAutopause($"{target.Name}: критическое здоровье");
            }
            CheckLowHpPassives(target, source);
            if (target.Casting != null && !target.Casting.Released)
            {
                if (deferredInterrupts != null) deferredInterrupts.Add((target, roll?.Surprise == true));
                else TryDamageInterrupt(target, roll?.Surprise == true);
            }
        }
        else
        {
            Kill(target, source, cause);
        }
        return dmg;
    }

    private void CheckLowHpPassives(Unit u, Unit? source)
    {
        foreach (var p in u.Passives)
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

    public double Heal(Unit? source, Unit target, double amount, string cause)
    {
        if (!target.Alive || amount <= 0) return 0;
        double before = target.Hp;
        target.Hp = Math.Min(target.HpMax, target.Hp + amount);
        double healed = target.Hp - before;
        if (target.HpFraction >= Rules.Autopause.CriticalHpFraction) target.CriticalReported = false;
        if (healed > 0)
            _sim.Emit(SimEventType.Heal, LogLevel.Normal, $"{target.Name} восстанавливает {healed:0} HP — {cause}", source?.Id ?? -1, target.Id, target.Pos, healed);
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
        _sim.Map.Touch(false);
        _sim.Vision.MarkDirty(urgent: true);

        _sim.Emit(SimEventType.Death, LogLevel.Normal, killer != null && killer != u ? $"{u.Name} погибает от руки {killer.Name} ({cause})" : $"{u.Name} погибает ({cause})",
            killer?.Id ?? -1, u.Id, u.Pos);
        if (Rules.Autopause.UnitDied && (_sim.IsPlayerFaction(u.BaseFactionId) || _sim.Vision.IsVisibleToPlayer(u) || _sim.Options.AdminMode))
            _sim.RequestAutopause($"{u.Name} погиб");

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

    /// <summary>Push (away from origin) or pull (towards origin) a unit cell by cell; stops at obstacles.</summary>
    public void Displace(Unit target, HexCoord origin, bool push, int distance, Unit? source, string cause)
    {
        if (target.Pos == origin && !push) return;
        HexDirection? dir = push ? origin.DirectionTo(target.Pos) : target.Pos.DirectionTo(origin);
        if (dir == null) dir = push ? target.Facing.Opposite() : target.Facing;
        var start = target.Pos;
        var cur = target.Pos;
        var mc = MoveContext.For(_sim, target);
        for (int i = 0; i < distance; i++)
        {
            var next = cur.Neighbor(dir.Value);
            if (!push && next == origin) break;
            var from = _sim.Map.Get(cur);
            var to = _sim.Map.Get(next);
            // Forced movement: ledges can be descended, pits can be entered (and the unit falls).
            var st = MovementRules.Check(mc, from, to, false, out _, forced: true);
            if (st != StepCheck.Ok) break;
            cur = next;
            if (to != null && _sim.Falls.Affects(target, to)) break;
        }
        if (cur == start) return;
        _sim.Commands.OnForcedMove(target);
        _sim.Emit(SimEventType.Displaced, LogLevel.Normal, $"{target.Name} {(push ? "отброшен" : "притянут")} в {cur} — {cause}", source?.Id ?? -1, target.Id, cur);
        if (!_sim.Relocate(target, cur, source, cause)) return;
        if (target.Casting != null && !target.Casting.Released && target.Casting.Skill.Interrupt.ByForcedMove)
            _sim.Skills.Interrupt(target, InterruptCause.ForcedMove, "перемещение");
        _sim.CellEffects.OnUnitEntered(target, _sim.Map.Get(cur)!);
    }

    /// <summary>
    /// Direct effects without attack rolls (status ticks, cell effects, passives, synergies).
    /// Armor and resistances still apply to damage.
    /// </summary>
    public void ApplyDirect(Unit? source, Unit target, IReadOnlyList<SkillEffectDef> effects, double scale, string cause)
    {
        if (!target.Alive) return;
        var origin = source != null && source.Alive ? source.Pos : target.Pos;
        foreach (var e in effects)
        {
            if (!target.Alive) return;
            if (e.Chance < 1 && !_sim.Rng.Chance(e.Chance)) continue;
            switch (e.Type)
            {
                case EffectType.Damage:
                    DealDamage(source, target, EffectAmount(e, source, target, scale), e.DamageType, null, cause);
                    break;
                case EffectType.Heal:
                    Heal(source, target, EffectAmount(e, source, target, scale) * (1.0 + (source?.Stat(Stats.HealPower) ?? 0)), cause);
                    break;
                default:
                    if (e.TargetsUnits) ApplyUnitEffect(e, source, target, origin, scale, cause);
                    break;
            }
        }
    }
}
