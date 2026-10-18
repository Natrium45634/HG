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
}

/// <summary>Base class of every content definition.</summary>
public abstract class Def
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

    [Ed("Версия", "Увеличивается автоматически при публикации изменений", Order = -95), EdReadOnly, JsonPropertyOrder(-5)]
    public int Version { get; set; } = 1;

    [JsonIgnore]
    public abstract DefCategory Category { get; }

    [JsonIgnore] public string DisplayName => string.IsNullOrEmpty(Name) ? Id : Name;

    public bool HasTag(string tag) => Tags.Contains(tag);

    public override string ToString() => $"{Category}:{Id}";
}

/// <summary>Stat modifier: final = (base + ΣAdd) × (1 + ΣMul).</summary>
public sealed class StatModDef
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
public sealed class TargetFilterDef
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
public sealed class ControlFlagsDef
{
    [Ed("Нельзя двигаться", "Обездвиживание")] public bool BlocksMovement { get; set; }
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

    [JsonIgnore]
    public bool Any => BlocksMovement || BlocksActions || BlocksSkills || BlocksAttacks || BlocksTurning
                       || BlocksApRegen || Blind || Invisible || Fear || MindControl || InterruptsCast;

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
    }

    public void Clear()
    {
        BlocksMovement = BlocksActions = BlocksSkills = BlocksAttacks = BlocksTurning = false;
        BlocksApRegen = Blind = Invisible = Fear = MindControl = InterruptsCast = false;
    }
}

public sealed class ResourceCostDef
{
    [Ed("Ресурс"), EdRef(DefCategory.Resource, AllowEmpty = false)]
    public string Resource { get; set; } = "";

    [Ed("Количество"), EdRange(0, 10000)]
    public double Amount { get; set; }
}
