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
    [Ed("Маркер предупреждения", "Показывать линию пути перед появлением врагов из этой точки")] public bool SpawnWarning { get; set; } = true;
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
    [Ed("Создана из шаблона", "Происхождение карты (шаблон не меняет карту автоматически)"), EdReadOnly] public string TemplateId { get; set; } = "";
    [Ed("Версия шаблона", "Версия шаблона на момент создания/обновления"), EdReadOnly] public int TemplateVersion { get; set; }
    [EdHidden] public List<MapCellDef> Cells { get; set; } = new();
    [EdHidden] public List<MapMarkerDef> Markers { get; set; } = new();
    [EdHidden] public List<MapRouteDef> Routes { get; set; } = new();

    /// <summary>Rectangle of width columns × height rows ("odd-r": odd rows shifted half a hex right).</summary>
    public static MapDef CreateRectangle(string id, int width, int height, string terrain)
    {
        var map = new MapDef { Id = id, Name = id };
        for (int row = 0; row < height; row++)
        for (int col = 0; col < width; col++)
            map.Cells.Add(new MapCellDef { C = HexLayout.FromOffset(col, row), T = terrain });
        return map;
    }

    public MapRouteDef? FindRoute(string id)
    {
        foreach (var r in Routes)
            if (r.Id == id)
                return r;
        return null;
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
    [Ed("Метка-ID", "Для условий и целей (например, boss)")] public string Tag { get; set; } = "";
    [Ed("Профиль ИИ", "Пусто — из шаблона"), EdRef(DefCategory.AiProfile)] public string AiProfile { get; set; } = "";
}

public enum WaveStart
{
    [Ed("По времени")] Time,
    [Ed("После зачистки предыдущей волны")] AfterPrevious,
    [Ed("По условию (вход в область, устройство, событие…)")] OnCondition,
    [Ed("Только действием триггера")] Manual,
}

public sealed class WaveDef
{
    [Ed("Название")] public string Name { get; set; } = "Волна";
    [Ed("Начало")] public WaveStart Start { get; set; } = WaveStart.Time;
    [Ed("Время начала, с"), EdRange(0, 36000), EdShowIf(nameof(Start), WaveStart.Time)] public double Time { get; set; } = 30;
    [Ed("Задержка, с", "После зачистки предыдущей / выполнения условия (маркер предупреждения добавляется сверху)"), EdRange(0, 3600),
     EdShowIf(nameof(Start), WaveStart.AfterPrevious, WaveStart.OnCondition)]
    public double Delay { get; set; }
    [Ed("Условие начала"), EdShowIf(nameof(Start), WaveStart.OnCondition)] public ConditionDef StartCondition { get; set; } = new() { Type = ConditionType.UnitInArea };
    [Ed("Метка появления", "ID метки карты (точка появления)")] public string SpawnMarker { get; set; } = "";
    [Ed("Доп. точки появления", "Враги распределяются по точкам по очереди")] public List<string> SpawnMarkers { get; set; } = new();
    [Ed("Маршрут", "ID маршрута карты: враги идут по нему")] public string Route { get; set; } = "";
    [Ed("Интервал появления, с", "Между врагами волны (0 — все сразу)"), EdRange(0, 600)] public double Interval { get; set; }
    [Ed("Лимит одновременно живых", "0 — без лимита; остальные ждут свободного места"), EdRange(0, 500)] public int MaxAlive { get; set; }
    [Ed("Фракция"), EdRef(DefCategory.Faction, AllowEmpty = false)] public string Faction { get; set; } = "enemy";
    [Ed("Юниты")] public List<WaveUnitDef> Units { get; set; } = new();
    [Ed("Своё условие завершения", "Иначе — волна зачищена, когда все её враги появились и уничтожены")] public bool CustomEnd { get; set; }
    [Ed("Условие завершения"), EdShowIf(nameof(CustomEnd), true)] public ConditionDef EndCondition { get; set; } = new() { Type = ConditionType.TimeElapsed };
    [Ed("При начале")] public List<TriggerActionDef> OnStart { get; set; } = new();
    [Ed("При завершении")] public List<TriggerActionDef> OnEnd { get; set; } = new();
    [Ed("Маркер предупреждения", "Линия будущего пути перед появлением")] public bool Warning { get; set; } = true;
    [Ed("Мин. предупреждение, с", "0 — из правил"), EdRange(0, 600)] public double WarningSeconds { get; set; }
    [Ed("Сообщение")] public string Message { get; set; } = "";

    public IEnumerable<string> AllSpawnMarkers()
    {
        if (!string.IsNullOrEmpty(SpawnMarker)) yield return SpawnMarker;
        foreach (var m in SpawnMarkers)
            if (!string.IsNullOrEmpty(m) && m != SpawnMarker)
                yield return m;
    }
}

public enum ConditionType
{
    [Ed("Все враги уничтожены")] AllEnemiesDead,
    [Ed("Все юниты игрока погибли")] AllPlayerUnitsDead,
    [Ed("Продержаться N с")] SurviveTime,
    [Ed("Юнит игрока в метке")] ReachMarker,
    [Ed("Юнит с меткой погиб")] UnitDead,
    [Ed("Юнит с меткой жив")] UnitAlive,
    [Ed("Уничтожено N врагов")] KillCount,
    [Ed("Прошло N с")] TimeElapsed,
    [Ed("Волна началась")] WaveSpawned,
    // Part 2 (docs/DECISIONS_2.md §37): logic and new leaves.
    [Ed("И (все вложенные)")] All,
    [Ed("ИЛИ (хотя бы одно)")] Any,
    [Ed("НЕ")] Not,
    [Ed("По порядку (вложенные выполнены в заданном порядке)")] InOrder,
    [Ed("Юнит стороны в области N с")] UnitInArea,
    [Ed("Волна зачищена")] WaveCleared,
    [Ed("Зачищено N волн")] WavesCleared,
    [Ed("Цель выполнена")] ObjectiveCompleted,
    [Ed("Цель провалена")] ObjectiveFailed,
    [Ed("Флаг события установлен")] FlagSet,
    [Ed("Здоровье ниже доли")] HpBelow,
    [Ed("Пропущено N врагов")] EnemiesLeaked,
    [Ed("Всегда")] Always,
    // Stage 9: catalog of conditions (docs/DECISIONS_2.md §33).
    [Ed("Противник в дальности")] TargetInRange,
    [Ed("Обнаружен противник")] EnemySpotted,
    [Ed("Юнит покинул область")] UnitLeftArea,
    [Ed("Ресурс достиг значения")] ResourceReached,
    [Ed("Наложено состояние")] HasStatus,
    [Ed("Прошло N с после события")] FlagTimer,
    [Ed("Препятствие разрушено")] ObstacleDestroyed,
    [Ed("Босс сменил фазу")] BossPhase,
    [Ed("Произошла комбинация эффектов")] SynergyOccurred,
}

public enum CompareMode
{
    [Ed("не меньше")] AtLeast,
    [Ed("не больше")] AtMost,
}

public enum AreaSide
{
    [Ed("Игрок")] Player,
    [Ed("Враги")] Enemy,
    [Ed("Любая")] Any,
}

public sealed class ConditionDef
{
    [Ed("Условие")] public ConditionType Type { get; set; } = ConditionType.AllEnemiesDead;
    [Ed("Вложенные условия"), EdShowIf(nameof(Type), ConditionType.All, ConditionType.Any, ConditionType.Not, ConditionType.InOrder)]
    public List<ConditionDef> Children { get; set; } = new();
    [Ed("Время, с"), EdRange(0, 36000), EdShowIf(nameof(Type), ConditionType.SurviveTime, ConditionType.TimeElapsed, ConditionType.UnitInArea, ConditionType.FlagTimer)]
    public double Seconds { get; set; } = 60;
    [Ed("Метка карты", "Для разрушения и комбинаций — пусто: где угодно"),
     EdShowIf(nameof(Type), ConditionType.ReachMarker, ConditionType.UnitInArea, ConditionType.UnitLeftArea, ConditionType.ObstacleDestroyed, ConditionType.SynergyOccurred)]
    public string Marker { get; set; } = "";
    [Ed("Метка юнита", "Пусто — сам владелец (босс, носитель состояния, юнит с пассивкой или правилом ИИ)"),
     EdShowIf(nameof(Type), ConditionType.UnitDead, ConditionType.UnitAlive, ConditionType.ReachMarker, ConditionType.HpBelow, ConditionType.UnitInArea,
         ConditionType.UnitLeftArea, ConditionType.TargetInRange, ConditionType.ResourceReached, ConditionType.HasStatus, ConditionType.BossPhase)]
    public string UnitTag { get; set; } = "";
    [Ed("Сторона"), EdShowIf(nameof(Type), ConditionType.UnitInArea, ConditionType.UnitLeftArea, ConditionType.EnemySpotted)] public AreaSide Side { get; set; } = AreaSide.Player;
    [Ed("Ресурс"), EdRef(DefCategory.Resource), EdShowIf(nameof(Type), ConditionType.ResourceReached)] public string Resource { get; set; } = "";
    [Ed("Значение", "Ресурс; для состояния — минимум очков/стаков (0 — любое)"), EdShowIf(nameof(Type), ConditionType.ResourceReached, ConditionType.HasStatus)]
    public double Value { get; set; }
    [Ed("Сравнение"), EdShowIf(nameof(Type), ConditionType.ResourceReached)] public CompareMode Compare { get; set; } = CompareMode.AtLeast;
    [Ed("Состояние"), EdRef(DefCategory.Status), EdShowIf(nameof(Type), ConditionType.HasStatus)] public string Status { get; set; } = "";
    [Ed("Синергия"), EdRef(DefCategory.Synergy), EdShowIf(nameof(Type), ConditionType.SynergyOccurred)] public string Synergy { get; set; } = "";
    [Ed("Количество", "Убитые/волны/прорывы; дальность для «в дальности»; номер фазы босса; число разрушений и комбинаций"), EdRange(0, 100000),
     EdShowIf(nameof(Type), ConditionType.KillCount, ConditionType.WavesCleared, ConditionType.EnemiesLeaked, ConditionType.TargetInRange,
         ConditionType.BossPhase, ConditionType.ObstacleDestroyed, ConditionType.SynergyOccurred)]
    public int Count { get; set; } = 5;
    [Ed("Категории врагов", "Для подсчёта убитых: категории существ или tag:тег; пусто — все"), EdShowIf(nameof(Type), ConditionType.KillCount)]
    public List<string> Categories { get; set; } = new();
    [Ed("Волна (индекс)"), EdRange(0, 1000), EdShowIf(nameof(Type), ConditionType.WaveSpawned, ConditionType.WaveCleared)] public int Wave { get; set; }
    [Ed("Цель (ID)"), EdShowIf(nameof(Type), ConditionType.ObjectiveCompleted, ConditionType.ObjectiveFailed, ConditionType.EnemiesLeaked)]
    public string Objective { get; set; } = "";
    [Ed("Флаг"), EdShowIf(nameof(Type), ConditionType.FlagSet, ConditionType.FlagTimer)] public string Flag { get; set; } = "";
    [Ed("Доля здоровья"), EdRange(0, 1), EdShowIf(nameof(Type), ConditionType.HpBelow)] public double Fraction { get; set; } = 0.5;

    public string Describe() => Type switch
    {
        ConditionType.AllEnemiesDead => "Уничтожить всех врагов",
        ConditionType.AllPlayerUnitsDead => "Все юниты игрока погибли",
        ConditionType.SurviveTime => $"Продержаться {Seconds:0} с",
        ConditionType.ReachMarker => $"Довести {(string.IsNullOrEmpty(UnitTag) ? "любого юнита" : UnitTag)} до «{Marker}»",
        ConditionType.UnitDead => $"Юнит «{UnitTag}» погиб",
        ConditionType.UnitAlive => $"Юнит «{UnitTag}» жив",
        ConditionType.KillCount => $"Уничтожить {Count} врагов" + (Categories.Count > 0 ? $" ({string.Join(", ", Categories)})" : ""),
        ConditionType.TimeElapsed => $"Прошло {Seconds:0} с",
        ConditionType.WaveSpawned => $"Началась волна {Wave + 1}",
        ConditionType.All => "Все: " + string.Join(" и ", Children.ConvertAll(c => c.Describe())),
        ConditionType.Any => "Любое: " + string.Join(" или ", Children.ConvertAll(c => c.Describe())),
        ConditionType.Not => "Не: " + string.Join(", ", Children.ConvertAll(c => c.Describe())),
        ConditionType.InOrder => "По порядку: " + string.Join(" → ", Children.ConvertAll(c => c.Describe())),
        ConditionType.UnitInArea => $"{(Side == AreaSide.Enemy ? "Враг" : Side == AreaSide.Player ? "Юнит игрока" : "Юнит")} в «{Marker}»" + (Seconds > 0 ? $" {Seconds:0} с" : ""),
        ConditionType.WaveCleared => $"Волна {Wave + 1} зачищена",
        ConditionType.WavesCleared => $"Зачищено волн: {Count}",
        ConditionType.ObjectiveCompleted => $"Цель «{Objective}» выполнена",
        ConditionType.ObjectiveFailed => $"Цель «{Objective}» провалена",
        ConditionType.FlagSet => $"Событие «{Flag}»",
        ConditionType.HpBelow => $"{(string.IsNullOrEmpty(UnitTag) ? "Здоровье" : $"Здоровье «{UnitTag}»")} ниже {Fraction:P0}",
        ConditionType.EnemiesLeaked => $"Пропущено врагов: {Count}",
        ConditionType.Always => "Всегда",
        ConditionType.TargetInRange => $"{Who()}: противник в {Count} гекс.",
        ConditionType.EnemySpotted => Side == AreaSide.Enemy ? "Враги обнаружили юнита игрока" : "Обнаружен противник",
        ConditionType.UnitLeftArea => $"{(Side == AreaSide.Enemy ? "Враг" : Side == AreaSide.Player ? "Юнит игрока" : "Юнит")} покинул «{Marker}»",
        ConditionType.ResourceReached => $"{Who()}: {Resource} {(Compare == CompareMode.AtLeast ? "≥" : "≤")} {Value:0.#}",
        ConditionType.HasStatus => $"{Who()}: состояние «{Status}»" + (Value > 0 ? $" ≥ {Value:0.#}" : ""),
        ConditionType.FlagTimer => $"{Seconds:0.#} с после «{Flag}»",
        ConditionType.ObstacleDestroyed => $"Разрушено препятствий: {System.Math.Max(1, Count)}" + (string.IsNullOrEmpty(Marker) ? "" : $" в «{Marker}»"),
        ConditionType.BossPhase => $"{(string.IsNullOrEmpty(UnitTag) ? "Босс" : $"«{UnitTag}»")}: фаза {System.Math.Max(1, Count)}",
        ConditionType.SynergyOccurred => $"Комбинация «{Synergy}» ×{System.Math.Max(1, Count)}" + (string.IsNullOrEmpty(Marker) ? "" : $" в «{Marker}»"),
        _ => Type.ToString(),
    };

    private string Who() => string.IsNullOrEmpty(UnitTag) ? "владелец" : $"«{UnitTag}»";
}

public enum TriggerActionType
{
    [Ed("Сообщение")] Message,
    [Ed("Запустить волну")] SpawnWave,
    [Ed("Автопауза")] Autopause,
    [Ed("Победа")] Victory,
    [Ed("Поражение")] Defeat,
    [Ed("Эффект клетки в метке")] AddCellEffect,
    // Part 2: events, objectives, map, units (boss phases use the same actions).
    [Ed("Установить флаг события")] SetFlag,
    [Ed("Выполнить цель")] CompleteObjective,
    [Ed("Провалить цель")] FailObjective,
    [Ed("Отменить волну")] CancelWave,
    [Ed("Сменить местность")] ChangeTerrain,
    [Ed("Призвать юнитов")] SummonUnits,
    [Ed("Наложить состояние")] ApplyStatus,
    [Ed("Снять состояние")] RemoveStatus,
    [Ed("Добавить навык")] AddSkill,
    [Ed("Убрать навык")] RemoveSkill,
    [Ed("Навык автоатаки")] SetAutoAttackSkill,
    [Ed("Профиль ИИ")] SetAiProfile,
    [Ed("Лечение, % HP")] HealPercent,
    // Stage 9: catalog of operations (docs/DECISIONS_2.md §33).
    [Ed("Операции над юнитами (урон, лечение, ресурсы, состояния, щиты, характеристики…)")] Effects,
    [Ed("Сменить маршрут")] SetRoute,
    [Ed("Разрушить препятствия")] DestroyObstacle,
    [Ed("Запустить триггер")] ActivateTrigger,
    [Ed("Сменить фазу босса")] SetBossPhase,
    [Ed("Визуальный эффект")] PlayVfx,
    [Ed("Звук")] PlaySound,
}

public enum ActionTarget
{
    [Ed("Владелец (босс / источник события)")] Self,
    [Ed("Юниты с меткой")] UnitTag,
    [Ed("Все враги игрока")] Enemies,
    [Ed("Все юниты игрока")] PlayerUnits,
}

public sealed class TriggerActionDef
{
    [Ed("Действие")] public TriggerActionType Type { get; set; } = TriggerActionType.Message;
    [Ed("Текст / ID", "Сообщение; имя триггера; ID визуального или звукового эффекта"),
     EdShowIf(nameof(Type), TriggerActionType.Message, TriggerActionType.Victory, TriggerActionType.Defeat, TriggerActionType.Autopause,
         TriggerActionType.ActivateTrigger, TriggerActionType.PlayVfx, TriggerActionType.PlaySound)]
    public string Text { get; set; } = "";
    [Ed("Волна (индекс)"), EdRange(0, 1000), EdShowIf(nameof(Type), TriggerActionType.SpawnWave, TriggerActionType.CancelWave)] public int Wave { get; set; }
    [Ed("Эффект клетки"), EdRef(DefCategory.CellEffect), EdShowIf(nameof(Type), TriggerActionType.AddCellEffect)] public string CellEffect { get; set; } = "";
    [Ed("Метка карты", "Пусто — вокруг владельца"),
     EdShowIf(nameof(Type), TriggerActionType.AddCellEffect, TriggerActionType.ChangeTerrain, TriggerActionType.SummonUnits, TriggerActionType.DestroyObstacle,
         TriggerActionType.PlayVfx, TriggerActionType.PlaySound)]
    public string Marker { get; set; } = "";
    [Ed("Радиус"), EdRange(0, 20), EdShowIf(nameof(Type), TriggerActionType.ChangeTerrain, TriggerActionType.SummonUnits, TriggerActionType.DestroyObstacle)]
    public int Radius { get; set; } = 1;
    [Ed("Маршрут карты"), EdShowIf(nameof(Type), TriggerActionType.SetRoute)] public string Route { get; set; } = "";
    [Ed("Фаза босса (номер)", "1 — первая фаза из списка босса"), EdRange(1, 50), EdShowIf(nameof(Type), TriggerActionType.SetBossPhase)] public int Phase { get; set; } = 1;
    [Ed("Операции", "Каталог операций навыков: применяются к выбранным юнитам"), EdShowIf(nameof(Type), TriggerActionType.Effects)]
    public List<SkillEffectDef> Effects { get; set; } = new();
    [Ed("Флаг"), EdShowIf(nameof(Type), TriggerActionType.SetFlag)] public string Flag { get; set; } = "";
    [Ed("Цель (ID)"), EdShowIf(nameof(Type), TriggerActionType.CompleteObjective, TriggerActionType.FailObjective)] public string Objective { get; set; } = "";
    [Ed("Местность"), EdRef(DefCategory.Terrain), EdShowIf(nameof(Type), TriggerActionType.ChangeTerrain)] public string Terrain { get; set; } = "";
    [Ed("Юнит"), EdRef(DefCategory.Unit), EdShowIf(nameof(Type), TriggerActionType.SummonUnits)] public string Unit { get; set; } = "";
    [Ed("Количество"), EdRange(1, 100), EdShowIf(nameof(Type), TriggerActionType.SummonUnits)] public int Count { get; set; } = 1;
    [Ed("На кого"),
     EdShowIf(nameof(Type), TriggerActionType.ApplyStatus, TriggerActionType.RemoveStatus, TriggerActionType.AddSkill, TriggerActionType.RemoveSkill,
         TriggerActionType.SetAutoAttackSkill, TriggerActionType.SetAiProfile, TriggerActionType.HealPercent, TriggerActionType.Effects, TriggerActionType.SetRoute,
         TriggerActionType.SetBossPhase)]
    public ActionTarget Target { get; set; } = ActionTarget.Self;
    [Ed("Метка юнита"), EdShowIf(nameof(Target), ActionTarget.UnitTag)] public string UnitTag { get; set; } = "";
    [Ed("Состояние"), EdRef(DefCategory.Status), EdShowIf(nameof(Type), TriggerActionType.ApplyStatus, TriggerActionType.RemoveStatus)] public string Status { get; set; } = "";
    [Ed("Величина", "Очки/стаки состояния, % лечения"), EdShowIf(nameof(Type), TriggerActionType.ApplyStatus, TriggerActionType.HealPercent)] public double Amount { get; set; } = 1;
    [Ed("Навык"), EdRef(DefCategory.Skill), EdShowIf(nameof(Type), TriggerActionType.AddSkill, TriggerActionType.RemoveSkill, TriggerActionType.SetAutoAttackSkill)]
    public string Skill { get; set; } = "";
    [Ed("Автоатака (индекс)"), EdRange(0, 20), EdShowIf(nameof(Type), TriggerActionType.SetAutoAttackSkill)] public int AutoAttack { get; set; }
    [Ed("Профиль ИИ"), EdRef(DefCategory.AiProfile), EdShowIf(nameof(Type), TriggerActionType.SetAiProfile)] public string AiProfile { get; set; } = "";

    public string Describe() => Type switch
    {
        TriggerActionType.Message => $"Сообщение «{Text}»",
        TriggerActionType.SpawnWave => $"Волна {Wave + 1}",
        TriggerActionType.CancelWave => $"Отменить волну {Wave + 1}",
        TriggerActionType.SetFlag => $"Флаг «{Flag}»",
        TriggerActionType.CompleteObjective => $"Выполнить цель «{Objective}»",
        TriggerActionType.FailObjective => $"Провалить цель «{Objective}»",
        TriggerActionType.ChangeTerrain => $"Местность → {Terrain}",
        TriggerActionType.SummonUnits => $"Призыв {Unit} ×{Count}",
        TriggerActionType.ApplyStatus => $"Состояние {Status}",
        TriggerActionType.AddSkill => $"+навык {Skill}",
        TriggerActionType.RemoveSkill => $"−навык {Skill}",
        TriggerActionType.SetAutoAttackSkill => $"Автоатака → {Skill}",
        TriggerActionType.SetAiProfile => $"Профиль ИИ → {AiProfile}",
        TriggerActionType.HealPercent => $"Лечение {Amount:0.#}%",
        TriggerActionType.RemoveStatus => $"Снять {Status}",
        TriggerActionType.AddCellEffect => $"Эффект клетки {CellEffect}",
        TriggerActionType.Autopause => "Автопауза",
        TriggerActionType.Victory => "Победа",
        TriggerActionType.Defeat => "Поражение",
        TriggerActionType.Effects => Effects.Count == 0 ? "Операции (нет)" : string.Join("; ", Effects.ConvertAll(e => e.Describe())),
        TriggerActionType.SetRoute => $"Маршрут → {Route}",
        TriggerActionType.DestroyObstacle => "Разрушить препятствия" + (string.IsNullOrEmpty(Marker) ? "" : $" в «{Marker}»"),
        TriggerActionType.ActivateTrigger => $"Триггер «{Text}»",
        TriggerActionType.SetBossPhase => $"Фаза босса {Phase}",
        TriggerActionType.PlayVfx => $"Визуальный эффект {Text}",
        TriggerActionType.PlaySound => $"Звук {Text}",
        _ => Type.ToString(),
    };
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
    [Ed("Пресет правил", "Пусто — глобальные правила", Group = "Основное"), EdRef(DefCategory.RulesPreset)] public string RulesPreset { get; set; } = "";
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
    [Ed("Цели", "Типы сценариев: зачистка, оборона, босс, выживание, волны, сопровождение, захват, уничтожение, смешанные", Group = "Цели")]
    public List<ObjectiveDef> Objectives { get; set; } = new();
    [Ed("Логика целей", "Как обязательные цели дают победу", Group = "Цели")] public ObjectiveLogic ObjectiveLogic { get; set; } = ObjectiveLogic.AllRequired;
    [Ed("Маркеры появления", Group = "Волны")] public ScenarioWarningsDef SpawnWarnings { get; set; } = new();
    [Ed("Триггеры", Group = "Триггеры")] public List<TriggerDef> Triggers { get; set; } = new();
    [Ed("Переопределение правил", "Ограничения сценария", Group = "Ограничения")] public ScenarioLimitsDef Limits { get; set; } = new();
}

public sealed class ScenarioLimitsDef
{
    [Ed("Совместное нахождение на гексе", "По настройкам типов юнитов / запрещено / разрешено всем")] public StackingMode Stacking { get; set; } = StackingMode.ByUnitType;
    [Ed("Туман войны")] public bool FogOfWar { get; set; } = true;
    [Ed("Лимит времени, с", "0 = без лимита (по истечении — поражение)"), EdRange(0, 36000)] public double TimeLimit { get; set; }
}
