using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace HexTactics.Core.Content;

// Passives and talents (docs/DECISIONS_3.md §46): one mechanism of conditions, modifiers and effects.

public enum PassiveKind
{
    [Ed("Пассивное умение")] Passive,
    [Ed("Талант")] Talent,
}

/// <summary>Where the passive comes from — a label and a group for the author (equipment, artifacts and levels do not exist as systems).</summary>
public enum PassiveSource
{
    [Ed("Раса")] Race,
    [Ed("Вид существа")] Species,
    [Ed("Класс или роль")] ClassRole,
    [Ed("Конкретный юнит")] Unit,
    [Ed("Экипировка")] Equipment,
    [Ed("Артефакт")] Artifact,
    [Ed("Уровень развития")] Level,
    [Ed("Состояние")] Status,
    [Ed("Тип гекса")] Terrain,
    [Ed("Аура союзника")] AllyAura,
    [Ed("Навык")] Skill,
    [Ed("Сценарное правило")] Scenario,
    [Ed("Пользовательский тег")] CustomTag,
}

public enum ModifierTarget
{
    [Ed("Характеристика")] Stat,
    [Ed("Максимум ресурса")] ResourceMax,
    [Ed("Восстановление ресурса")] ResourceRegen,
    [Ed("Входящий урон")] IncomingDamage,
    [Ed("Наносимый урон")] OutgoingDamage,
    [Ed("Сила отдельных ударов")] HitPower,
    [Ed("Количество ударов")] HitCount,
    [Ed("Стоимость навыков (ОД)")] SkillCost,
    [Ed("Иммунитет")] Immunity,
    [Ed("Выбор целей")] TargetChoice,
    [Ed("Стоимость движения по гексам")] TerrainCost,
    [Ed("Отражение урона")] Reflection,
    [Ed("Поглощение урона")] Absorption,
}

/// <summary>How a modifier is summed with the others of the same parameter (docs/DECISIONS_3.md §46, steps 2–8).</summary>
public enum ModifierMode
{
    [Ed("Прибавка до процентов")] FlatBefore,
    [Ed("Проценты (складываются)")] Percent,
    [Ed("Множитель (перемножается)")] Multiplier,
    [Ed("Наибольший из группы")] Max,
    [Ed("Наименьший из группы")] Min,
    [Ed("Приоритетный источник")] Priority,
    [Ed("Взаимоисключающая категория")] Exclusive,
    [Ed("Прибавка после процентов")] FlatAfter,
}

/// <summary>
/// A modifier: what changes, by how much and how it is summed. Damage, cost, hits and the rest use the same
/// summing modes as stats; texts are generated from the data («Получает на 75 % меньше урона от магии воды»).
/// </summary>
public sealed class ModifierDef : ContentPart
{
    [Ed("Что меняется")] public ModifierTarget Target { get; set; } = ModifierTarget.Stat;
    [Ed("Характеристика"), EdRef(DefCategory.Stat), EdShowIf(nameof(Target), ModifierTarget.Stat)] public string Stat { get; set; } = "";
    [Ed("Ресурс"), EdRef(DefCategory.Resource), EdShowIf(nameof(Target), ModifierTarget.ResourceMax, ModifierTarget.ResourceRegen)] public string Resource { get; set; } = "";
    [Ed("Тип урона", "Тип, элемент (el:), категория (cat:), тег (tag:) или * — любой"), EdDamageMatch,
     EdShowIf(nameof(Target), ModifierTarget.IncomingDamage, ModifierTarget.OutgoingDamage, ModifierTarget.Immunity, ModifierTarget.Reflection, ModifierTarget.Absorption)]
    public string Match { get; set; } = "";
    [Ed("Только навыки с тегом", "Пусто — любые"), EdShowIf(nameof(Target), ModifierTarget.OutgoingDamage, ModifierTarget.HitCount, ModifierTarget.SkillCost, ModifierTarget.HitPower)]
    public string SkillTag { get; set; } = "";
    [Ed("Только навык"), EdRef(DefCategory.Skill), EdShowIf(nameof(Target), ModifierTarget.OutgoingDamage, ModifierTarget.HitCount, ModifierTarget.SkillCost, ModifierTarget.HitPower)]
    public string Skill { get; set; } = "";
    [Ed("Тег удара", "Удары этапов с этим тегом (например finisher)"), EdShowIf(nameof(Target), ModifierTarget.HitPower, ModifierTarget.OutgoingDamage)]
    public string HitTag { get; set; } = "";
    [Ed("Против типов", "Цели (для наносимого) или атакующие (для входящего): категория, ID юнита, tag:, role:; пусто — любые"),
     EdShowIf(nameof(Target), ModifierTarget.OutgoingDamage, ModifierTarget.IncomingDamage)]
    public List<string> VsTypes { get; set; } = new();
    [Ed("Состояние", "Иммунитет к состоянию (или tag:тег); пусто — иммунитет к типу урона"), EdShowIf(nameof(Target), ModifierTarget.Immunity)]
    public string Status { get; set; } = "";
    [Ed("Правило выбора цели", "Ставится первым в автоматическом выборе цели юнита"), EdShowIf(nameof(Target), ModifierTarget.TargetChoice)]
    public AutoTargetRule Rule { get; set; } = AutoTargetRule.LowestHp;
    [Ed("Тег / состояние правила"), EdShowIf(nameof(Target), ModifierTarget.TargetChoice)] public string RuleTag { get; set; } = "";
    [Ed("Поверхность", "ID местности, tag:тег или kind:вид"), EdShowIf(nameof(Target), ModifierTarget.TerrainCost)] public string Terrain { get; set; } = "";
    [Ed("Величина", "Проценты: 25 = +25 %, −75 = −75 %; множитель: 1,2; прибавки — в единицах параметра; количество ударов — штуки"),
     EdShowIf(nameof(Target), ModifierTarget.Stat, ModifierTarget.ResourceMax, ModifierTarget.ResourceRegen, ModifierTarget.IncomingDamage, ModifierTarget.OutgoingDamage,
         ModifierTarget.HitPower, ModifierTarget.HitCount, ModifierTarget.SkillCost, ModifierTarget.TerrainCost, ModifierTarget.Reflection, ModifierTarget.Absorption)]
    public double Value { get; set; } = 10;
    [Ed("Суммирование"),
     EdShowIf(nameof(Target), ModifierTarget.Stat, ModifierTarget.ResourceMax, ModifierTarget.ResourceRegen, ModifierTarget.IncomingDamage, ModifierTarget.OutgoingDamage,
         ModifierTarget.HitPower, ModifierTarget.SkillCost, ModifierTarget.TerrainCost)]
    public ModifierMode Mode { get; set; } = ModifierMode.Percent;
    [Ed("Приоритет", "Для «приоритетного источника»: действует только наибольший"), EdShowIf(nameof(Mode), ModifierMode.Priority)] public int Priority { get; set; }
    [Ed("Категория", "Из одной категории действует только один (наибольший по модулю)"), EdShowIf(nameof(Mode), ModifierMode.Exclusive)] public string Category { get; set; } = "";
    [Ed("За каждого", "Величина умножается на число учитываемых юнитов (с пределом)")] public PerEachDef? PerEach { get; set; }

    [JsonIgnore] public bool IsPercentLike => Mode is ModifierMode.Percent or ModifierMode.Max or ModifierMode.Min or ModifierMode.Priority or ModifierMode.Exclusive;

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static string N(double v) => v.ToString("0.##", Ru);

    /// <summary>Unambiguous text of the modifier; names come from the content when given.</summary>
    public string Describe(ContentDatabase? db = null)
    {
        string Name(DefCategory c, string id) => db?.Table(c).Get(id)?.DisplayName ?? id;
        string Dmg(string m) => "урона " + DamageWhat(db, m);
        string Skills()
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Skill)) parts.Add($"навыком «{Name(DefCategory.Skill, Skill)}»");
            if (!string.IsNullOrEmpty(SkillTag)) parts.Add($"навыками с тегом «{SkillTag}»");
            if (!string.IsNullOrEmpty(HitTag)) parts.Add($"ударами с тегом «{HitTag}»");
            return parts.Count == 0 ? "" : " " + string.Join(" и ", parts);
        }
        string VsName(string v) => db?.Table(DefCategory.Origin).Get(v)?.DisplayName ?? db?.Table(DefCategory.Unit).Get(v)?.DisplayName ?? v;
        string Vs(bool incoming) => VsTypes.Count == 0 ? "" : (incoming ? " от атак существ: " : " по существам: ") + string.Join(", ", VsTypes.Select(VsName));
        string More(string verb, string what) => Mode switch
        {
            ModifierMode.Multiplier => $"{verb} {what} ×{N(Value)}",
            ModifierMode.FlatBefore or ModifierMode.FlatAfter => $"{verb} {what} {Value.ToString("+0.##;−0.##", Ru)}",
            _ => Value >= 0 ? $"{verb} на {N(Value)} % больше {what}" : $"{verb} на {N(-Value)} % меньше {what}",
        };
        string tail = Mode switch
        {
            ModifierMode.Max => " (наибольший из группы)",
            ModifierMode.Min => " (наименьший из группы)",
            ModifierMode.Priority => $" (приоритет {Priority})",
            ModifierMode.Exclusive => $" (категория «{Category}»)",
            ModifierMode.FlatAfter => " (после процентов)",
            _ => "",
        };
        string text = Target switch
        {
            ModifierTarget.Stat => StatText(db),
            ModifierTarget.ResourceMax => $"Максимум «{Name(DefCategory.Resource, Resource)}» {Amount()}",
            ModifierTarget.ResourceRegen => $"Восстановление «{Name(DefCategory.Resource, Resource)}» {Amount()}",
            ModifierTarget.IncomingDamage => More("Получает", Dmg(Match)) + Vs(true),
            ModifierTarget.OutgoingDamage => More("Наносит", string.IsNullOrEmpty(Match) || Match == "*" ? "урона" : Dmg(Match)) + Skills() + Vs(false),
            ModifierTarget.HitPower => (Value >= 0 ? $"Удары сильнее на {N(Value)} %" : $"Удары слабее на {N(-Value)} %") + Skills(),
            ModifierTarget.HitCount => $"{Value.ToString("+0;−0", Ru)} удар(а) в серии" + Skills(),
            ModifierTarget.SkillCost => (Mode is ModifierMode.FlatBefore or ModifierMode.FlatAfter
                ? $"Стоимость навыков {Value.ToString("+0.##;−0.##", Ru)} ОД"
                : Value >= 0 ? $"Навыки стоят на {N(Value)} % больше ОД" : $"Навыки стоят на {N(-Value)} % меньше ОД") + Skills(),
            ModifierTarget.Immunity => !string.IsNullOrEmpty(Status) ? $"Невосприимчив к состоянию «{(Status.StartsWith("tag:") ? Status : Name(DefCategory.Status, Status))}»" : $"Невосприимчив к урону {DamageWhat(db, Match)}",
            ModifierTarget.TargetChoice => $"Автоматический выбор цели: сначала «{Rule}{(string.IsNullOrEmpty(RuleTag) ? "" : " " + RuleTag)}»",
            ModifierTarget.TerrainCost => Value >= 0 ? $"Движение по «{Terrain}» на {N(Value)} % дороже" : $"Движение по «{Terrain}» на {N(-Value)} % дешевле",
            ModifierTarget.Reflection => $"Отражает атакующему {N(Value)} % {Dmg(Match)}",
            ModifierTarget.Absorption => $"Поглощает {N(Value)} % {Dmg(Match)}",
            _ => Target.ToString(),
        };
        return Target is ModifierTarget.Immunity or ModifierTarget.TargetChoice or ModifierTarget.HitCount or ModifierTarget.Reflection or ModifierTarget.Absorption ? text : text + tail;
    }

    private string Amount() => Mode switch
    {
        ModifierMode.Multiplier => $"×{N(Value)}",
        ModifierMode.FlatBefore or ModifierMode.FlatAfter => Value.ToString("+0.##;−0.##", Ru),
        _ => Value.ToString("+0.##;−0.##", Ru) + " %",
    };

    private string StatText(ContentDatabase? db)
    {
        var sd = db?.Stat(Stat);
        string name = sd?.DisplayName ?? Stat;
        if (Mode is ModifierMode.FlatBefore or ModifierMode.FlatAfter && sd?.Format == StatFormat.Percent)
            return $"{name} {(Value * 100).ToString("+0.#;−0.#", Ru)} п. п.";
        return $"{name} {Amount()}";
    }

    /// <summary>«стихии «Вода»», «типа «Огненный»», «категории «…»», «с тегом «…»», «из любых источников».</summary>
    private static string DamageWhat(ContentDatabase? db, string m)
    {
        if (string.IsNullOrEmpty(m) || m == "*") return "из любых источников";
        if (m.StartsWith("el:")) return $"стихии «{db?.Element(m[3..])?.DisplayName ?? m[3..]}»";
        if (m.StartsWith("cat:")) return $"категории «{m[4..]}»";
        if (m.StartsWith("tag:")) return $"с тегом «{m[4..]}»";
        return $"типа «{db?.DamageType(m)?.DisplayName ?? m}»";
    }

    public override string ToString() => Describe();
}

public enum OriginKind
{
    [Ed("Раса")] Race,
    [Ed("Вид существа")] Species,
    [Ed("Класс")] Class,
    [Ed("Роль")] Role,
    [Ed("Свой вид")] Custom,
}

/// <summary>
/// Origin (race, species, class, role…): a set of passives and talents units inherit (docs/DECISIONS_3.md §46).
/// A unit gets it from its list «Происхождения» or automatically by a tag.
/// </summary>
public sealed class OriginDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.Origin;

    [Ed("Вид")] public OriginKind Kind { get; set; } = OriginKind.Race;
    [Ed("Пассивы и таланты"), EdRef(DefCategory.Passive)] public List<string> Passives { get; set; } = new();
    [Ed("Назначать по тегам", "Юнит с любым из этих тегов получает происхождение автоматически")] public List<string> AutoTags { get; set; } = new();
    [Ed("Значок", "1–2 символа")] public string Icon { get; set; } = "";
    [Ed("Цвет"), EdColor] public string Color { get; set; } = "#a0c070";
    // Part 4 (docs/DECISIONS_4.md §61): the tree and its rules.
    [Ed("Родительская категория", "Дерево любой глубины: правила родителя наследуются"), EdRef(DefCategory.Origin)] public string Parent { get; set; } = "";
    [Ed("Другие родители", "Категория может наследовать нескольким (граф без циклов, ТЗ ч. 5, 13.2): правила всех родителей; " +
        "при совпадении ID сильнее более поздний родитель и сама категория. Цикл не сохраняется"), EdRef(DefCategory.Origin), JsonOmitEmpty]
    public List<string> ExtraParents { get; set; } = new();

    /// <summary>The main parent first, then the others, each once.</summary>
    [JsonIgnore] public IEnumerable<string> ParentIds =>
        (Parent != "" ? new[] { Parent } : Array.Empty<string>()).Concat(ExtraParents.Where(x => x != "" && x != Parent)).Distinct(StringComparer.Ordinal);
    [Ed("Правила", "Пассивы, теги, перемещение, запреты, профиль ИИ — с режимом наследования")] public List<CategoryRuleDef> Rules { get; set; } = new();
}

/// <summary>A passive given by the scenario to the units of a side or with a tag.</summary>
public sealed class ScenarioPassiveDef : ContentPart
{
    [Ed("Пассив"), EdRef(DefCategory.Passive, AllowEmpty = false)] public string Passive { get; set; } = "";
    [Ed("Фракция", "Пусто — все"), EdRef(DefCategory.Faction)] public string Faction { get; set; } = "";
    [Ed("Тег юнита", "Пусто — все; иначе метка/тег юнита")] public string Tag { get; set; } = "";

    public override string ToString() => $"{Passive} → {(string.IsNullOrEmpty(Faction) ? "все" : Faction)}{(string.IsNullOrEmpty(Tag) ? "" : " · " + Tag)}";
}

/// <summary>The scenario switches off a passive (talent of a race…) or replaces it with another one.</summary>
public sealed class PassiveOverrideDef : ContentPart
{
    [Ed("Пассив"), EdRef(DefCategory.Passive, AllowEmpty = false)] public string Passive { get; set; } = "";
    [Ed("Заменить на", "Пусто — отключить"), EdRef(DefCategory.Passive)] public string ReplaceWith { get; set; } = "";

    public override string ToString() => string.IsNullOrEmpty(ReplaceWith) ? $"отключить {Passive}" : $"{Passive} → {ReplaceWith}";
}

public enum PassiveStacking
{
    [Ed("Один экземпляр (из любого числа источников)")] Unique,
    [Ed("Складывается до максимума стаков")] Stack,
    // Part 4 (docs/DECISIONS_4.md §62).
    [Ed("По экземпляру от каждого источника", "Две ауры разных владельцев действуют обе, одна и та же — один раз")] BySource,
    [Ed("Запрещать дубликаты", "Действует только первый источник, остальные отмечены как неактивные")] NoDuplicates,
}

public enum AuraAffects
{
    [Ed("Союзники")] Allies,
    [Ed("Противники")] Enemies,
    [Ed("Все")] All,
}

public enum StatCalcOrder
{
    [Ed("(база + прибавки) × (1 + Σ%) × множители + прибавки после")] Standard,
    [Ed("База × (1 + Σ%) × множители + все прибавки")] PercentOfBase,
    [Ed("Убывающая отдача: проценты 1 − Π(1 − p)")] Diminishing,
    [Ed("Только прибавки (проценты и множители не действуют)")] FlatOnly,
}

public enum StatRounding
{
    [Ed("Нет")] None,
    [Ed("До ближайшего целого")] Round,
    [Ed("Вниз")] Floor,
    [Ed("Вверх")] Ceiling,
}

public static class PassiveText
{
    /// <summary>All lines of a passive: modifiers, old-style stat modifiers, defense and offense.</summary>
    public static List<string> Lines(PassiveDef p, ContentDatabase? db = null)
    {
        var lines = p.Modifiers.Select(m => m.Describe(db)).ToList();
        foreach (var m in p.StatMods)
        {
            if (m.Add != 0) lines.Add(new ModifierDef { Target = ModifierTarget.Stat, Stat = m.Stat, Mode = ModifierMode.FlatBefore, Value = m.Add }.Describe(db));
            if (m.Mul != 0) lines.Add(new ModifierDef { Target = ModifierTarget.Stat, Stat = m.Stat, Mode = ModifierMode.Percent, Value = m.Mul * 100 }.Describe(db));
        }
        foreach (var dm in p.Defense.Modifiers)
            lines.Add(new ModifierDef { Target = ModifierTarget.IncomingDamage, Match = dm.Match, Value = dm.Percent }.Describe(db));
        foreach (var im in p.Defense.Immune) lines.Add(new ModifierDef { Target = ModifierTarget.Immunity, Match = im }.Describe(db));
        foreach (var b in p.Offense.Bonuses) lines.Add(new ModifierDef { Target = ModifierTarget.OutgoingDamage, Match = b.Match, Value = b.Percent }.Describe(db));
        return lines;
    }
}
