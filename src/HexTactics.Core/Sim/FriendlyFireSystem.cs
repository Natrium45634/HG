using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Content;

namespace HexTactics.Core.Sim;

/// <summary>What an effect does to its target, for the rules of friendly fire (ТЗ 19.2: healing and buffs are not harm).</summary>
public enum FfCategory
{
    [Ed("урон")] Damage,
    [Ed("лечение")] Healing,
    [Ed("усиление")] Buff,
    [Ed("ослабление")] Debuff,
    [Ed("контроль")] Control,
    [Ed("прочее")] Other,
}

/// <summary>Who the target is to the source of an effect.</summary>
public enum FfRelation
{
    [Ed("сам")] Self,
    [Ed("свой призванный")] OwnSummon,
    [Ed("союзник")] Ally,
    [Ed("нейтрал")] Neutral,
    [Ed("враг")] Hostile,
    [Ed("окружение", "Поле без создателя, лава, падение")] Environment,
}

/// <summary>The rule in force for an impact, and where it comes from.</summary>
public sealed record FfPolicy(FriendlyFireDef Rule, string Source, FfRelationAt RelationAt);

/// <summary>
/// Friendly fire (ТЗ ч. 6, 19; docs/DECISIONS_6.md §105): the rule in force for a kind of impact is the most specific explicit one —
/// object, skill or effect of a cell, scenario, rules of the mode, battle rules; «наследовать» takes the level above. It is a gate on
/// top of the filter of the effect (a skill «только враги» stays so), except the explicit mode «попутный урон» of an area. Harm is
/// damage, debuffs and control; healing and buffs are never blocked by a rule of damage. Fields take the relations at the moment
/// they act (the creator's faction now) or at their creation, as the rule says; the environment belongs to nobody.
/// </summary>
public sealed class FriendlyFireSystem
{
    private readonly Simulation _sim;
    private FriendlyFireRulesDef? _preset, _scenario;

    public FriendlyFireSystem(Simulation sim) => _sim = sim;

    /// <summary>The levels of the battle: the rules of the mode and the scenario's own (set when the scenario starts).</summary>
    public void Load(ContentDatabase db, ScenarioDef sc)
    {
        _preset = sc.GameRules == "" ? null : db.Get<GameRulesDef>(sc.GameRules)?.FriendlyFireRules;
        _scenario = sc.GameRulesOverride?.FriendlyFireRules;
    }

    /// <summary>The old rule «Урон по союзникам» off: allies are spared damage in areas, nothing else changes.</summary>
    public static readonly FriendlyFireDef LegacyNoAllyDamage = new()
    {
        Targets = FfTargets.Custom, Neutrals = true, Self = true, AlliesDamage = false, AlliesDebuffs = true, AlliesControl = true, AlliesHealing = true,
        AlliesBuffs = true, OwnSummons = true,
    };

    public static readonly FriendlyFireDef Everyone = new() { Targets = FfTargets.AllIncludingSource };

    /// <summary>The rule in force (static: the editor previews with the same resolution).</summary>
    public static FfPolicy Resolve(RulesDef rules, FriendlyFireRulesDef? preset, FriendlyFireRulesDef? scenario, FfKind kind, params (FriendlyFireDef? Rule, string Source)[] specific)
    {
        var chain = specific.Concat(new[]
        {
            (scenario?.Of(kind), "сценарий"), (preset?.Of(kind), "правила режима"), (rules.Combat.FriendlyFireRules?.Of(kind), "правила боя"),
        }).ToList();
        var at = chain.Select(x => x.Item1?.RelationAt ?? FfRelationAt.Inherit).FirstOrDefault(r => r != FfRelationAt.Inherit);
        if (at == FfRelationAt.Inherit) at = FfRelationAt.Live;
        foreach (var (rule, source) in chain)
            if (rule != null && rule.Targets != FfTargets.Inherit)
                return new FfPolicy(rule, source, at);
        // Nothing explicit: the old switch for areas, the filters of the effects for the rest.
        if (kind == FfKind.Area && !rules.Combat.FriendlyFire) return new FfPolicy(LegacyNoAllyDamage, "«Урон по союзникам» выключен", at);
        return new FfPolicy(Everyone, "по фильтру эффекта", at);
    }

    public FfPolicy Policy(FfKind kind, params (FriendlyFireDef? Rule, string Source)[] specific) => Resolve(_sim.Rules, _preset, _scenario, kind, specific);

    /// <summary>What an effect does: harm (damage, debuff, control), help (healing, buff) or something else.</summary>
    public static FfCategory CategoryOf(SkillEffectDef e, ContentDatabase db)
    {
        switch (e.Type)
        {
            case EffectType.Damage: return FfCategory.Damage;
            case EffectType.Heal:
            case EffectType.Shield: return FfCategory.Healing;
            case EffectType.RemoveShield: return FfCategory.Debuff;
            case EffectType.Push:
            case EffectType.Pull:
            case EffectType.ChangeFacing: return FfCategory.Control;
            case EffectType.ModifyResource:
            case EffectType.ModifyAp:
            case EffectType.ChangeScale: return e.Amount < 0 ? FfCategory.Debuff : FfCategory.Buff;
            case EffectType.ModifyStat: return e.StatAdd < 0 || e.StatMul < 0 ? FfCategory.Debuff : FfCategory.Buff;
            case EffectType.ApplyStatus:
            case EffectType.ModifyAccumulation:
            {
                var sd = db.Status(e.Status);
                if (sd == null) return FfCategory.Other;
                bool control = sd.Levels.Any(l => l.Flags.BlocksActions || l.Flags.BlocksMovement || l.Flags.BlocksSkills || l.Flags.Fear || l.Flags.MindControl || l.Flags.Panic);
                bool harm = sd.Negative == (e.Type != EffectType.ModifyAccumulation || e.Amount >= 0);
                return !harm ? FfCategory.Buff : control ? FfCategory.Control : FfCategory.Debuff;
            }
            case EffectType.RemoveStatus:
                return db.Status(e.Status) is { Negative: true } ? FfCategory.Buff : FfCategory.Debuff;
            default: return FfCategory.Other;
        }
    }

    public static bool IsHarm(FfCategory c) => c is FfCategory.Damage or FfCategory.Debuff or FfCategory.Control;

    /// <summary>Who the target is to a source unit, or to a faction (a field whose creator is gone, «при создании»).</summary>
    public FfRelation RelationOf(Unit? source, string sourceFaction, Unit target)
    {
        if (source != null && source == target) return FfRelation.Self;
        if (source != null && target.SummonerId == source.Id) return FfRelation.OwnSummon;
        if (sourceFaction == "") return FfRelation.Environment;
        return _sim.Content.RelationOf(sourceFaction, target.FactionId) switch
        {
            Relation.Ally => FfRelation.Ally,
            Relation.Enemy => FfRelation.Hostile,
            _ => FfRelation.Neutral,
        };
    }

    /// <summary>Does a rule let an effect of this category reach a target of this relation.</summary>
    public static bool Permits(FriendlyFireDef r, FfCategory cat, FfRelation rel, Unit target)
    {
        if (rel == FfRelation.Environment) return true; // lava belongs to nobody: it is no «enemy» and no «friend»
        if (!IsHarm(cat))
        {
            if (r.Targets != FfTargets.Custom || rel is not (FfRelation.Ally or FfRelation.OwnSummon)) return true;
            return cat == FfCategory.Healing ? r.AlliesHealing : cat == FfCategory.Buff ? r.AlliesBuffs : true;
        }
        switch (r.Targets)
        {
            case FfTargets.HostileOnly: return rel == FfRelation.Hostile;
            case FfTargets.HostileAndNeutral: return rel is FfRelation.Hostile or FfRelation.Neutral;
            case FfTargets.AllExceptSource: return rel != FfRelation.Self;
            case FfTargets.AllIncludingSource: return true;
        }
        if (r.IgnoreTags.Any(target.HasTag)) return false;
        return rel switch
        {
            FfRelation.Hostile => true,
            FfRelation.Neutral => r.Neutrals,
            FfRelation.Self => r.Self,
            FfRelation.OwnSummon => r.OwnSummons,
            _ => cat switch
            {
                FfCategory.Damage => r.AlliesDamage,
                FfCategory.Debuff => r.AlliesDebuffs,
                _ => r.AlliesControl,
            },
        };
    }

    /// <summary>
    /// The gate of an effect of a skill (direct or area): the filter of the effect, or the explicit «попутный урон» of an area;
    /// then the rule of friendly fire. Relations are those of now.
    /// </summary>
    public bool Allows(FfKind kind, SkillDef? skill, SkillEffectDef e, Unit caster, Unit target, FriendlyFireDef? objectRule = null)
    {
        var policy = Policy(kind, (objectRule, "объект"), (skill?.FriendlyFire, "навык"));
        var cat = CategoryOf(e, _sim.Content);
        if (!_sim.FilterAccepts(e.Filter, caster, target) && !(kind == FfKind.Area && policy.Rule.Collateral && IsHarm(cat) && target != caster)) return false;
        return Permits(policy.Rule, cat, RelationOf(caster, caster.FactionId, target), target);
    }

    /// <summary>
    /// The gate of a field (enter, stay, exit, neighbourhood): the rule of the field, then the levels above; the faction of its creator
    /// now (dead — the one stored) or at creation, as the rule says. A field without a creator is the environment.
    /// </summary>
    public bool AllowsField(CellEffectInstance field, SkillEffectDef e, Unit target)
    {
        var policy = Policy(FfKind.Field, (field.Def.FriendlyFire, "поле"));
        var owner = field.SourceUnit >= 0 ? _sim.GetUnit(field.SourceUnit) : null;
        string faction = policy.RelationAt == FfRelationAt.Creation || owner is not { Alive: true } ? field.SourceFaction : owner.FactionId;
        if (owner == null && faction == "") return true;
        return Permits(policy.Rule, CategoryOf(e, _sim.Content), RelationOf(owner, faction, target), target);
    }

    /// <summary>
    /// The editor's preview (ТЗ 19.5): for every relation, what each effect does to it and why — by the filter of the effect and
    /// by the rule in force (and the level it comes from).
    /// </summary>
    public static List<string> Preview(ContentDatabase db, ScenarioDef? sc, FfKind kind, IReadOnlyList<SkillEffectDef> effects, params (FriendlyFireDef? Rule, string Source)[] specific)
    {
        var preset = sc == null || sc.GameRules == "" ? null : db.Get<GameRulesDef>(sc.GameRules)?.FriendlyFireRules;
        var policy = Resolve(db.Rules, preset, sc?.GameRulesOverride?.FriendlyFireRules, kind, specific);
        var lines = new List<string> { $"Правило ({EdUtil.EnumLabel(kind).ToLowerInvariant()}): {policy.Rule} — {policy.Source}" };
        var probe = new Unit(-1, new UnitDef { Id = "__probe" }, db);
        foreach (var rel in new[] { FfRelation.Hostile, FfRelation.Neutral, FfRelation.Ally, FfRelation.OwnSummon, FfRelation.Self })
        {
            var parts = new List<string>();
            foreach (var e in effects.Where(x => x.TargetsUnits))
            {
                var cat = CategoryOf(e, db);
                bool filter = kind == FfKind.Field || rel switch
                {
                    FfRelation.Hostile => e.Filter.Enemies,
                    FfRelation.Neutral => e.Filter.Neutrals,
                    FfRelation.Self => e.Filter.Self,
                    _ => e.Filter.Allies,
                } || kind == FfKind.Area && policy.Rule.Collateral && IsHarm(cat) && rel != FfRelation.Self;
                bool ok = filter && Permits(policy.Rule, cat, rel, probe);
                bool status = e.Type is EffectType.ApplyStatus or EffectType.RemoveStatus or EffectType.ModifyAccumulation;
                parts.Add($"{EdUtil.EnumLabel(cat)}{(status ? $" «{db.Status(e.Status)?.DisplayName ?? e.Status}»" : "")} — " +
                          (ok ? "да" : !filter ? "нет (фильтр эффекта)" : "нет (правило)"));
            }
            if (parts.Count > 0) lines.Add($"{EdUtil.EnumLabel(rel)}: {string.Join("; ", parts.Distinct())}");
        }
        return lines;
    }
}
