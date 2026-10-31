using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace HexTactics.Core.Content;

public enum UnitTab
{
    Base,
    Movement,
    Ai,
    Tags,
    Passives,
    Skills,
    Visual,
    Rating,
    // Part 6, stage 6.7 (§102).
    Boss,
}

/// <summary>
/// The tabs of the unit editor (ТЗ ч. 5, 25.3; docs/DECISIONS_5.md §89): Base Stats / Movement / AI / Tags / Passive Rules /
/// Active Skills / Visual / Level &amp; Rating. Every field of the template is on exactly one tab; a field added later and
/// not placed lands on the first one, so nothing is ever hidden.
/// </summary>
public static class UnitEditorTabs
{
    public static readonly (UnitTab Tab, string Title)[] All =
    {
        (UnitTab.Base, "Характеристики"),
        (UnitTab.Movement, "Движение"),
        (UnitTab.Ai, "ИИ"),
        (UnitTab.Tags, "Теги и категории"),
        (UnitTab.Passives, "Пассивы"),
        (UnitTab.Skills, "Навыки"),
        (UnitTab.Visual, "Вид"),
        (UnitTab.Rating, "Уровень и рейтинг"),
        (UnitTab.Boss, "Босс и фазы"),
    };

    private static readonly Dictionary<string, UnitTab> Placed = new()
    {
        [nameof(UnitDef.MoveCategory)] = UnitTab.Movement,
        [nameof(UnitDef.MoveTypes)] = UnitTab.Movement,
        [nameof(UnitDef.Layer)] = UnitTab.Movement,
        [nameof(UnitDef.Stacking)] = UnitTab.Movement,
        [nameof(UnitDef.Blocking)] = UnitTab.Movement,
        [nameof(UnitDef.Blockable)] = UnitTab.Movement,
        [nameof(UnitDef.AiProfile)] = UnitTab.Ai,
        [nameof(UnitDef.Role)] = UnitTab.Ai,
        [nameof(UnitDef.Perception)] = UnitTab.Ai,
        [nameof(UnitDef.Info)] = UnitTab.Ai,
        [nameof(UnitDef.Scale)] = UnitTab.Ai,
        [nameof(UnitDef.Boss)] = UnitTab.Boss,
        [nameof(Def.Tags)] = UnitTab.Tags,
        [nameof(Def.Group)] = UnitTab.Tags,
        [nameof(UnitDef.CreatureCategory)] = UnitTab.Tags,
        [nameof(UnitDef.MainCategory)] = UnitTab.Tags,
        [nameof(UnitDef.Origins)] = UnitTab.Tags,
        [nameof(UnitDef.Passives)] = UnitTab.Passives,
        [nameof(UnitDef.StartingStatuses)] = UnitTab.Passives,
        [nameof(UnitDef.BasicAttack)] = UnitTab.Skills,
        [nameof(UnitDef.Skills)] = UnitTab.Skills,
        [nameof(UnitDef.DefenseSkill)] = UnitTab.Skills,
        [nameof(UnitDef.AutoAttacks)] = UnitTab.Skills,
        [nameof(UnitDef.Resources)] = UnitTab.Skills,
        [nameof(UnitDef.Visual)] = UnitTab.Visual,
        [nameof(UnitDef.SpawnWarning)] = UnitTab.Visual,
        [nameof(UnitDef.ThreatCost)] = UnitTab.Rating,
        [nameof(UnitDef.Rank)] = UnitTab.Rating,
        [nameof(UnitDef.CustomRank)] = UnitTab.Rating,
        [nameof(UnitDef.PowerRole)] = UnitTab.Rating,
        [nameof(UnitDef.Build)] = UnitTab.Rating,
        [nameof(UnitDef.ScalingProfile)] = UnitTab.Rating,
        [nameof(UnitDef.Deployment)] = UnitTab.Rating,
    };

    /// <summary>The tab of a field of the template (by its property name).</summary>
    public static UnitTab Of(string property) => Placed.GetValueOrDefault(property, UnitTab.Base);

    /// <summary>The fields of the editor (with [Ed], not hidden) — those the tabs share out.</summary>
    public static IEnumerable<string> Fields() => typeof(UnitDef).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetCustomAttribute<EdAttribute>() != null && p.GetCustomAttribute<EdHiddenAttribute>() == null && p.GetIndexParameters().Length == 0)
        .Select(p => p.Name);

    /// <summary>The fields not on <paramref name="tab"/> — what its grid skips.</summary>
    public static HashSet<string> SkipFor(UnitTab tab) => Fields().Where(f => Of(f) != tab).ToHashSet();
}
