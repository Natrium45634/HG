using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HexTactics.Core.Content;

/// <summary>Categories of content definitions (one JSON file / editor section each).</summary>
public enum DefCategory
{
    Terrain,
    Stat,
    Resource,
    DamageType,
    Faction,
    Unit,
    Skill,
    Passive,
    CellEffect,
    Synergy,
    Status,
    AiProfile,
    Map,
    Scenario,
    /// <summary>Movement categories are plain strings listed in rules (walk, fly, swim...).</summary>
    MoveCategory,
    MapTemplate,
    Layer,
    Element,
    RulesPreset,
    Asset,
    Vfx,
    Formation,
    /// <summary>Races, species, classes, roles: sets of passives and talents (docs/DECISIONS_3.md §46).</summary>
    Origin,
    /// <summary>Saved wave groups to reuse in scenarios (docs/DECISIONS_4.md §58).</summary>
    GroupPreset,
    /// <summary>Scaling profiles of «Подогнать под уровень» and of the level of instances (docs/DECISIONS_4.md §66).</summary>
    ScalingProfile,
    /// <summary>Rules of a game mode: switches of the subsystems of part 6 (docs/DECISIONS_6.md §96).</summary>
    GameRules,
    /// <summary>Saved squads of the player (docs/DECISIONS_6.md §96).</summary>
    SquadPreset,
    /// <summary>Saved zones of deployment of a map, to reuse in its scenarios (docs/DECISIONS_6.md §97).</summary>
    ZonePreset,
    /// <summary>Reusable sets of modifiers of a mission (docs/DECISIONS_6.md §99).</summary>
    ModifierSet,
    /// <summary>Variants of a scenario: «обычная», «сложная»… (docs/DECISIONS_6.md §99).</summary>
    DifficultyVariant,
    /// <summary>Interactive objects assembled from components, to put on any map (docs/DECISIONS_6.md §100).</summary>
    ObjectPreset,
    /// <summary>Campaigns: missions over the scenarios, their links and access (docs/DECISIONS_6.md §103).</summary>
    Campaign,
    /// <summary>Sets of rewards of missions (docs/DECISIONS_6.md §103).</summary>
    RewardSet,
    /// <summary>Scales of units: sanity, morale, stability (docs/DECISIONS_6.md §104).</summary>
    Scale,
    /// <summary>Summons of sub-units: who, where, how long, limits, simple AI (docs/DECISIONS_6.md §106).</summary>
    Summon,
}

/// <summary>
/// Base of every part of the content (definitions and their nested objects): fields this version does not know —
/// written by a newer version or by hand — are kept as they are and written back on save (docs/DECISIONS_5.md §84).
/// </summary>
public abstract class ContentPart
{
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

/// <summary>Base class of every content definition.</summary>
public abstract class Def : ContentPart
{
    [Ed("ID", "Уникальный идентификатор (латиница, цифры, _)", Order = -100), JsonPropertyOrder(-10)]
    public string Id { get; set; } = "";

    [Ed("Название", Order = -99), JsonPropertyOrder(-9)]
    public string Name { get; set; } = "";

    [Ed("Описание", Order = -98), EdMultiline, JsonPropertyOrder(-8)]
    public string Description { get; set; } = "";

    [Ed("Теги", "Теги для правил, навыков и синергий", Order = -97), JsonPropertyOrder(-7)]
    public List<string> Tags { get; set; } = new();

    [Ed("Категория в библиотеке", "Группа для поиска и фильтров библиотеки (например «Боссы»)", Order = -96), JsonPropertyOrder(-6)]
    public string Group { get; set; } = "";

    [Ed("Синонимы для поиска", "Другие названия, по которым объект находится в поиске и в быстром вводе (например «святой, holy» у «Света»)", Order = -96),
     JsonPropertyOrder(-6), JsonOmitEmpty]
    public List<string> Aliases { get; set; } = new();

    [Ed("Версия", "Увеличивается автоматически при публикации изменений", Order = -95), EdReadOnly, JsonPropertyOrder(-5)]
    public int Version { get; set; } = 1;

    [Ed("Неполный (черновик)", "Объект сохранён или импортирован без всех зависимостей: он виден в библиотеке, но сценарии с ним не запускаются", Order = -94),
     JsonPropertyOrder(-4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Incomplete { get; set; }

    [JsonIgnore]
    public abstract DefCategory Category { get; }

    [JsonIgnore] public string DisplayName => string.IsNullOrEmpty(Name) ? Id : Name;

    public bool HasTag(string tag) => Tags.Contains(tag);

    public override string ToString() => $"{Category}:{Id}";
}

/// <summary>Stat modifier: final = (base + ΣAdd) × (1 + ΣMul).</summary>
public sealed class StatModDef : ContentPart
{
    [Ed("Характеристика"), EdRef(DefCategory.Stat, AllowEmpty = false)]
    public string Stat { get; set; } = "";

    [Ed("Прибавка", "Добавляется к базовому значению")]
    public double Add { get; set; }

    [Ed("Множитель", "Доля: 0.2 = +20%, -0.5 = -50%")]
    public double Mul { get; set; }

    public override string ToString() => $"{Stat} {Add:+0.##;-0.##} {Mul:+0%;-0%}";
}

/// <summary>Which units an effect or targeting can affect, relative to the source.</summary>
public sealed class TargetFilterDef : ContentPart
{
    [Ed("Враги")] public bool Enemies { get; set; } = true;
    [Ed("Союзники")] public bool Allies { get; set; }
    [Ed("Нейтралы")] public bool Neutrals { get; set; } = true;
    [Ed("Сам исполнитель")] public bool Self { get; set; }

    public static TargetFilterDef EnemiesOnly() => new() { Enemies = true, Neutrals = true };
    public static TargetFilterDef AlliesAndSelf() => new() { Enemies = false, Neutrals = false, Allies = true, Self = true };
    public static TargetFilterDef Everyone() => new() { Enemies = true, Allies = true, Neutrals = true, Self = true };

    public TargetFilterDef Clone() => (TargetFilterDef)MemberwiseClone();

    public override string ToString()
    {
        var parts = new List<string>();
        if (Enemies) parts.Add("враги");
        if (Allies) parts.Add("союзники");
        if (Neutrals) parts.Add("нейтралы");
        if (Self) parts.Add("сам");
        return parts.Count == 0 ? "никто" : string.Join(", ", parts);
    }
}

/// <summary>Control flags granted by a status level (generic: no per-status code).</summary>
public sealed class ControlFlagsDef : ContentPart
{
    [Ed("Нельзя двигаться", "Обездвиживание")] public bool BlocksMovement { get; set; }
    [Ed("Нельзя лечить", "Лечение не действует")] public bool BlocksHealing { get; set; }
    [Ed("Нельзя действовать", "Оглушение: никаких действий")] public bool BlocksActions { get; set; }
    [Ed("Нельзя использовать навыки", "Немота")] public bool BlocksSkills { get; set; }
    [Ed("Нельзя атаковать")] public bool BlocksAttacks { get; set; }
    [Ed("Нельзя поворачиваться")] public bool BlocksTurning { get; set; }
    [Ed("Нет восстановления ОД")] public bool BlocksApRegen { get; set; }
    [Ed("Ослепление", "Не замечает атаки, видимость 1")] public bool Blind { get; set; }
    [Ed("Невидимость", "Обнаруживается только вплотную")] public bool Invisible { get; set; }
    [Ed("Страх", "ИИ отступает от источника")] public bool Fear { get; set; }
    [Ed("Контроль разума", "Юнит временно сражается за источник")] public bool MindControl { get; set; }
    [Ed("Прерывает подготовку", "При наложении прерывает подготовку навыка")] public bool InterruptsCast { get; set; }
    // Part 6 (docs/DECISIONS_6.md §98): blocking through statuses and passives.
    [Ed("Неудержим", "Не блокируется (стены и ямы — по-прежнему препятствие)")] public bool IgnoresBlocking { get; set; }
    [Ed("Не может блокировать", "Снимает связи блока этого юнита")] public bool CannotBlock { get; set; }
    // Part 6, stage 6.10 (§104).
    [Ed("Паника", "Не слушает приказов и бежит от врагов; очередь приказов отложена и продолжится после"), JsonOmitEmpty] public bool Panic { get; set; }

    [JsonIgnore]
    public bool Any => BlocksHealing || BlocksMovement || BlocksActions || BlocksSkills || BlocksAttacks || BlocksTurning
                       || BlocksApRegen || Blind || Invisible || Fear || MindControl || InterruptsCast || IgnoresBlocking || CannotBlock || Panic;

    public void OrWith(ControlFlagsDef o)
    {
        BlocksMovement |= o.BlocksMovement;
        BlocksActions |= o.BlocksActions;
        BlocksSkills |= o.BlocksSkills;
        BlocksAttacks |= o.BlocksAttacks;
        BlocksTurning |= o.BlocksTurning;
        BlocksApRegen |= o.BlocksApRegen;
        Blind |= o.Blind;
        Invisible |= o.Invisible;
        Fear |= o.Fear;
        MindControl |= o.MindControl;
        InterruptsCast |= o.InterruptsCast;
        BlocksHealing |= o.BlocksHealing;
        IgnoresBlocking |= o.IgnoresBlocking;
        CannotBlock |= o.CannotBlock;
        Panic |= o.Panic;
    }

    public void Clear()
    {
        BlocksMovement = BlocksActions = BlocksSkills = BlocksAttacks = BlocksTurning = false;
        BlocksApRegen = Blind = Invisible = Fear = MindControl = InterruptsCast = BlocksHealing = false;
        IgnoresBlocking = CannotBlock = Panic = false;
    }
}

public sealed class ResourceCostDef : ContentPart
{
    [Ed("Ресурс"), EdRef(DefCategory.Resource, AllowEmpty = false)]
    public string Resource { get; set; } = "";

    [Ed("Количество"), EdRange(0, 10000)]
    public double Amount { get; set; }
}
