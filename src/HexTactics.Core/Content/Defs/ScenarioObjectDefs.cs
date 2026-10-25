using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

// Scenario objects (docs/DECISIONS_4.md §56): one object assembled from components instead of a class per story
// object — health, team, occupied hexes, spawn, goal, interaction, trigger, zone.

public enum ScenarioObjectType
{
    [Ed("База игрока")] PlayerBase,
    [Ed("База спавна")] SpawnBase,
    [Ed("Точка маршрута")] RoutePoint,
    [Ed("Контрольная точка")] Checkpoint,
    [Ed("Защитное сооружение")] Defense,
    [Ed("Цель сценария")] Goal,
    [Ed("Точка взаимодействия")] Interaction,
    [Ed("Зона поведения")] BehaviorZone,
    [Ed("Свой объект")] Custom,
}

/// <summary>What happens when the hex of an appearance is taken (docs/DECISIONS_4.md §58).</summary>
public enum SpawnBlockedPolicy
{
    [Ed("Ждать освобождения")] Wait,
    [Ed("Другой гекс области")] OtherInArea,
    [Ed("Ближайшая свободная позиция")] NearestFree,
    [Ed("Отменить с записью ошибки")] Cancel,
}

/// <summary>Defeat by the player's bases (docs/DECISIONS_4.md §56).</summary>
public enum BaseDefeatMode
{
    [Ed("Не учитывать базы")] None,
    [Ed("Уничтожена любая база")] AnyBase,
    [Ed("Уничтожены все базы")] AllBases,
    [Ed("Уничтожена главная база")] MainBase,
    [Ed("Потеряно N баз")] LostCount,
    [Ed("Своё условие")] Custom,
}

public sealed class BaseDefeatDef : ContentPart
{
    [Ed("Правило поражения")] public BaseDefeatMode Mode { get; set; } = BaseDefeatMode.MainBase;
    [Ed("Главная база", "ID объекта; пусто — база с отметкой «главная» (или первая)"), EdShowIf(nameof(Mode), BaseDefeatMode.MainBase)] public string MainBase { get; set; } = "";
    [Ed("Потеряно баз"), EdRange(1, 100), EdShowIf(nameof(Mode), BaseDefeatMode.LostCount)] public int Count { get; set; } = 2;
    [Ed("Условие"), EdShowIf(nameof(Mode), BaseDefeatMode.Custom)] public ConditionDef? Condition { get; set; }

    public override string ToString() => Mode switch
    {
        BaseDefeatMode.MainBase => "главная база" + (MainBase == "" ? "" : $" [{MainBase}]"),
        BaseDefeatMode.LostCount => $"потеряно {Count} баз",
        _ => Mode.ToString(),
    };
}

/// <summary>Component «Здоровье и защита»: the object becomes a structure in battle.</summary>
public sealed class StructureDef : ContentPart
{
    [Ed("Здоровье"), EdRange(1, 1_000_000)] public double MaxHp { get; set; } = 500;
    [Ed("Броня"), EdRange(0, 10_000)] public double Armor { get; set; }
    [Ed("Восстановление, ед./с"), EdRange(0, 10_000)] public double Regen { get; set; }
    [Ed("Можно уничтожить", "Нет — урон показывается, но объект не разрушается")] public bool Destructible { get; set; } = true;
    [Ed("Главная", "Для правила «уничтожена главная база»")] public bool Main { get; set; }
    [Ed("Важность", "Для выбора альтернативной цели врагами: к более важной"), EdRange(0, 100)] public double Importance { get; set; } = 1;
    [Ed("Приоритет защиты", "Защитники базы охраняют сначала базу с большим приоритетом"), EdRange(0, 100)] public int DefensePriority { get; set; }
    [Ed("Защита по типам урона")] public DefenseDef Defense { get; set; } = new();
    [Ed("Прорвавшийся враг снимается с поля", "Дошедший до базы враг наносит урон по базе и исчезает")] public bool RemoveLeaked { get; set; } = true;
}

/// <summary>Component «Спавн» (docs/DECISIONS_4.md §58).</summary>
public sealed class SpawnComponentDef : ContentPart
{
    [Ed("Активна", "Выключенная база не создаёт врагов, пока её не включит событие")] public bool Active { get; set; } = true;
    [Ed("Начальная задержка, с"), EdRange(0, 3600)] public double InitialDelay { get; set; }
    [Ed("Лимит живых", "Одновременно живых врагов этой базы; 0 — без лимита"), EdRange(0, 10_000)] public int MaxAlive { get; set; }
    [Ed("Лимит созданных", "Врагов за весь сценарий; 0 — без лимита (это не лимит живых)"), EdRange(0, 100_000)] public int MaxCreated { get; set; }
    [Ed("Радиус области появления", "0 — только сам гекс базы"), EdRange(0, 10)] public int AreaRadius { get; set; }
    [Ed("Типы перемещения", "Кого может создавать (пусто — всех)")] public List<string> MoveCategories { get; set; } = new();
    [Ed("Интервал повторной активации, с", "0 — без повторов"), EdRange(0, 3600)] public double ReactivateInterval { get; set; }
    [Ed("Эффект появления"), EdRef(DefCategory.Vfx)] public string SpawnVfx { get; set; } = "";
    [Ed("Условие включения")] public ConditionDef? EnableCondition { get; set; }
    [Ed("Условие отключения")] public ConditionDef? DisableCondition { get; set; }
    [Ed("Если выход занят")] public SpawnBlockedPolicy Blocked { get; set; } = SpawnBlockedPolicy.NearestFree;
    [Ed("Радиус поиска свободного гекса"), EdRange(1, 10), EdShowIf(nameof(Blocked), SpawnBlockedPolicy.NearestFree)] public int SearchRadius { get; set; } = 3;
    [Ed("Если база разрушена", "Что делать с запланированными появлениями (ТЗ ч. 5, 9.10); перенаправить уже вышедших — триггер на событие " +
        "«Уничтожение базы» с действием «сменить маршрут группы»")]
    public SpawnerDestroyedPolicy OnDestroyed { get; set; } = SpawnerDestroyedPolicy.ContinueQueue;
}

/// <summary>What a destroyed spawn base does with the appearances still planned (docs/DECISIONS_5.md §85).</summary>
public enum SpawnerDestroyedPolicy
{
    [Ed("Продолжить очередь", "Запланированные враги появляются на месте разрушенной базы (как в 1.0)")] ContinueQueue,
    [Ed("Остановить", "Оставшиеся появления отменяются")] Stop,
    [Ed("Докончить начатую порцию", "Порция (подгруппа), из которой кто-то уже вышел до разрушения, выходит до конца; остальные отменяются")] CompleteBatch,
}

/// <summary>
/// An object of the scenario: stable ID (independent of name, position and look), type preset, occupied hexes and
/// components. A base may occupy one hex (a portal) or an area (a fortress); the whole area is checked.
/// </summary>
public sealed class ScenarioObjectDef : ContentPart
{
    [Ed("ID", "Стабильный; не меняется при переименовании и перемещении")] public string Id { get; set; } = "";
    [Ed("Название")] public string Name { get; set; } = "";
    [Ed("Тип")] public ScenarioObjectType Type { get; set; } = ScenarioObjectType.PlayerBase;
    [Ed("Команда"), EdRef(DefCategory.Faction)] public string Faction { get; set; } = "player";
    [Ed("Положение"), EdHidden] public HexCoord Anchor { get; set; }
    [Ed("Ориентация")] public HexDirection Facing { get; set; } = HexDirection.E;
    [Ed("Занимаемые гексы", "Смещения от положения при ориентации «восток»; положение входит всегда"), EdHidden] public List<HexCoord> Footprint { get; set; } = new();
    [Ed("Метка карты", "Связь с маркером карты (для старых сценариев)")] public string Marker { get; set; } = "";
    [Ed("Теги")] public List<string> Tags { get; set; } = new();
    [Ed("Описание")] public string Description { get; set; } = "";
    [Ed("Здоровье и защита", "Компонент: объект становится сооружением, его можно атаковать")] public StructureDef? Health { get; set; }
    [Ed("Спавн", "Компонент: из объекта появляются юниты")] public SpawnComponentDef? Spawn { get; set; }
    [Ed("Модель"), EdRef(DefCategory.Asset)] public string Model { get; set; } = "";
    [Ed("Цвет"), EdColor] public string Color { get; set; } = "";

    [JsonIgnore] public string DisplayName => string.IsNullOrEmpty(Name) ? Id : Name;

    /// <summary>Every occupied hex (the anchor first), rotated by the facing.</summary>
    public List<HexCoord> Cells()
    {
        int steps = (int)Facing - (int)HexDirection.E;
        var list = new List<HexCoord> { Anchor };
        foreach (var o in Footprint)
        {
            if (o == HexCoord.Zero) continue;
            var c = Anchor + o.Rotate(steps);
            if (!list.Contains(c)) list.Add(c);
        }
        return list;
    }

    public bool Occupies(HexCoord c) => Cells().Contains(c);

    public override string ToString() => $"{DisplayName} [{Id}] — {Type}";
}

/// <summary>Presets of object types (docs/DECISIONS_4.md §56): which components and values a new object gets.</summary>
public static class ScenarioObjects
{
    public static string Caption(ScenarioObjectType t) => t switch
    {
        ScenarioObjectType.PlayerBase => "База игрока",
        ScenarioObjectType.SpawnBase => "База спавна",
        ScenarioObjectType.RoutePoint => "Точка маршрута",
        ScenarioObjectType.Checkpoint => "Контрольная точка",
        ScenarioObjectType.Defense => "Защитное сооружение",
        ScenarioObjectType.Goal => "Цель сценария",
        ScenarioObjectType.Interaction => "Точка взаимодействия",
        ScenarioObjectType.BehaviorZone => "Зона поведения",
        _ => "Объект",
    };

    public static string NewId(ScenarioDef sc, ScenarioObjectType t)
    {
        string prefix = t switch
        {
            ScenarioObjectType.PlayerBase => "base",
            ScenarioObjectType.SpawnBase => "spawn",
            ScenarioObjectType.Checkpoint => "checkpoint",
            ScenarioObjectType.Defense => "tower",
            _ => "obj",
        };
        for (int i = 1; ; i++)
        {
            string id = $"{prefix}_{i}";
            if (sc.Objects.All(o => o.Id != id)) return id;
        }
    }

    /// <summary>A new object of a type at a hex, with the components of its preset.</summary>
    public static ScenarioObjectDef Create(ScenarioDef sc, ScenarioObjectType t, HexCoord at)
    {
        var o = new ScenarioObjectDef { Id = NewId(sc, t), Type = t, Anchor = at, Name = Caption(t) };
        switch (t)
        {
            case ScenarioObjectType.PlayerBase:
                o.Faction = sc.Sides.FirstOrDefault(s => s.Controller == SideController.Player)?.Faction ?? "player";
                o.Health = new StructureDef { Main = sc.Objects.All(x => x.Type != ScenarioObjectType.PlayerBase) };
                break;
            case ScenarioObjectType.SpawnBase:
                o.Faction = sc.Sides.FirstOrDefault(s => s.Controller == SideController.Ai)?.Faction ?? "enemy";
                o.Spawn = new SpawnComponentDef();
                break;
            case ScenarioObjectType.Defense:
                o.Faction = sc.Sides.FirstOrDefault(s => s.Controller == SideController.Player)?.Faction ?? "player";
                o.Health = new StructureDef { MaxHp = 200, RemoveLeaked = false };
                break;
            case ScenarioObjectType.Goal:
                o.Health = new StructureDef { MaxHp = 300 };
                break;
        }
        int n = sc.Objects.Count(x => x.Type == t) + 1;
        if (n > 1) o.Name = $"{o.Name} {n}";
        return o;
    }

    /// <summary>
    /// Objects linked to a marker of the map (old scenarios migrated to format 5) stand on that marker: the anchor
    /// follows the map. An object whose marker is gone keeps its last position.
    /// </summary>
    public static void ResolveMarkers(ScenarioDef sc, MapDef? map)
    {
        if (map == null) return;
        foreach (var o in sc.Objects)
            if (o.Marker != "" && map.FindMarker(o.Marker) is { } m)
                o.Anchor = m.C;
    }

    /// <summary>A copy with a new ID: references to templates, routes and resources stay.</summary>
    public static ScenarioObjectDef Duplicate(ScenarioDef sc, ScenarioObjectDef src, HexCoord at)
    {
        var o = ContentJson.Clone(src);
        o.Id = NewId(sc, src.Type);
        o.Anchor = at;
        o.Name = src.DisplayName + " (копия)";
        if (o.Health != null) o.Health.Main = false;
        return o;
    }
}
