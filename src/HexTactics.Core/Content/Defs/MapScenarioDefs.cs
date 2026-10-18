using System.Text.Json.Serialization;
using System.Collections.Generic;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

/// <summary>A single cell of a map definition. Nullable fields override the terrain type.</summary>
public sealed class MapCellDef
{
    [Ed("Координаты")] public HexCoord C { get; set; }
    [Ed("Местность"), EdRef(DefCategory.Terrain, AllowEmpty = false)] public string T { get; set; } = "grass";
    [Ed("Высота"), EdRange(-5, 20)] public int H { get; set; }

    [Ed("Стоимость перемещения", "Переопределение")] public double? MoveCost { get; set; }
    [Ed("Непроходима", "Переопределение: true — закрыта для всех")] public bool? Blocked { get; set; }
    [Ed("Блокирует обзор", "Переопределение")] public bool? BlocksLoS { get; set; }
    [Ed("Укрытие", "Переопределение")] public double? Cover { get; set; }
    [Ed("Можно размещать", "Переопределение")] public bool? AllowPlacement { get; set; }
    [Ed("Прочность препятствия", "Переопределение")] public int? ObstacleHp { get; set; }
    [Ed("Допустимые эффекты", "Если задано — только эти эффекты клеток могут быть наложены"), EdRef(DefCategory.CellEffect)]
    public List<string>? AllowedEffects { get; set; }
    [Ed("Стартовые эффекты"), EdRef(DefCategory.CellEffect)] public List<string>? Effects { get; set; }
    [Ed("Теги клетки")] public List<string>? Tags { get; set; }
    [Ed("Имя клетки")] public string? Name { get; set; }
    [Ed("Описание клетки")] public string? Description { get; set; }

    public MapCellDef Clone()
    {
        var c = (MapCellDef)MemberwiseClone();
        c.AllowedEffects = AllowedEffects == null ? null : new List<string>(AllowedEffects);
        c.Effects = Effects == null ? null : new List<string>(Effects);
        c.Tags = Tags == null ? null : new List<string>(Tags);
        return c;
    }
}

public enum MarkerType
{
    PlayerStart,
    EnemySpawn,
    NeutralSpawn,
    Objective,
    Trigger,
    Exit,
}

public sealed class MapMarkerDef
{
    [Ed("ID метки")] public string Id { get; set; } = "";
    [Ed("Тип")] public MarkerType Type { get; set; } = MarkerType.PlayerStart;
    [Ed("Клетка")] public HexCoord C { get; set; }
    [Ed("Подпись")] public string Label { get; set; } = "";
    [Ed("Радиус"), EdRange(0, 20)] public int Radius { get; set; }
}

public sealed class MapRulesDef
{
    [Ed("Темнота", "0..1: снижает дальность обнаружения и шанс заметить атаку"), EdRange(0, 1)] public double Darkness { get; set; }
    [Ed("Местность за краем", "Для информации редактора")] public string Notes { get; set; } = "";
}

public sealed class MapDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.Map;

    [Ed("Правила карты")] public MapRulesDef Rules { get; set; } = new();
    [EdHidden] public List<MapCellDef> Cells { get; set; } = new();
    [EdHidden] public List<MapMarkerDef> Markers { get; set; } = new();

    public static MapDef CreateRectangle(string id, int width, int height, string terrain)
    {
        var map = new MapDef { Id = id, Name = id };
        // "Odd-r" style rectangle in axial coordinates.
        for (int row = 0; row < height; row++)
        {
            int offset = row / 2;
            for (int col = 0; col < width; col++)
                map.Cells.Add(new MapCellDef { C = new HexCoord(col - offset, row), T = terrain });
        }
        return map;
    }

    public static MapDef CreateHexagon(string id, int radius, string terrain)
    {
        var map = new MapDef { Id = id, Name = id };
        foreach (var c in HexCoord.Spiral(HexCoord.Zero, radius))
            map.Cells.Add(new MapCellDef { C = c, T = terrain });
        return map;
    }

    public static MapDef CreateRhombus(string id, int width, int height, string terrain)
    {
        var map = new MapDef { Id = id, Name = id };
        for (int r = 0; r < height; r++)
        for (int q = 0; q < width; q++)
            map.Cells.Add(new MapCellDef { C = new HexCoord(q, r), T = terrain });
        return map;
    }

    public MapMarkerDef? FindMarker(string id)
    {
        foreach (var m in Markers)
            if (m.Id == id)
                return m;
        return null;
    }
}

public enum SideController
{
    Player,
    Ai,
}

public sealed class ScenarioSideDef
{
    [Ed("Фракция"), EdRef(DefCategory.Faction, AllowEmpty = false)] public string Faction { get; set; } = "player";
    [Ed("Управление")] public SideController Controller { get; set; } = SideController.Player;
}

public sealed class ScenarioUnitDef
{
    [Ed("Шаблон юнита"), EdRef(DefCategory.Unit, AllowEmpty = false)] public string Unit { get; set; } = "";
    [Ed("Фракция", "Пусто — из шаблона"), EdRef(DefCategory.Faction)] public string Faction { get; set; } = "";
    [Ed("Клетка")] public HexCoord C { get; set; }
    [Ed("Направление")] public HexDirection Facing { get; set; } = HexDirection.E;
    [Ed("Метка-ID", "Для условий сценария")] public string Tag { get; set; } = "";
    [Ed("Профиль ИИ", "Пусто — из шаблона"), EdRef(DefCategory.AiProfile)] public string AiProfile { get; set; } = "";
    [Ed("Имя", "Пусто — из шаблона")] public string Name { get; set; } = "";
}

public sealed class WaveUnitDef
{
    [Ed("Шаблон юнита"), EdRef(DefCategory.Unit, AllowEmpty = false)] public string Unit { get; set; } = "";
    [Ed("Количество"), EdRange(1, 100)] public int Count { get; set; } = 1;
}

public sealed class WaveDef
{
    [Ed("Название")] public string Name { get; set; } = "Волна";
    [Ed("Время начала, с"), EdRange(0, 36000)] public double Time { get; set; } = 30;
    [Ed("Метка появления", "ID метки карты (точка появления)")] public string SpawnMarker { get; set; } = "";
    [Ed("Фракция"), EdRef(DefCategory.Faction, AllowEmpty = false)] public string Faction { get; set; } = "enemy";
    [Ed("Юниты")] public List<WaveUnitDef> Units { get; set; } = new();
    [Ed("Сообщение")] public string Message { get; set; } = "";
}

public enum ConditionType
{
    AllEnemiesDead,
    AllPlayerUnitsDead,
    SurviveTime,
    ReachMarker,
    UnitDead,
    UnitAlive,
    KillCount,
    TimeElapsed,
    WaveSpawned,
}

public sealed class ConditionDef
{
    [Ed("Условие")] public ConditionType Type { get; set; } = ConditionType.AllEnemiesDead;
    [Ed("Время, с"), EdShowIf(nameof(Type), ConditionType.SurviveTime, ConditionType.TimeElapsed)] public double Seconds { get; set; } = 60;
    [Ed("Метка карты"), EdShowIf(nameof(Type), ConditionType.ReachMarker)] public string Marker { get; set; } = "";
    [Ed("Метка юнита"), EdShowIf(nameof(Type), ConditionType.UnitDead, ConditionType.UnitAlive, ConditionType.ReachMarker)]
    public string UnitTag { get; set; } = "";
    [Ed("Количество"), EdShowIf(nameof(Type), ConditionType.KillCount)] public int Count { get; set; } = 5;
    [Ed("Волна (индекс)"), EdShowIf(nameof(Type), ConditionType.WaveSpawned)] public int Wave { get; set; }

    public string Describe() => Type switch
    {
        ConditionType.AllEnemiesDead => "Уничтожить всех врагов",
        ConditionType.AllPlayerUnitsDead => "Все юниты игрока погибли",
        ConditionType.SurviveTime => $"Продержаться {Seconds:0} с",
        ConditionType.ReachMarker => $"Довести {(string.IsNullOrEmpty(UnitTag) ? "любого юнита" : UnitTag)} до «{Marker}»",
        ConditionType.UnitDead => $"Юнит «{UnitTag}» погиб",
        ConditionType.UnitAlive => $"Юнит «{UnitTag}» жив",
        ConditionType.KillCount => $"Уничтожить {Count} врагов",
        ConditionType.TimeElapsed => $"Прошло {Seconds:0} с",
        ConditionType.WaveSpawned => $"Началась волна {Wave + 1}",
        _ => Type.ToString(),
    };
}

public enum TriggerActionType
{
    Message,
    SpawnWave,
    Autopause,
    Victory,
    Defeat,
    AddCellEffect,
}

public sealed class TriggerActionDef
{
    [Ed("Действие")] public TriggerActionType Type { get; set; } = TriggerActionType.Message;
    [Ed("Текст"), EdShowIf(nameof(Type), TriggerActionType.Message, TriggerActionType.Victory, TriggerActionType.Defeat)]
    public string Text { get; set; } = "";
    [Ed("Волна (индекс)"), EdShowIf(nameof(Type), TriggerActionType.SpawnWave)] public int Wave { get; set; }
    [Ed("Эффект клетки"), EdRef(DefCategory.CellEffect), EdShowIf(nameof(Type), TriggerActionType.AddCellEffect)] public string CellEffect { get; set; } = "";
    [Ed("Метка карты"), EdShowIf(nameof(Type), TriggerActionType.AddCellEffect)] public string Marker { get; set; } = "";
}

public sealed class TriggerDef
{
    [Ed("Название")] public string Name { get; set; } = "Триггер";
    [Ed("Условие")] public ConditionDef Condition { get; set; } = new() { Type = ConditionType.TimeElapsed };
    [Ed("Действия")] public List<TriggerActionDef> Actions { get; set; } = new();
    [Ed("Однократный")] public bool Once { get; set; } = true;
}

public sealed class ScenarioDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.Scenario;

    [Ed("Карта", Group = "Основное"), EdRef(DefCategory.Map, AllowEmpty = false)] public string Map { get; set; } = "";
    [Ed("Seed случайности", "0 = случайный при каждом запуске", Group = "Основное")] public ulong Seed { get; set; } = 12345;
    [Ed("Вступление", Group = "Основное"), EdMultiline] public string Intro { get; set; } = "";
    [Ed("Стороны", Group = "Стороны")] public List<ScenarioSideDef> Sides { get; set; } = new()
    {
        new ScenarioSideDef { Faction = "player", Controller = SideController.Player },
        new ScenarioSideDef { Faction = "enemy", Controller = SideController.Ai },
    };
    [Ed("Юниты", Group = "Юниты"), EdHidden] public List<ScenarioUnitDef> Units { get; set; } = new();
    [Ed("Волны", Group = "Волны")] public List<WaveDef> Waves { get; set; } = new();
    [Ed("Условия победы", "Любое выполненное — победа", Group = "Условия")] public List<ConditionDef> Victory { get; set; } = new() { new ConditionDef() };
    [Ed("Условия поражения", "Любое выполненное — поражение", Group = "Условия")] public List<ConditionDef> Defeat { get; set; } = new() { new ConditionDef { Type = ConditionType.AllPlayerUnitsDead } };
    [Ed("Триггеры", Group = "Триггеры")] public List<TriggerDef> Triggers { get; set; } = new();
    [Ed("Переопределение правил", "Ограничения сценария", Group = "Ограничения")] public ScenarioLimitsDef Limits { get; set; } = new();
}

public sealed class ScenarioLimitsDef
{
    [Ed("Разрешить несколько юнитов на клетке")] public bool AllowStacking { get; set; }
    [Ed("Туман войны")] public bool FogOfWar { get; set; } = true;
    [Ed("Лимит времени, с", "0 = без лимита (по истечении — поражение)"), EdRange(0, 36000)] public double TimeLimit { get; set; }
}
