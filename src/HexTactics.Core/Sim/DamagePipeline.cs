using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HexTactics.Core.Content;

namespace HexTactics.Core.Sim;

/// <summary>One hit of damage entering the unified pipeline.</summary>
public sealed class DamageRequest
{
    public Unit? Attacker { get; init; }
    public Unit Target { get; init; } = null!;
    public double Amount { get; init; }
    public string DamageType { get; init; } = "physical";
    public DamageSource Source { get; init; } = DamageSource.Other;
    public SkillDef? Skill { get; init; }
    /// <summary>Attack roll of a skill/auto-attack (notice, hit, block, crit); null — the pipeline rolls by the source rules.</summary>
    public AttackRoll? Roll { get; init; }
    public string Cause { get; init; } = "";
    public int ReflectDepth { get; init; }
    public List<(Unit Target, bool Surprise)>? DeferredInterrupts { get; init; }
    /// <summary>Critical multiplier of the hit (a stage of a series); 0 — the attacker's stat.</summary>
    public double CritMultiplier { get; init; }
    /// <summary>Tag of the hit (a stage of a series) for passives and reactions.</summary>
    public string HitTag { get; init; } = "";
}

public sealed class DamagePart
{
    public string Type { get; set; } = "";
    public double Amount { get; set; }
    public double Percent { get; set; }
}

public sealed class DamageStep
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public double Value { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>Everything the pipeline computed — the admin breakdown (docs/DECISIONS_2.md §29, TZ 11.8).</summary>
public sealed class DamageResult
{
    public DamageSource Source { get; init; }
    public string DamageType { get; init; } = "";
    public double Raw { get; set; }
    public double Bonus { get; set; }
    public double CritMultiplier { get; set; } = 1;
    public bool Crit { get; set; }
    public bool Missed { get; set; }
    public bool Immune { get; set; }
    public double ModifierPercent { get; set; }
    public double Armor { get; set; }
    public double FlatReduction { get; set; }
    public double Mitigated { get; set; }
    public double Shielded { get; set; }
    public double Absorbed { get; set; }
    public double HealthLost { get; set; }
    public double Overkill { get; set; }
    public double Reflected { get; set; }
    /// <summary>Absorbed damage returned as health after the hit.</summary>
    public double AbsorbedToHealth { get; set; }
    public List<DamagePart> Parts { get; } = new();
    public List<DamageStep> Steps { get; } = new();
    public List<string> Effects { get; } = new();

    /// <summary>Total damage removed from health and shields (what the hit "did").</summary>
    public double Dealt => HealthLost + Overkill + Shielded;

    internal void Step(string name, double value, string note = "") =>
        Steps.Add(new DamageStep { Index = Steps.Count + 1, Name = name, Value = value, Note = note });

    /// <summary>One-line chain of values for compact views: "исх. 10 → сопр. 5 → броня 3 → HP 3".</summary>
    public string ShortBreakdown() => string.Join(" → ", Steps.Select(s => $"{Short(s.Name)} {s.Value.ToString("0.#", CultureInfo.InvariantCulture)}"));

    private static string Short(string step) => step switch
    {
        "исходный урон" => "исх.",
        "бонусы атакующего" => "бонусы",
        "бросок попадания" => "попадание %",
        "критический удар" => "крит",
        "сопротивления и уязвимости" => "сопр.",
        "броня, снижение, блок" => "броня",
        "щиты" => "щит",
        "поглощение" => "погл.",
        "потеря здоровья" => "HP",
        "отражение" => "отраж.",
        _ => step,
    };

    public string Breakdown()
    {
        var sb = new StringBuilder();
        foreach (var s in Steps)
            sb.Append(s.Index).Append(". ").Append(s.Name).Append(": ").Append(s.Value.ToString("0.##", CultureInfo.InvariantCulture))
              .Append(s.Note.Length > 0 ? " (" + s.Note + ")" : "").Append("; ");
        return sb.ToString().TrimEnd(' ', ';');
    }
}

/// <summary>
/// The single damage pipeline every source goes through (docs/DECISIONS_2.md §29):
/// 1 source/type/value → 2 target & immunity → 3 attacker bonuses → 4 hit → 5 crit →
/// 6 conversion, resistances & vulnerabilities D = D₀ × max(0, 1 + ΣV/100) → 7 armor, flat reduction, block →
/// 8 shields → 9 absorption → 10 health → 11 reflection → 12 statuses & reactions → 13 log.
/// Exceptions per source are the rules table <see cref="DamageRulesDef.Sources"/>.
/// </summary>
public sealed class DamagePipeline
{
    private readonly Simulation _sim;
    private int _nextShieldId = 1;

    public DamagePipeline(Simulation sim)
    {
        _sim = sim;
        Taxonomy = new DamageTaxonomy(sim.Content);
    }

    public DamageTaxonomy Taxonomy { get; }

    /// <summary>Last results (admin inspector), newest last.</summary>
    public List<(long Tick, int Attacker, int Target, DamageResult Result)> Recent { get; } = new();

    private RulesDef Rules => _sim.Rules;

    // ------------------------------------------------------------------ modifiers

    /// <summary>Sum of resistance/vulnerability modifiers (percent) of the target for a damage type.</summary>
    public double ModifierPercent(Unit target, string damageType, Unit? attacker = null)
    {
        // Passives and talents: their modifiers of this damage type, summed by their modes (docs/DECISIONS_3.md §46).
        double v = _sim.Passives.IncomingPercent(target, damageType, attacker);
        foreach (var d in target.Defenses)
            foreach (var m in d.Modifiers)
                if (Taxonomy.Matches(m.Match, damageType))
                    v += Math.Clamp(m.Percent, -500, 500);
        // Legacy stat resist_<type> (fraction, 0.5 = −50 %).
        v -= target.ResistStat(damageType) * 100.0;
        return v;
    }

    public bool IsImmune(Unit target, string damageType)
    {
        if (target.IsImmuneTo(damageType, Array.Empty<string>())) return true;
        if (_sim.Passives.ImmuneToDamage(target, damageType)) return true;
        foreach (var d in target.Defenses)
            foreach (var m in d.Immune)
                if (Taxonomy.Matches(m, damageType))
                    return true;
        return false;
    }

    private double OffenseBonus(Unit attacker, string damageType, DamageRequest q)
    {
        double v = _sim.Passives.OutgoingPercent(attacker, q.Target, damageType, q.Skill, q.HitTag);
        foreach (var o in attacker.Offenses)
            foreach (var m in o.Bonuses)
                if (Taxonomy.Matches(m.Match, damageType))
                    v += Math.Clamp(m.Percent, -500, 500);
        return v;
    }

    private static void Convert(List<DamagePart> parts, IEnumerable<ConversionDef> conversions, DamageTaxonomy tax)
    {
        foreach (var c in conversions)
        {
            if (string.IsNullOrEmpty(c.To) || c.Percent <= 0) continue;
            var add = new List<DamagePart>();
            foreach (var p in parts)
            {
                if (p.Type == c.To || !tax.Matches(c.From, p.Type)) continue;
                double moved = p.Amount * Math.Clamp(c.Percent, 0, 100) / 100.0;
                if (moved <= 0) continue;
                p.Amount -= moved;
                add.Add(new DamagePart { Type = c.To, Amount = moved });
            }
            foreach (var a in add)
            {
                var same = parts.FirstOrDefault(x => x.Type == a.Type);
                if (same != null) same.Amount += a.Amount;
                else parts.Add(a);
            }
            parts.RemoveAll(p => p.Amount <= 1e-9);
        }
    }

    /// <summary>Element interactions of the hit: partner statuses on the target / cell effects under it.</summary>
    private List<(ElementInteractionDef Def, StatusInstance? Status, CellEffectInstance? Cell)> Interactions(Unit target, string damageType)
    {
        var list = new List<(ElementInteractionDef, StatusInstance?, CellEffectInstance?)>();
        var el = Taxonomy.Element(Taxonomy.ElementOf(damageType));
        if (el == null || el.Interactions.Count == 0) return list;
        var cell = _sim.Map.Get(target.Pos);
        foreach (var i in el.Interactions)
        {
            if (string.IsNullOrEmpty(i.With)) continue;
            StatusInstance? st = null;
            CellEffectInstance? ce = null;
            if (i.Partner != InteractionPartner.CellEffect)
                st = target.Statuses.FirstOrDefault(s => Taxonomy.ElementIs(s.Def.Element, i.With));
            if (st == null && i.Partner != InteractionPartner.TargetStatus && cell != null)
                ce = cell.Effects.FirstOrDefault(e => Taxonomy.ElementIs(e.Def.Element, i.With));
            if (st != null || ce != null) list.Add((i, st, ce));
        }
        return list;
    }

    // ------------------------------------------------------------------ crit

    /// <summary>Crit chance of an attack: attacker stat, skill and rules bonuses (attack kind, sector), minus target protection.</summary>
    public double CritChance(Unit attacker, Unit target, SkillDef? skill, AttackSector sector, bool surprise, HitSpec? spec = null)
    {
        bool can = spec?.Crit switch
        {
            CritRule.Allowed => true,
            CritRule.Never => false,
            _ => skill == null || skill.CanCrit,
        };
        if (!can) return 0;
        if (target.Defenses.Any(d => d.CritImmune)) return 0;
        var c = Rules.Damage.Crit;
        double p = attacker.Stat(Stats.CritChance) + (skill?.CritChanceBonus ?? 0) + (spec?.CritBonus ?? 0) + (surprise ? Rules.Detection.SurpriseCrit : 0);
        var kind = spec?.Kind ?? StageAttackKind.Inherit;
        if (kind != StageAttackKind.Inherit)
            p += kind switch { StageAttackKind.Ranged => c.RangedBonus, StageAttackKind.Melee => c.MeleeBonus, _ => c.SpellBonus };
        else if (skill != null)
            p += skill.Kind == SkillKind.Attack ? (skill.Ranged ? c.RangedBonus : c.MeleeBonus) : skill.Kind == SkillKind.Spell ? c.SpellBonus : 0;
        p += sector switch
        {
            AttackSector.NearFlank or AttackSector.FarFlank => c.FlankBonus,
            AttackSector.Rear => c.RearBonus,
            _ => 0,
        };
        p -= target.Stat(Stats.CritResist);
        return Math.Clamp(p, 0, 1);
    }

    // ------------------------------------------------------------------ the pipeline

    public DamageResult Apply(DamageRequest q)
    {
        // Defensive stances act inside the pipeline (docs/DECISIONS_3.md §45).
        bool stance = q.Target.Stance != null;
        var r = ApplyCore(q, out var avoided);
        if (stance) _sim.Defense.AfterHit(q, r, avoided);
        return r;
    }

    private DamageResult ApplyCore(DamageRequest q, out DefenseSystem.Avoid avoided)
    {
        avoided = DefenseSystem.Avoid.None;
        var target = q.Target;
        var attacker = q.Attacker;
        var rule = Rules.Damage.For(q.Source);
        var r = new DamageResult { Source = q.Source, DamageType = q.DamageType };
        bool periodic = q.Source == DamageSource.Periodic;

        // 1. Source, type, value.
        double raw = Math.Max(0, q.Amount);
        r.Raw = raw;
        r.Step("исходный урон", raw, $"{TypeName(q.DamageType)}, источник: {Names(q.Source)}");
        if (raw <= 0 || !target.Alive) return r;

        // 2. Target and immunity.
        if (IsImmune(target, q.DamageType))
        {
            r.Immune = true;
            r.Step("иммунитет", 0);
            Finish(q, r, rule);
            return r;
        }

        // 3. Attacker and skill bonuses.
        var parts = new List<DamagePart> { new() { Type = q.DamageType, Amount = raw } };
        if (rule.AttackerBonuses && attacker != null && attacker != target)
        {
            Convert(parts, attacker.Offenses.SelectMany(o => o.Conversions), Taxonomy);
            double before = parts.Sum(p => p.Amount);
            foreach (var p in parts) p.Amount *= Math.Max(0, 1 + OffenseBonus(attacker, p.Type, q) / 100.0);
            if (q.Roll?.Surprise == true)
                foreach (var p in parts)
                    p.Amount *= Rules.Detection.SurpriseDamageMul;
            r.Bonus = parts.Sum(p => p.Amount) - before;
            r.Step("бонусы атакующего", parts.Sum(p => p.Amount), q.Roll?.Surprise == true ? "неожиданность" : "");
        }

        // 4. Hit and evasion (sources without their own roll, by the rules table).
        bool hit = q.Roll?.Hit ?? true;
        if (q.Roll == null && rule.RollHit && attacker != null && attacker != target)
        {
            double ph = _sim.Combat.HitChance(attacker, target, q.Skill, attacker.Pos, true, false);
            hit = _sim.Rng.Chance(ph);
            r.Step("бросок попадания", ph * 100, hit ? "попадание" : "промах");
        }
        if (!hit)
        {
            r.Missed = true;
            Finish(q, r, rule);
            return r;
        }
        if (target.Stance != null && (avoided = _sim.Defense.TryAvoid(q)) != DefenseSystem.Avoid.None)
        {
            r.Missed = true;
            r.Step("защитный навык", 0, avoided == DefenseSystem.Avoid.Dodge ? "уклонение" : "парирование");
            Finish(q, r, rule);
            return r;
        }

        // 5. Critical hit.
        bool crit = q.Roll?.Crit ?? false;
        if (q.Roll == null && rule.CanCrit && attacker != null && attacker != target)
        {
            double pc = CritChance(attacker, target, q.Skill, AttackSector.None, false);
            crit = pc > 0 && _sim.Rng.Chance(pc);
            if (crit) _sim.Reactions.Enqueue(PassiveTrigger.OnCrit, attacker, target);
        }
        if (crit && target.Defenses.Any(d => d.CritImmune)) crit = false;
        r.Crit = crit;
        if (crit && q.Skill?.CritNoExtraDamage != true && attacker != null)
        {
            double mult = q.CritMultiplier > 0 ? q.CritMultiplier : Math.Max(Rules.Damage.Crit.MinMultiplier, attacker.Stat(Stats.CritMult));
            double total = parts.Sum(p => p.Amount);
            double bonus = Math.Max(0, attacker.Stat(Stats.CritBonus));
            foreach (var p in parts) p.Amount = p.Amount * mult + (total > 0 ? bonus * p.Amount / total : 0);
            r.CritMultiplier = mult;
            r.Step("критический удар", parts.Sum(p => p.Amount), $"×{mult:0.##}" + (bonus > 0 ? $" +{bonus:0.#}" : ""));
        }

        // 6. Conversion, then resistances and vulnerabilities: D = D₀ × max(0, 1 + ΣV/100).
        Convert(parts, target.Defenses.SelectMany(d => d.Conversions), Taxonomy);
        var interactions = rule.ElementEffects ? Interactions(target, q.DamageType) : new();
        double interactionPercent = interactions.Sum(i => i.Def.DamagePercent);
        double beforeResist = parts.Sum(p => p.Amount);
        var dmf = Rules.Damage;
        foreach (var p in parts)
        {
            if (IsImmune(target, p.Type))
            {
                p.Percent = -100;
                p.Amount = 0;
                continue;
            }
            double v = ModifierPercent(target, p.Type, attacker) + interactionPercent;
            v = Math.Clamp(v, dmf.MinTotalPercent, dmf.MaxTotalPercent);
            p.Percent = v;
            p.Amount *= Math.Max(0, 1 + v / 100.0);
        }
        double afterResist = parts.Sum(p => p.Amount);
        r.ModifierPercent = beforeResist > 0 ? parts.Sum(p => p.Percent * (p.Amount + 1e-9)) / Math.Max(1e-9, parts.Sum(p => p.Amount + 1e-9)) : 0;
        if (parts.Count == 1) r.ModifierPercent = parts[0].Percent;
        r.Step("сопротивления и уязвимости", afterResist,
            string.Join(", ", parts.Select(p => $"{TypeName(p.Type)} {p.Percent:+0;-0;0} %")) + (interactionPercent != 0 ? $"; взаимодействие {interactionPercent:+0;-0} %" : ""));

        // 7. Armor, flat reduction, block (a custom formula replaces 5–7 if set in the rules).
        double dmg = afterResist;
        double armor = 0;
        if (rule.Armor)
        {
            double armored = parts.Where(p => _sim.Content.DamageType(p.Type)?.ArmorApplies == true).Sum(p => p.Amount);
            armor = armored > 0 ? Math.Max(0, target.Stat(Stats.Armor)) : 0;
            if (armor > 0)
            {
                double reduced = Math.Max(armored - armor, armored * Rules.Combat.MinArmorFraction);
                double k = armored > 0 ? reduced / armored : 1;
                foreach (var p in parts)
                    if (_sim.Content.DamageType(p.Type)?.ArmorApplies == true)
                        p.Amount *= k;
            }
        }
        double flat = 0;
        foreach (var p in parts)
        {
            double f = 0;
            foreach (var d in target.Defenses)
                foreach (var fr in d.FlatReductions)
                    if (Taxonomy.Matches(fr.Match, p.Type))
                        f += fr.Amount;
            if (p.Amount > 0 || f < 0) p.Amount = Math.Max(0, p.Amount - f);
            flat += f;
        }
        if (q.Roll?.Blocked == true)
            foreach (var p in parts)
                p.Amount *= 1.0 - Math.Clamp(target.Stat(Stats.BlockReduction), 0, 1);
        double stanceCut = target.Stance != null ? _sim.Defense.Reduce(q, parts, Taxonomy) : 0;
        dmg = parts.Sum(p => p.Amount);
        var formula = _sim.Combat.CustomDamage(beforeResist, armor, -r.ModifierPercent / 100.0, crit, q.Roll?.Surprise == true, q.Roll?.Blocked == true);
        if (formula.HasValue) dmg = Math.Max(0, formula.Value);
        double final = Math.Max(0, Math.Round(dmg, MidpointRounding.AwayFromZero));
        if (afterResist > 0 && final < Rules.Combat.MinDamageOnHit) final = Rules.Combat.MinDamageOnHit;
        Rescale(parts, final);
        r.Armor = armor;
        r.FlatReduction = flat;
        r.Mitigated = afterResist - final;
        r.Step("броня, снижение, блок", final,
            (armor > 0 ? $"броня {armor:0.#}" : "") + (flat != 0 ? $" снижение {flat:+0.#;-0.#}" : "") + (q.Roll?.Blocked == true ? " блок" : "") + (stanceCut > 0 ? $" защита −{stanceCut:0.#}" : "") + (formula.HasValue ? " формула правил" : ""));
        if (final <= 0)
        {
            r.Step("итог", 0, afterResist <= 0 ? "урон погашен модификаторами" : "");
            Finish(q, r, rule);
            return r;
        }

        // 8–9. Shields and absorption (absorption may be set to go first).
        double beforeShields = final;
        if (rule.Absorption) Absorb(q, r, parts, periodic, AbsorbOrder.BeforeShields);
        if (rule.Shields && q.Skill?.PiercesShields != true) Shields(q, r, parts, periodic);
        if (rule.Absorption) Absorb(q, r, parts, periodic, AbsorbOrder.AfterShields);

        // 10. Health loss.
        double remaining = parts.Sum(p => p.Amount);
        r.HealthLost = Math.Min(remaining, Math.Max(0, target.Hp));
        r.Overkill = remaining - r.HealthLost;
        target.Hp -= remaining;
        if (target.Indestructible && target.Hp < 1) target.Hp = 1;
        target.LastDamagedTick = _sim.Tick;
        if (r.HealthLost > 0) _sim.Passives.OnHealthLost(target, r.HealthLost, attacker);
        r.Step("потеря здоровья", remaining, r.Overkill > 0 ? $"избыток {r.Overkill:0}" : "");
        r.Parts.AddRange(parts);

        // 11. Reflection (depth-limited: reflected damage is not reflected again by default).
        if (rule.Reflection && attacker != null && attacker != target && attacker.Alive && q.ReflectDepth < Rules.Damage.ReflectMaxDepth)
            Reflect(q, r, beforeShields, periodic);

        Finish(q, r, rule, interactions);
        return r;
    }

    private static void Rescale(List<DamagePart> parts, double total)
    {
        double sum = parts.Sum(p => p.Amount);
        if (sum <= 1e-9)
        {
            if (parts.Count > 0) parts[0].Amount = total;
            return;
        }
        foreach (var p in parts) p.Amount *= total / sum;
    }

    private void Shields(DamageRequest q, DamageResult r, List<DamagePart> parts, bool periodic)
    {
        var target = q.Target;
        if (target.Shields.Count == 0) return;
        double absorbed = 0;
        foreach (var s in target.Shields.OrderByDescending(x => x.Spec.Priority).ThenBy(x => x.Id).ToList())
        {
            if (periodic && s.Spec.IgnoresPeriodic) continue;
            foreach (var p in parts)
            {
                if (s.Amount <= 0 || p.Amount <= 0) continue;
                if (_sim.Content.DamageType(p.Type)?.IgnoresShields == true) continue;
                if (!Taxonomy.MatchesAny(s.Spec.Protects, p.Type)) continue;
                double take = Math.Min(s.Amount, p.Amount);
                s.Amount -= take;
                p.Amount -= take;
                absorbed += take;
            }
            if (s.Amount <= 1e-9)
            {
                target.Shields.Remove(s);
                _sim.Emit(SimEventType.Info, LogLevel.Normal, $"{target.Name}: щит «{s.Spec.Name}» разрушен", q.Attacker?.Id ?? -1, target.Id);
            }
        }
        if (absorbed <= 0) return;
        r.Shielded += absorbed;
        r.Step("щиты", parts.Sum(p => p.Amount), $"щит принял {absorbed:0.#}");
    }

    private void Absorb(DamageRequest q, DamageResult r, List<DamagePart> parts, bool periodic, AbsorbOrder order)
    {
        var target = q.Target;
        double total = 0;
        foreach (var d in target.Defenses)
        foreach (var a in d.Absorptions)
        {
            if (a.Order != order || a.Percent <= 0) continue;
            if (periodic && !a.AffectsPeriodic) continue;
            double cap = a.CapPerHit > 0 ? a.CapPerHit : double.MaxValue;
            double took = 0;
            foreach (var p in parts)
            {
                if (p.Amount <= 0 || !Taxonomy.Matches(a.Match, p.Type)) continue;
                double x = Math.Min(p.Amount * Math.Clamp(a.Percent, 0, 100) / 100.0, cap - took);
                if (x <= 0) continue;
                p.Amount -= x;
                took += x;
            }
            if (took <= 0) continue;
            total += took;
            switch (a.Into)
            {
                case AbsorbInto.Health:
                    // Healed after the hit lands (health is restored from the absorbed part).
                    r.Effects.Add($"поглощение → +{took:0.#} HP");
                    r.AbsorbedToHealth += took;
                    break;
                case AbsorbInto.Resource:
                    if (target.Resources.TryGetValue(a.Resource, out var res))
                    {
                        res.Current = Math.Min(res.Max, res.Current + took);
                        r.Effects.Add($"поглощение → {res.Def.DisplayName} +{took:0.#}");
                    }
                    break;
            }
        }
        if (total <= 0) return;
        r.Absorbed += total;
        r.Step("поглощение", parts.Sum(p => p.Amount), $"поглощено {total:0.#}");
    }

    private void Reflect(DamageRequest q, DamageResult r, double beforeShields, bool periodic)
    {
        var target = q.Target;
        var attacker = q.Attacker!;
        foreach (var d in target.Defenses.ToList())
        foreach (var rf in d.Reflections)
        {
            if (rf.Percent <= 0 || !Taxonomy.Matches(rf.Match, q.DamageType)) continue;
            if (periodic && !rf.AffectsPeriodic) continue;
            if (rf.MeleeOnly && Hex.HexCoord.Distance(attacker.Pos, target.Pos) > 1) continue;
            double basis = rf.Base == ReflectBase.HealthLost ? r.HealthLost : beforeShields;
            double amount = basis * rf.Percent / 100.0;
            if (amount <= 0) continue;
            string type = rf.Type switch
            {
                ReflectType.Fixed when !string.IsNullOrEmpty(rf.FixedType) => rf.FixedType,
                ReflectType.Converted when r.Parts.Count > 0 => r.Parts.OrderByDescending(p => p.Amount).First().Type,
                _ => q.DamageType,
            };
            r.Reflected += amount;
            r.Step("отражение", amount, $"{TypeName(type)} → {attacker.Name}");
            Apply(new DamageRequest
            {
                Attacker = target, Target = attacker, Amount = amount, DamageType = type, Source = DamageSource.Reflection,
                Cause = $"отражение ({target.Name})", ReflectDepth = q.ReflectDepth + 1,
            });
        }
    }

    // ------------------------------------------------------------------ 12–13: effects and log

    private void Finish(DamageRequest q, DamageResult r, DamageSourceRuleDef rule,
        List<(ElementInteractionDef Def, StatusInstance? Status, CellEffectInstance? Cell)>? interactions = null)
    {
        var target = q.Target;
        var attacker = q.Attacker;
        string dtName = TypeName(q.DamageType);
        _sim.Knowledge.OnDamageObserved(attacker, target, q.DamageType);

        if (r.Missed) return;
        if (r.Immune)
        {
            _sim.Emit(SimEventType.Damage, LogLevel.Normal, $"{target.Name}: иммунитет к урону «{dtName}» ({q.Cause})", attacker?.Id ?? -1, target.Id, target.Pos, 0, q.DamageType);
            Record(q, r);
            return;
        }

        double lost = r.HealthLost + r.Overkill;
        // 13. Log: the main line, and the full breakdown for the admin view.
        var notes = new List<string> { dtName };
        if (r.Crit) notes.Add("крит");
        if (Math.Abs(r.ModifierPercent) > 0.5) notes.Add($"{(r.ModifierPercent < 0 ? "сопр." : "уязв.")} {r.ModifierPercent:+0;-0} %");
        if (r.Armor > 0) notes.Add($"броня {r.Armor:0.#}");
        if (r.Shielded > 0) notes.Add($"щит −{r.Shielded:0}");
        if (r.Absorbed > 0) notes.Add($"поглощено {r.Absorbed:0}");
        string detail = $"{lost:0} ({string.Join(", ", notes)})";
        if (lost > 0 || r.Shielded > 0)
            _sim.Emit(SimEventType.Damage, LogLevel.Normal,
                attacker != null && attacker != target ? $"{attacker.Name} наносит {target.Name} {detail} — {q.Cause}" : $"{target.Name} получает {detail} — {q.Cause}",
                attacker?.Id ?? -1, target.Id, target.Pos, lost, q.DamageType);
        else
            _sim.Emit(SimEventType.Damage, LogLevel.Detail, $"{target.Name}: урон «{dtName}» погашен ({q.Cause})", attacker?.Id ?? -1, target.Id, target.Pos, 0, q.DamageType);
        if (Rules.Damage.AdminBreakdown)
            _sim.Emit(SimEventType.Info, LogLevel.Diagnostic, $"Разбор урона {target.Name}: {r.Breakdown()}", attacker?.Id ?? -1, target.Id);

        if (r.AbsorbedToHealth > 0) _sim.Combat.Heal(null, target, r.AbsorbedToHealth, "поглощение урона", applyPower: false, overhealShield: false);

        // 12. Element statuses, interactions, crit effects, removal conditions, reactions.
        if (target.Alive && rule.ElementEffects && lost > 0) ElementEffects(q, r, interactions);
        if (lost > 0)
        {
            _sim.Statuses.OnDamaged(target, q.DamageType);
            _sim.Reactions.Enqueue(PassiveTrigger.OnDamaged, target, attacker);
        }
        Record(q, r);

        if (target.Hp > 0)
        {
            if (!target.CriticalReported && target.HpFraction < Rules.Autopause.CriticalHpFraction && _sim.IsPlayerFaction(target.FactionId))
            {
                target.CriticalReported = true;
                if (Rules.Autopause.AllyCriticalDamage) _sim.RequestAutopause($"{target.Name}: критическое здоровье");
            }
            if (lost > 0) _sim.Combat.OnDamagedAlive(target, attacker, q.Roll, q.DeferredInterrupts);
        }
        else
        {
            _sim.Combat.Kill(target, attacker, q.Cause);
        }
    }

    private void ElementEffects(DamageRequest q, DamageResult r, List<(ElementInteractionDef Def, StatusInstance? Status, CellEffectInstance? Cell)>? interactions)
    {
        var target = q.Target;
        var el = Taxonomy.Element(Taxonomy.ElementOf(q.DamageType));
        double lost = r.HealthLost + r.Overkill;
        if (el != null)
        {
            foreach (var s in el.Statuses)
            {
                var sd = _sim.Content.Status(s.Status);
                if (sd == null || (s.Chance < 1 && !_sim.Rng.Chance(s.Chance))) continue;
                double amount = s.PerDamage > 0 ? lost * s.PerDamage : s.Amount;
                if (_sim.Statuses.Apply(target, sd, amount, s.Duration, q.Attacker?.Id ?? -1)) r.Effects.Add(sd.DisplayName);
            }
            var cell = _sim.Map.Get(target.Pos);
            if (cell != null && el.CellEffects.Count > 0 && (el.CellEffectChance >= 1 || _sim.Rng.Chance(el.CellEffectChance)))
                foreach (var id in el.CellEffects)
                {
                    var ce = _sim.Content.CellEffect(id);
                    if (ce != null) _sim.CellEffects.Add(cell, ce, 1, 0, q.Attacker?.Id ?? -1, 0, 0, EffectOrigin.Element);
                }
        }
        if (interactions == null) return;
        foreach (var (i, st, cellEffect) in interactions)
        {
            if (!target.Alive) break;
            if (!string.IsNullOrEmpty(i.ApplyStatus) && _sim.Content.Status(i.ApplyStatus) is { } sd)
                _sim.Statuses.Apply(target, sd, i.StatusAmount, 0, q.Attacker?.Id ?? -1);
            var cell = _sim.Map.Get(target.Pos);
            if (!string.IsNullOrEmpty(i.CreateCellEffect) && cell != null && _sim.Content.CellEffect(i.CreateCellEffect) is { } ce)
                _sim.CellEffects.Add(cell, ce, 1, 0, q.Attacker?.Id ?? -1, 0, 0, EffectOrigin.Element);
            if (i.Consume)
            {
                if (st != null && target.Statuses.Contains(st)) _sim.Statuses.Remove(target, st, "взаимодействие элементов");
                if (cellEffect != null && cell != null) _sim.CellEffects.Remove(cell, cellEffect, "взаимодействие элементов");
            }
            string text = string.IsNullOrEmpty(i.LogText) ? $"взаимодействие: {el?.Name} + {_sim.Content.Element(i.With)?.DisplayName ?? i.With}" : i.LogText;
            r.Effects.Add(text);
            _sim.Emit(SimEventType.Info, LogLevel.Normal, $"{target.Name}: {text}", q.Attacker?.Id ?? -1, target.Id, target.Pos);
        }
    }

    private void Record(DamageRequest q, DamageResult r)
    {
        Recent.Add((_sim.Tick, q.Attacker?.Id ?? -1, q.Target.Id, r));
        if (Recent.Count > 64) Recent.RemoveAt(0);
    }

    // ------------------------------------------------------------------ shields

    public ShieldInstance? AddShield(Unit target, ShieldSpecDef spec, double amount, double duration, int source)
    {
        if (!target.Alive || amount <= 0) return null;
        amount *= Math.Max(0, 1 + target.Stat(Stats.ShieldPower));
        long expires = duration > 0 ? _sim.Tick + _sim.TicksFor(duration) : -1;
        var existing = target.Shields.FirstOrDefault(s => s.Spec.Name == spec.Name);
        if (existing != null && spec.Stacking != ShieldStacking.Separate)
        {
            switch (spec.Stacking)
            {
                case ShieldStacking.Add:
                    existing.Amount += amount;
                    existing.Max = Math.Max(existing.Max, existing.Amount);
                    break;
                case ShieldStacking.ReplaceIfStronger:
                    if (amount <= existing.Amount) return existing;
                    existing.Amount = existing.Max = amount;
                    break;
                default:
                    existing.Amount = Math.Max(existing.Amount, amount);
                    existing.Max = Math.Max(existing.Max, amount);
                    break;
            }
            existing.ExpiresTick = expires;
            _sim.Emit(SimEventType.Info, LogLevel.Normal, $"{target.Name}: щит «{spec.Name}» {existing.Amount:0}", source, target.Id);
            return existing;
        }
        var s = new ShieldInstance { Id = _nextShieldId++, Spec = spec, Amount = amount, Max = amount, ExpiresTick = expires, SourceUnit = source };
        target.Shields.Add(s);
        _sim.Emit(SimEventType.Info, LogLevel.Normal, $"{target.Name} получает щит «{spec.Name}» {amount:0}", source, target.Id);
        return s;
    }

    public void RemoveShields(Unit target, string name, string cause)
    {
        int n = target.Shields.RemoveAll(s => string.IsNullOrEmpty(name) || s.Spec.Name == name);
        if (n > 0) _sim.Emit(SimEventType.Info, LogLevel.Normal, $"{target.Name}: щиты сняты ({n}) — {cause}", -1, target.Id);
    }

    /// <summary>Per-tick: expiry and regeneration (after the delay since the last hit).</summary>
    public void UpdateShields(Unit u)
    {
        if (u.Shields.Count == 0) return;
        for (int i = u.Shields.Count - 1; i >= 0; i--)
        {
            var s = u.Shields[i];
            if (s.ExpiresTick >= 0 && _sim.Tick >= s.ExpiresTick)
            {
                u.Shields.RemoveAt(i);
                _sim.Emit(SimEventType.Info, LogLevel.Detail, $"{u.Name}: щит «{s.Spec.Name}» истёк", -1, u.Id);
                continue;
            }
            if (s.Spec.RegenPerSecond > 0 && s.Amount < s.Max && (u.LastDamagedTick < 0 || _sim.Tick - u.LastDamagedTick >= _sim.TicksFor(s.Spec.RegenDelay)))
                s.Amount = Math.Min(s.Max, s.Amount + s.Spec.RegenPerSecond * _sim.Dt);
        }
    }

    /// <summary>Readable lines of a unit's damage defenses and bonuses (unit panel, editor test view).</summary>
    public List<string> DefenseSummary(Unit u)
    {
        var lines = new List<string>();
        var mods = new Dictionary<string, double>();
        foreach (var d in u.Defenses)
            foreach (var m in d.Modifiers)
                mods[m.Match] = (mods.TryGetValue(m.Match, out var v) ? v : 0) + m.Percent;
        foreach (var dt in _sim.Content.All<DamageTypeDef>())
        {
            double st = -u.ResistStat(dt.Id) * 100;
            if (Math.Abs(st) > 0.5) mods[dt.Id] = (mods.TryGetValue(dt.Id, out var v) ? v : 0) + st;
        }
        var parts = mods.Where(kv => Math.Abs(kv.Value) > 0.5).Select(kv => $"{Taxonomy.NameOfMatch(kv.Key)} {kv.Value:+0;−0} %").ToList();
        if (parts.Count > 0) lines.Add("Модификаторы урона: " + string.Join(", ", parts));
        var immune = u.Defenses.SelectMany(d => d.Immune).Select(Taxonomy.NameOfMatch).ToList();
        if (immune.Count > 0) lines.Add("Иммунитет к урону: " + string.Join(", ", immune));
        var flat = u.Defenses.SelectMany(d => d.FlatReductions).Where(f => Math.Abs(f.Amount) > 1e-9)
            .Select(f => $"{Taxonomy.NameOfMatch(f.Match)} {(f.Amount > 0 ? "−" : "+")}{Math.Abs(f.Amount):0.#}").ToList();
        if (flat.Count > 0) lines.Add("Снижение за удар: " + string.Join(", ", flat));
        foreach (var c in u.Defenses.SelectMany(d => d.Conversions))
            lines.Add($"Преобразование: {c.Percent:0} % «{Taxonomy.NameOfMatch(c.From)}» → {Taxonomy.NameOfMatch(c.To)}");
        foreach (var a in u.Defenses.SelectMany(d => d.Absorptions))
            lines.Add($"Поглощение: {a.Percent:0} % «{Taxonomy.NameOfMatch(a.Match)}»" + (a.CapPerHit > 0 ? $" (до {a.CapPerHit:0} за удар)" : "")
                      + (a.Into == AbsorbInto.Health ? " → здоровье" : a.Into == AbsorbInto.Resource ? $" → {a.Resource}" : ""));
        foreach (var r in u.Defenses.SelectMany(d => d.Reflections))
            lines.Add($"Отражение: {r.Percent:0} % «{Taxonomy.NameOfMatch(r.Match)}»" + (r.MeleeOnly ? " (ближний бой)" : ""));
        if (u.Defenses.Any(d => d.CritImmune)) lines.Add("Иммунитет к критам");
        var bonuses = u.Offenses.SelectMany(o => o.Bonuses).Select(b => $"{Taxonomy.NameOfMatch(b.Match)} {b.Percent:+0;−0} %").ToList();
        if (bonuses.Count > 0) lines.Add("Бонусы урона: " + string.Join(", ", bonuses));
        foreach (var s in u.Shields) lines.Add($"Щит «{s.Spec.Name}»: {s.Amount:0}/{s.Max:0}" + (s.ExpiresTick >= 0 ? $" — {(s.ExpiresTick - _sim.Tick) * _sim.Dt:0.#} с" : ""));
        return lines;
    }

    // ------------------------------------------------------------------ names

    private string TypeName(string id) => _sim.Content.DamageType(id)?.DisplayName ?? id;

    private static string Names(DamageSource s) => s switch
    {
        DamageSource.Skill => "навык",
        DamageSource.AutoAttack => "автоатака",
        DamageSource.Periodic => "периодический",
        DamageSource.CellEffect => "эффект клетки",
        DamageSource.Terrain => "местность",
        DamageSource.Synergy => "синергия",
        DamageSource.Reflection => "отражение",
        DamageSource.Fall => "падение",
        DamageSource.Scenario => "сценарий",
        DamageSource.Passive => "пассивная способность",
        _ => "прочее",
    };
}
