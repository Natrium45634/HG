using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

/// <summary>A single cell of a map definition. Nullable fields override the terrain type.</summary>
public sealed class MapCellDef : ContentPart
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

public sealed class MapMarkerDef : ContentPart
{
    [Ed("ID метки")] public string Id { get; set; } = "";
    [Ed("Тип")] public MarkerType Type { get; set; } = MarkerType.PlayerStart;
    [Ed("Клетка")] public HexCoord C { get; set; }
    [Ed("Подпись")] public string Label { get; set; } = "";
    [Ed("Радиус"), EdRange(0, 20)] public int Radius { get; set; }
    [Ed("Маркер предупреждения", "Показывать линию пути перед появлением врагов из этой точки")] public bool SpawnWarning { get; set; } = true;
}

public sealed class MapRulesDef : ContentPart
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

public sealed class ScenarioSideDef : ContentPart
{
    [Ed("Фракция"), EdRef(DefCategory.Faction, AllowEmpty = false)] public string Faction { get; set; } = "player";
    [Ed("Управление")] public SideController Controller { get; set; } = SideController.Player;
}

public sealed class ScenarioUnitDef : ContentPart
{
    [Ed("Шаблон юнита"), EdRef(DefCategory.Unit, AllowEmpty = false)] public string Unit { get; set; } = "";
    [Ed("Фракция", "Пусто — из шаблона"), EdRef(DefCategory.Faction)] public string Faction { get; set; } = "";
    [Ed("Клетка")] public HexCoord C { get; set; }
    [Ed("Направление")] public HexDirection Facing { get; set; } = HexDirection.E;
    [Ed("Метка-ID", "Для условий сценария")] public string Tag { get; set; } = "";
    [Ed("Профиль ИИ", "Пусто — из шаблона"), EdRef(DefCategory.AiProfile)] public string AiProfile { get; set; } = "";
    [Ed("Имя", "Пусто — из шаблона")] public string Name { get; set; } = "";
    [Ed("Уровень", "0 — уровень шаблона; иначе характеристики экземпляра подгоняются")] public int Level { get; set; }
    [Ed("Свои фазы босса", "Профиль фаз этого юнита вместо фаз шаблона (ТЗ ч. 6, 8.1); пусто — из шаблона")] public BossDef? Boss { get; set; }
}

public sealed class WaveUnitDef : ContentPart
{
    [Ed("Шаблон юнита"), EdRef(DefCategory.Unit, AllowEmpty = false)] public string Unit { get; set; } = "";
    [Ed("Количество"), EdRange(1, 100)] public int Count { get; set; } = 1;
    [Ed("Метка-ID", "Для условий и целей (например, boss)")] public string Tag { get; set; } = "";
    [Ed("Профиль ИИ", "Пусто — из шаблона"), EdRef(DefCategory.AiProfile)] public string AiProfile { get; set; } = "";
}

/// <summary>How a group spreads its enemies over its routes (docs/DECISIONS_4.md §57).</summary>
public enum GroupRouteSelect
{
    [Ed("Один маршрут")] Single,
    [Ed("По очереди")] RoundRobin,
    [Ed("Случайно (ГСЧ боя)")] Random,
    // Part 5 (ТЗ ч. 5, 8.3; docs/DECISIONS_5.md §86): the final base — chosen when the enemy appears.
    [Ed("Ближайшая достижимая база", "Цель маршрута ближе всех к месту появления, путь к ней открыт")] NearestBase,
    [Ed("Кратчайший путь", "Маршрут с наименьшим числом гексов")] ShortestRoute,
    [Ed("База с наименьшим здоровьем", "Доля здоровья цели маршрута в момент появления")] LowestHp,
    [Ed("Случайно по весам (seed)", "Вес — у каждого маршрута")] Weighted,
}

/// <summary>
/// A group of appearance (docs/DECISIONS_4.md §58): identical instances of one template on one schedule, from a spawn
/// base along a route to a target. The wave stores groups, not every enemy.
/// </summary>
public sealed class SpawnGroupDef : ContentPart
{
    [Ed("ID")] public string Id { get; set; } = "";
    [Ed("Название", "Пусто — по шаблону")] public string Name { get; set; } = "";
    [Ed("Шаблон юнита"), EdRef(DefCategory.Unit, AllowEmpty = false)] public string Unit { get; set; } = "";
    [Ed("Включена")] public bool Enabled { get; set; } = true;
    [Ed("Количество"), EdRange(1, 10_000)] public int Count { get; set; } = 1;
    [Ed("Задержка, с", "От начала волны"), EdRange(0, 3600)] public double Delay { get; set; }
    [Ed("Интервал, с", "Между врагами группы (0 — сразу)"), EdRange(0, 600)] public double Interval { get; set; } = 1;
    [Ed("Уровень экземпляров", "0 — уровень шаблона; иначе характеристики экземпляров подгоняются (шаблон не меняется)"), EdRange(0, 1000)] public int Level { get; set; }
    [Ed("Уровень до", "Больше «уровня» — случайно в диапазоне (по seed)"), EdRange(0, 1000)] public int LevelMax { get; set; }
    [Ed("База спавна", "ID объекта сценария")] public string SpawnBase { get; set; } = "";
    [Ed("Маршрут", "ID маршрута сценария")] public string Route { get; set; } = "";
    [Ed("Цель", "ID объекта; пусто — цель маршрута")] public string Target { get; set; } = "";
    [Ed("Фракция", "Пусто — фракция волны"), EdRef(DefCategory.Faction)] public string Faction { get; set; } = "";
    [Ed("Профиль ИИ", "Пусто — из шаблона"), EdRef(DefCategory.AiProfile)] public string AiProfile { get; set; } = "";
    [Ed("Метка-ID", "Для условий и целей")] public string Tag { get; set; } = "";
    // Stage 2 (docs/DECISIONS_4.md §58): subgroups, limits, several bases, repeats, local modifiers.
    [Ed("Размер подгруппы", "Враги выходят пачками по N, между пачками — пауза; 0 — без подгрупп"), EdRange(0, 1000)] public int SubgroupSize { get; set; }
    [Ed("Пауза между подгруппами, с"), EdRange(0, 600)] public double SubgroupPause { get; set; } = 5;
    [Ed("Лимит живых группы", "Одновременно живых врагов группы; 0 — без лимита"), EdRange(0, 10_000)] public int MaxAlive { get; set; }
    [Ed("Доп. базы спавна", "Враги распределяются по базам по очереди (основная — первая)")] public List<string> ExtraBases { get; set; } = new();
    [Ed("Повторов", "Сколько раз группа выходит снова (0 — один раз)"), EdRange(0, 100)] public int Repeats { get; set; }
    [Ed("Повтор через, с", "От начала предыдущего выхода группы"), EdRange(1, 3600)] public double RepeatEvery { get; set; } = 30;
    [Ed("Модификаторы экземпляров", "Только у врагов этой группы; шаблон не меняется")] public List<StatModDef> StatMods { get; set; } = new();
    [Ed("Условие отмены", "Выполнено — оставшиеся враги группы не появятся")] public ConditionDef? CancelCondition { get; set; }
    [Ed("Из пресета", "ID пресета, из которого создана группа (для справки: группа — копия, удаление пресета её не меняет)")] public string Preset { get; set; } = "";
    [Ed("Подобрана по бюджету", "Создана подбором по бюджету угрозы; заменяется при новом закреплении")] public bool FromBudget { get; set; }
    // Stage 3 (docs/DECISIONS_4.md §57): several routes for one group.
    [Ed("Другие маршруты", "Враги группы распределяются между основным и этими маршрутами")] public List<string> AltRoutes { get; set; } = new();
    [Ed("Выбор маршрута (базы)", "Если указаны другие маршруты; при уничтожении выбранной базы — альтернативные цели")] public GroupRouteSelect RouteSelect { get; set; } = GroupRouteSelect.RoundRobin;
    [Ed("Охотится", "Режим «Смешанный»: враги группы ищут юнитов игрока, маршрут — только путь подхода (в «Штурме» охотятся все)")]
    public bool Hunt { get; set; }

    /// <summary>The main route and the other ones (empty IDs skipped).</summary>
    public List<string> Routes()
    {
        var list = new List<string> { Route };
        foreach (var r in AltRoutes)
            if (r != "" && !list.Contains(r))
                list.Add(r);
        return list;
    }

    /// <summary>Bases in turn: the main one first, then the extra ones (empty IDs skipped).</summary>
    public List<string> Bases()
    {
        var list = new List<string>();
        if (SpawnBase != "") list.Add(SpawnBase);
        foreach (var b in ExtraBases)
            if (b != "" && !list.Contains(b))
                list.Add(b);
        return list;
    }

    /// <summary>
    /// Seconds from the start of the wave to the k-th enemy of one run of the group: interval inside a subgroup,
    /// the pause between subgroups.
    /// </summary>
    public double OffsetOf(int k)
    {
        int gaps = SubgroupSize > 0 ? k / SubgroupSize : 0;
        return Delay + (k - gaps) * Interval + gaps * SubgroupPause;
    }

    public override string ToString() => $"{(Name == "" ? Unit : Name)} ×{Count}" + (Repeats > 0 ? $" ×{Repeats + 1} раз" : "");
}

/// <summary>Difficulty of a wave (docs/DECISIONS_4.md §58): multipliers on its enemies, the templates stay.</summary>
public sealed class WaveDifficultyDef : ContentPart
{
    [Ed("Здоровье ×"), EdRange(0.1, 100)] public double Health { get; set; } = 1;
    [Ed("Сила и магия ×", "Сила и сила магии врагов волны"), EdRange(0.1, 100)] public double Power { get; set; } = 1;
    [Ed("Скорость ×"), EdRange(0.1, 10)] public double Speed { get; set; } = 1;
    [Ed("Количество ×", "Количество в каждой группе (округление вверх)"), EdRange(0.1, 100)] public double Count { get; set; } = 1;

    [JsonIgnore] public bool IsDefault => Health == 1 && Power == 1 && Speed == 1 && Count == 1;

    public override string ToString() => IsDefault ? "обычная" : $"здоровье ×{Health:0.##}, сила ×{Power:0.##}, скорость ×{Speed:0.##}, количество ×{Count:0.##}";
}

/// <summary>
/// A wave composed by a threat budget (docs/DECISIONS_4.md §58): the author sets the budget and the limits; the
/// composition is picked deterministically by the seed and can be pinned — then it no longer changes.
/// </summary>
public sealed class WaveBudgetDef : ContentPart
{
    [Ed("Подбирать состав по бюджету")] public bool Enabled { get; set; }
    [Ed("Бюджет угрозы", "Сумма стоимостей угрозы врагов"), EdRange(0, 1_000_000)] public double Budget { get; set; } = 20;
    [Ed("Мин. врагов"), EdRange(0, 1000)] public int MinUnits { get; set; } = 3;
    [Ed("Макс. врагов"), EdRange(1, 1000)] public int MaxUnits { get; set; } = 15;
    [Ed("Категории существ", "Пусто — любые (например, beast, humanoid)")] public List<string> Categories { get; set; } = new();
    [Ed("Обязательные теги", "У шаблона должны быть все")] public List<string> RequiredTags { get; set; } = new();
    [Ed("Запрещённые теги")] public List<string> ForbiddenTags { get; set; } = new();
    [Ed("Макс. доля элитных", "Элитный — тег «elite» или босс"), EdRange(0, 1)] public double MaxEliteShare { get; set; } = 0.25;
    [Ed("Лидер", "Обязательный шаблон: первым в составе"), EdRef(DefCategory.Unit)] public string Leader { get; set; } = "";
    [Ed("Предел стоимости шаблона", "0 — без предела"), EdRange(0, 100_000)] public double MaxUnitCost { get; set; }
    [Ed("Типы перемещения", "Пусто — любые")] public List<string> MoveCategories { get; set; } = new();
    [Ed("База спавна", "ID объекта сценария")] public string SpawnBase { get; set; } = "";
    [Ed("Маршрут", "ID маршрута сценария")] public string Route { get; set; } = "";
    [Ed("Интервал, с", "Между врагами подобранного состава"), EdRange(0, 600)] public double Interval { get; set; } = 1;
    [Ed("Seed подбора", "0 — от seed сценария и номера волны")] public ulong Seed { get; set; }
    [Ed("Состав закреплён", "Закреплённый состав хранится группами волны и при запуске не подбирается заново")] public bool Pinned { get; set; }

    public override string ToString() => Enabled ? $"бюджет {Budget:0.#}" + (Pinned ? " (закреплён)" : "") : "нет";
}

public enum WaveStart
{
    [Ed("По времени")] Time,
    [Ed("После зачистки предыдущей волны")] AfterPrevious,
    [Ed("По условию (вход в область, устройство, событие…)")] OnCondition,
    [Ed("Только действием триггера")] Manual,
    [Ed("После окончания появления предыдущей", "Предыдущая выпустила всех врагов; её враги ещё могут быть живы")] AfterPreviousSpawned,
}

public sealed class WaveDef : ContentPart
{
    [Ed("Название")] public string Name { get; set; } = "Волна";
    [Ed("Начало")] public WaveStart Start { get; set; } = WaveStart.Time;
    [Ed("Время начала, с"), EdRange(0, 36000), EdShowIf(nameof(Start), WaveStart.Time)] public double Time { get; set; } = 30;
    [Ed("Задержка, с", "После зачистки предыдущей / выполнения условия (маркер предупреждения добавляется сверху)"), EdRange(0, 3600),
     EdShowIf(nameof(Start), WaveStart.AfterPrevious, WaveStart.OnCondition, WaveStart.AfterPreviousSpawned)]
    public double Delay { get; set; }
    [Ed("Условие начала"), EdShowIf(nameof(Start), WaveStart.OnCondition)] public ConditionDef StartCondition { get; set; } = new() { Type = ConditionType.UnitInArea };
    [Ed("Метка появления", "ID метки карты (точка появления)")] public string SpawnMarker { get; set; } = "";
    [Ed("Доп. точки появления", "Враги распределяются по точкам по очереди")] public List<string> SpawnMarkers { get; set; } = new();
    [Ed("Маршрут", "ID маршрута карты: враги идут по нему")] public string Route { get; set; } = "";
    [Ed("Интервал появления, с", "Между врагами волны (0 — все сразу)"), EdRange(0, 600)] public double Interval { get; set; }
    [Ed("Лимит одновременно живых", "0 — без лимита; остальные ждут свободного места"), EdRange(0, 500)] public int MaxAlive { get; set; }
    [Ed("Фракция"), EdRef(DefCategory.Faction, AllowEmpty = false)] public string Faction { get; set; } = "enemy";
    [Ed("Юниты", "Состав без баз спавна (старый вид волны)")] public List<WaveUnitDef> Units { get; set; } = new();
    [Ed("Группы", "Группы появления с баз спавна по маршрутам сценария")] public List<SpawnGroupDef> Groups { get; set; } = new();
    [Ed("Своё условие завершения", "Иначе — волна зачищена, когда все её враги появились и уничтожены")] public bool CustomEnd { get; set; }
    [Ed("Условие завершения"), EdShowIf(nameof(CustomEnd), true)] public ConditionDef EndCondition { get; set; } = new() { Type = ConditionType.TimeElapsed };
    [Ed("При начале")] public List<TriggerActionDef> OnStart { get; set; } = new();
    [Ed("При завершении")] public List<TriggerActionDef> OnEnd { get; set; } = new();
    [Ed("Маркер предупреждения", "Линия будущего пути перед появлением")] public bool Warning { get; set; } = true;
    [Ed("Мин. предупреждение, с", "0 — из правил"), EdRange(0, 600)] public double WarningSeconds { get; set; }
    [Ed("Сообщение")] public string Message { get; set; } = "";
    // Stage 2 (docs/DECISIONS_4.md §58).
    [Ed("ID волны", "Стабильный; для событий, отчётов и пресетов")] public string Id { get; set; } = "";
    [Ed("Макс. продолжительность, с", "0 — без ограничения; по истечении оставшиеся появления отменяются"), EdRange(0, 36000)] public double MaxDuration { get; set; }
    [Ed("Мин. интервал до следующей, с", "Следующая волна (кроме волн по времени) не начнётся раньше — от начала этой"), EdRange(0, 3600)] public double MinGapNext { get; set; }
    [Ed("Условие прерывания", "Выполнено — волна отменяется, оставшиеся враги не появятся")] public ConditionDef? CancelCondition { get; set; }
    [Ed("Сложность", "Множители врагов волны")] public WaveDifficultyDef Difficulty { get; set; } = new();
    [Ed("Бюджет угрозы", "Подбор состава по бюджету")] public WaveBudgetDef Budget { get; set; } = new();

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
    // Part 4.
    [Ed("Переменная сценария равна")] Environment,
    // Part 6 (§100).
    [Ed("Объект в состоянии")] ObjectInState,
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

public sealed class ConditionDef : ContentPart
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
    [Ed("Переменная"), EdShowIf(nameof(Type), ConditionType.Environment)] public string Variable { get; set; } = "";
    [Ed("Значение", "Равно ли значение переменной этому"), EdShowIf(nameof(Type), ConditionType.Environment)] public string VariableValue { get; set; } = "";
    [Ed("Доля здоровья"), EdRange(0, 1), EdShowIf(nameof(Type), ConditionType.HpBelow)] public double Fraction { get; set; } = 0.5;
    [Ed("Объект (ID)"), EdShowIf(nameof(Type), ConditionType.ObjectInState), JsonOmitEmpty] public string ObjectId { get; set; } = "";
    [Ed("Состояние (ID)"), EdShowIf(nameof(Type), ConditionType.ObjectInState), JsonOmitEmpty] public string ObjectState { get; set; } = "";

    public string Describe() => Type switch
    {
        ConditionType.Environment => $"{Variable} = {VariableValue}",
        ConditionType.ObjectInState => $"Объект «{ObjectId}» в состоянии «{ObjectState}»",
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
    // Part 4 (docs/DECISIONS_4.md §62): variables of the scenario (time of day, weather).
    [Ed("Изменить переменную сценария")] SetEnvironment,
    // Part 4, stage 5 (docs/DECISIONS_4.md §68): bases, groups, targets, objects, the end, diagnostics.
    [Ed("Активировать базу спавна")] ActivateBase,
    [Ed("Отключить базу спавна")] DeactivateBase,
    [Ed("Изменить маршрут группы")] SetGroupRoute,
    [Ed("Установить цель")] SetTarget,
    [Ed("Изменить состояние объекта")] SetObjectState,
    [Ed("Завершить сценарий")] EndScenario,
    [Ed("Записать диагностическое событие")] LogDiagnostic,
    // Part 6, stage 6.2 (docs/DECISIONS_6.md §97): the zones of deployment open and close during the battle.
    [Ed("Открыть зону развёртывания")] OpenDeploymentZone,
    [Ed("Закрыть зону развёртывания")] CloseDeploymentZone,
    // Part 6, stage 6.5 (§100).
    [Ed("Перевести объект в состояние", "Компонент «Состояния»")] SwitchObjectState,
    // Part 6, stage 6.6 (§101): what the graph of events needs from the triggers.
    [Ed("Запустить триггер через N с")] DelayedTrigger,
    [Ed("Если — то триггер, иначе — триггер", "Условие проверяется один раз")] IfElse,
    [Ed("Снять флаг события")] ClearFlag,
    // Part 6, stage 6.10 (§104).
    [Ed("Изменить шкалу", "Боевой дух, санити… выбранных юнитов на величину")] ChangeScale,
}

public enum ActionTarget
{
    [Ed("Владелец (босс / источник события)")] Self,
    [Ed("Юниты с меткой")] UnitTag,
    [Ed("Все враги игрока")] Enemies,
    [Ed("Все юниты игрока")] PlayerUnits,
}

public sealed class TriggerActionDef : ContentPart
{
    [Ed("Действие")] public TriggerActionType Type { get; set; } = TriggerActionType.Message;
    [Ed("Текст / ID", "Сообщение; имя триггера; ID визуального или звукового эффекта"),
     EdShowIf(nameof(Type), TriggerActionType.Message, TriggerActionType.Victory, TriggerActionType.Defeat, TriggerActionType.Autopause,
         TriggerActionType.ActivateTrigger, TriggerActionType.PlayVfx, TriggerActionType.PlaySound, TriggerActionType.EndScenario, TriggerActionType.LogDiagnostic,
         TriggerActionType.DelayedTrigger, TriggerActionType.IfElse)]
    public string Text { get; set; } = "";
    [Ed("Через, с"), EdRange(0, 36000), EdShowIf(nameof(Type), TriggerActionType.DelayedTrigger)] public double Delay { get; set; } = 1;
    [Ed("Условие «если»"), EdShowIf(nameof(Type), TriggerActionType.IfElse)] public ConditionDef? If { get; set; }
    [Ed("Иначе — триггер", "Пусто — ничего"), EdShowIf(nameof(Type), TriggerActionType.IfElse), JsonOmitEmpty] public string ElseTrigger { get; set; } = "";
    /// <summary>The node of a graph of events the action was compiled from («граф/узел»; the debugger marks it when it runs).</summary>
    [Ed("Узел графа"), EdHidden, JsonOmitEmpty] public string Node { get; set; } = "";
    [Ed("Объект", "ID базы или объекта сценария; для зон — ID зоны развёртывания"),
     EdShowIf(nameof(Type), TriggerActionType.ActivateBase, TriggerActionType.DeactivateBase, TriggerActionType.SetTarget, TriggerActionType.SetObjectState,
         TriggerActionType.OpenDeploymentZone, TriggerActionType.CloseDeploymentZone, TriggerActionType.SwitchObjectState)]
    public string Object { get; set; } = "";
    [Ed("Состояние объекта", "ID; пусто — следующее"), EdShowIf(nameof(Type), TriggerActionType.SwitchObjectState), JsonOmitEmpty] public string ObjectStateId { get; set; } = "";
    [Ed("Группа волны", "ID группы"), EdShowIf(nameof(Type), TriggerActionType.SetGroupRoute)] public string Group { get; set; } = "";
    [Ed("Цель-юниты", "Метка или тег юнитов, которых атаковать (вместо объекта)"), EdShowIf(nameof(Type), TriggerActionType.SetTarget)] public string TargetTag { get; set; } = "";
    [Ed("Новое состояние"), EdShowIf(nameof(Type), TriggerActionType.SetObjectState)] public ObjectStateChange ObjectState { get; set; } = ObjectStateChange.Activate;
    [Ed("Итог"), EdShowIf(nameof(Type), TriggerActionType.EndScenario)] public bool Win { get; set; } = true;
    [Ed("Волна (индекс)"), EdRange(0, 1000), EdShowIf(nameof(Type), TriggerActionType.SpawnWave, TriggerActionType.CancelWave)] public int Wave { get; set; }
    [Ed("Эффект клетки"), EdRef(DefCategory.CellEffect), EdShowIf(nameof(Type), TriggerActionType.AddCellEffect)] public string CellEffect { get; set; } = "";
    [Ed("Метка карты", "Пусто — вокруг владельца"),
     EdShowIf(nameof(Type), TriggerActionType.AddCellEffect, TriggerActionType.ChangeTerrain, TriggerActionType.SummonUnits, TriggerActionType.DestroyObstacle,
         TriggerActionType.PlayVfx, TriggerActionType.PlaySound)]
    public string Marker { get; set; } = "";
    [Ed("Радиус"), EdRange(0, 20), EdShowIf(nameof(Type), TriggerActionType.ChangeTerrain, TriggerActionType.SummonUnits, TriggerActionType.DestroyObstacle)]
    public int Radius { get; set; } = 1;
    [Ed("Маршрут", "Маршрут сценария или карты"), EdShowIf(nameof(Type), TriggerActionType.SetRoute, TriggerActionType.SetGroupRoute)] public string Route { get; set; } = "";
    [Ed("Фаза босса (номер)", "1 — первая фаза из списка босса"), EdRange(1, 50), EdShowIf(nameof(Type), TriggerActionType.SetBossPhase)] public int Phase { get; set; } = 1;
    [Ed("Операции", "Каталог операций навыков: применяются к выбранным юнитам"), EdShowIf(nameof(Type), TriggerActionType.Effects)]
    public List<SkillEffectDef> Effects { get; set; } = new();
    [Ed("Флаг"), EdShowIf(nameof(Type), TriggerActionType.SetFlag, TriggerActionType.ClearFlag)] public string Flag { get; set; } = "";
    [Ed("Переменная"), EdShowIf(nameof(Type), TriggerActionType.SetEnvironment)] public string Variable { get; set; } = "";
    [Ed("Значение переменной"), EdShowIf(nameof(Type), TriggerActionType.SetEnvironment)] public string VariableValue { get; set; } = "";
    [Ed("Цель (ID)"), EdShowIf(nameof(Type), TriggerActionType.CompleteObjective, TriggerActionType.FailObjective)] public string Objective { get; set; } = "";
    [Ed("Местность"), EdRef(DefCategory.Terrain), EdShowIf(nameof(Type), TriggerActionType.ChangeTerrain)] public string Terrain { get; set; } = "";
    [Ed("Юнит"), EdRef(DefCategory.Unit), EdShowIf(nameof(Type), TriggerActionType.SummonUnits)] public string Unit { get; set; } = "";
    [Ed("Количество"), EdRange(1, 100), EdShowIf(nameof(Type), TriggerActionType.SummonUnits)] public int Count { get; set; } = 1;
    [Ed("На кого"),
     EdShowIf(nameof(Type), TriggerActionType.ApplyStatus, TriggerActionType.RemoveStatus, TriggerActionType.AddSkill, TriggerActionType.RemoveSkill,
         TriggerActionType.SetAutoAttackSkill, TriggerActionType.SetAiProfile, TriggerActionType.HealPercent, TriggerActionType.Effects, TriggerActionType.SetRoute,
         TriggerActionType.SetBossPhase, TriggerActionType.SetTarget, TriggerActionType.ChangeScale)]
    public ActionTarget Target { get; set; } = ActionTarget.Self;
    [Ed("Метка юнита"), EdShowIf(nameof(Target), ActionTarget.UnitTag)] public string UnitTag { get; set; } = "";
    [Ed("Состояние"), EdRef(DefCategory.Status), EdShowIf(nameof(Type), TriggerActionType.ApplyStatus, TriggerActionType.RemoveStatus)] public string Status { get; set; } = "";
    [Ed("Величина", "Очки/стаки состояния, % лечения, изменение шкалы"), EdShowIf(nameof(Type), TriggerActionType.ApplyStatus, TriggerActionType.HealPercent, TriggerActionType.ChangeScale)] public double Amount { get; set; } = 1;
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
        TriggerActionType.SetEnvironment => $"{Variable} = {VariableValue}",
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
        TriggerActionType.ChangeScale => $"Шкала {Amount:+0.##;-0.##}",
        TriggerActionType.PlayVfx => $"Визуальный эффект {Text}",
        TriggerActionType.PlaySound => $"Звук {Text}",
        TriggerActionType.ActivateBase => $"Включить базу «{Object}»",
        TriggerActionType.DeactivateBase => $"Отключить базу «{Object}»",
        TriggerActionType.SetGroupRoute => $"Группа «{Group}» → маршрут «{Route}»",
        TriggerActionType.SetTarget => TargetTag != "" ? $"Цель → юниты «{TargetTag}»" : $"Цель → «{Object}»",
        TriggerActionType.SetObjectState => $"Объект «{Object}»: {ObjectState}",
        TriggerActionType.EndScenario => (Win ? "Завершить победой" : "Завершить поражением") + (Text != "" ? $" «{Text}»" : ""),
        TriggerActionType.LogDiagnostic => $"Диагностика «{Text}»",
        TriggerActionType.SwitchObjectState => $"Объект «{Object}» → {(ObjectStateId != "" ? $"«{ObjectStateId}»" : "следующее состояние")}",
        TriggerActionType.DelayedTrigger => $"Через {Delay:0.##} с — триггер «{Text}»",
        TriggerActionType.IfElse => $"Если {If?.Describe() ?? "?"} — «{Text}»" + (ElseTrigger != "" ? $", иначе «{ElseTrigger}»" : ""),
        TriggerActionType.ClearFlag => $"Снять флаг «{Flag}»",
        TriggerActionType.OpenDeploymentZone => $"Открыть зону «{Object}»",
        TriggerActionType.CloseDeploymentZone => $"Закрыть зону «{Object}»",
        _ => Type.ToString(),
    };
}

public sealed class TriggerDef : ContentPart
{
    [Ed("Название")] public string Name { get; set; } = "Триггер";
    // Part 4 (docs/DECISIONS_4.md §68): event → conditions → actions; without an event — the condition every tick.
    [Ed("Событие", "Нет — условие проверяется каждый тик (как раньше)")] public ScenarioEventType Event { get; set; } = ScenarioEventType.None;
    [Ed("Фильтр: объект или ID", "База, объект, узел-контрольная точка, состояние, навык, пассив; пусто — любой"),
     EdShowIf(nameof(Event), ScenarioEventType.UnitSpawned, ScenarioEventType.UnitDied, ScenarioEventType.GoalReached, ScenarioEventType.BaseDestroyed,
         ScenarioEventType.ObjectActivated, ScenarioEventType.CheckpointReached, ScenarioEventType.StatusApplied, ScenarioEventType.SkillUsed,
         ScenarioEventType.PassiveTriggered, ScenarioEventType.ObjectStateChanged)]
    public string EventObject { get; set; } = "";
    [Ed("Фильтр: волна", "Номер волны с 1; 0 — любая"), EdRange(0, 1000),
     EdShowIf(nameof(Event), ScenarioEventType.WaveStarted, ScenarioEventType.WaveEnded, ScenarioEventType.UnitSpawned, ScenarioEventType.UnitDied,
         ScenarioEventType.GoalReached, ScenarioEventType.AliveLimitExceeded)]
    public int EventWave { get; set; }
    [Ed("Фильтр: группа", "ID группы волны; пусто — любая"),
     EdShowIf(nameof(Event), ScenarioEventType.UnitSpawned, ScenarioEventType.UnitDied, ScenarioEventType.GoalReached, ScenarioEventType.CheckpointReached)]
    public string EventGroup { get; set; } = "";
    [Ed("Фильтр: юнит", "Метка, «tag:x», категория или ID шаблона; пусто — любой"),
     EdShowIf(nameof(Event), ScenarioEventType.UnitSpawned, ScenarioEventType.UnitDied, ScenarioEventType.GoalReached, ScenarioEventType.CheckpointReached,
         ScenarioEventType.StatusApplied, ScenarioEventType.SkillUsed, ScenarioEventType.PassiveTriggered)]
    public string EventUnit { get; set; } = "";
    [Ed("Таймер, с", "С начала боя (после подготовки); повторяемый — каждые N с"), EdRange(0.1, 36000), EdShowIf(nameof(Event), ScenarioEventType.Timer)]
    public double Interval { get; set; } = 10;
    [Ed("Условие")] public ConditionDef Condition { get; set; } = new() { Type = ConditionType.TimeElapsed };
    [Ed("Действия")] public List<TriggerActionDef> Actions { get; set; } = new();
    [Ed("Однократный")] public bool Once { get; set; } = true;
    [Ed("Не чаще, с", "Предел частоты повторяемого триггера; 0 — без предела"), EdRange(0, 3600), EdShowIf(nameof(Once), false)] public double Cooldown { get; set; }
    [Ed("Не более срабатываний", "0 — без предела"), EdRange(0, 100_000), EdShowIf(nameof(Once), false)] public int MaxFires { get; set; }
    // Part 6, stage 6.6 (§101).
    [Ed("Только запуском", "Сам не проверяется: его запускает другой триггер («Запустить триггер», «через N с», «если — то»)"), JsonOmitEmpty]
    public bool ActivationOnly { get; set; }
    [Ed("Проверять условие при запуске", "Иначе запуск другим триггером выполняет действия без условия (как раньше)"), JsonOmitEmpty]
    public bool CheckOnActivate { get; set; }
    /// <summary>The node of a graph of events it was compiled from («граф/узел»; empty — written by hand).</summary>
    [Ed("Узел графа"), EdHidden, JsonOmitEmpty] public string GraphNode { get; set; } = "";
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
    // Part 4 (docs/DECISIONS_4.md §56–58): objects of the scenario, the route graph, the defeat by the bases.
    [Ed("Объекты", "Базы игрока и спавна, точки, сооружения, цели, зоны", Group = "Объекты"), EdHidden] public List<ScenarioObjectDef> Objects { get; set; } = new();
    [Ed("Маршруты", "Граф маршрутов врагов", Group = "Объекты"), EdHidden] public List<RouteDef> Routes { get; set; } = new();
    [Ed("Поражение по базам", "Считается вместе с условиями поражения", Group = "Условия")] public BaseDefeatDef BaseDefeat { get; set; } = new();
    [Ed("Волны", Group = "Волны")] public List<WaveDef> Waves { get; set; } = new();
    [Ed("Условия победы", "Любое выполненное — победа", Group = "Условия")] public List<ConditionDef> Victory { get; set; } = new() { new ConditionDef() };
    [Ed("Условия поражения", "Любое выполненное — поражение", Group = "Условия")] public List<ConditionDef> Defeat { get; set; } = new() { new ConditionDef { Type = ConditionType.AllPlayerUnitsDead } };
    [Ed("Цели", "Типы сценариев: зачистка, оборона, босс, выживание, волны, сопровождение, захват, уничтожение, смешанные", Group = "Цели")]
    public List<ObjectiveDef> Objectives { get; set; } = new();
    [Ed("Логика целей", "Как обязательные цели дают победу", Group = "Цели")] public ObjectiveLogic ObjectiveLogic { get; set; } = ObjectiveLogic.AllRequired;
    [Ed("Маркеры появления", Group = "Волны")] public ScenarioWarningsDef SpawnWarnings { get; set; } = new();
    [Ed("Триггеры", Group = "Триггеры")] public List<TriggerDef> Triggers { get; set; } = new();
    [Ed("Пассивы сценария", "Выдаются юнитам стороны или с тегом", Group = "Пассивы")] public List<ScenarioPassiveDef> Passives { get; set; } = new();
    [Ed("Переменные сценария", "Время суток, погода…: меняются действием триггера, проверяются условиями (docs/DECISIONS_4.md §62)", Group = "Пассивы")]
    public List<EnvironmentVarDef> Environment { get; set; } = new();
    [Ed("Отключить или заменить пассивы", "Например, талант расы в этом сценарии", Group = "Пассивы")] public List<PassiveOverrideDef> PassiveOverrides { get; set; } = new();
    [Ed("Переопределение правил", "Ограничения сценария", Group = "Ограничения")] public ScenarioLimitsDef Limits { get; set; } = new();
    // Part 4, stage 5 (docs/DECISIONS_4.md §64): modes, preparation, alternative targets of the enemies.
    [Ed("Режим боя", Group = "Автобой")] public BattleMode Mode { get; set; } = BattleMode.Normal;
    // Part 6 (ТЗ ч. 6, 1–2; docs/DECISIONS_6.md §96).
    [Ed("Правила режима", "Свободная RTS, тактический TD, гибрид…: какие системы части 6 включены; пусто — как раньше", Group = "Режим"),
     EdRef(DefCategory.GameRules), JsonOmitEmpty]
    public string GameRules { get; set; } = "";
    [Ed("Свои значения правил режима", "Заданное поле сильнее пресета; незаданное наследуется", Group = "Режим")]
    public GameRulesOverrideDef? GameRulesOverride { get; set; }
    [Ed("Отряд игрока", "Кого игрок берёт в бой: размер, теги, лимиты, обязательные бойцы", Group = "Режим")]
    public ScenarioSquadDef? Squad { get; set; }
    [Ed("Свои правила развёртывания", "Задано — весь блок вместо блока правил режима", Group = "Режим")]
    public DeploymentRulesDef? DeploymentOverride { get; set; }
    [Ed("Зоны развёртывания", "Где сторона вызывает бойцов; пусто — зона расстановки", Group = "Режим"), EdHidden, JsonOmitEmpty]
    public List<DeploymentZoneDef> DeploymentZones { get; set; } = new();
    // Part 6, stage 6.6 (§101).
    [Ed("Графы событий", "Редактируются на вкладке «Граф событий»; компилируются в триггеры при запуске боя"), EdHidden, JsonOmitEmpty]
    public List<RuleGraphDef> Graphs { get; set; } = new();
    // Part 6, stage 6.7 (§102).
    [Ed("Телеграфы атак", "Предупреждение перед навыком: не меньше N с, область, контур, таймер; удар решает бой (ТЗ ч. 6, 8.5)", Group = "Юниты"), JsonOmitEmpty]
    public List<TelegraphRuleDef> Telegraphs { get; set; } = new();
    // Part 6, stage 6.4 (docs/DECISIONS_6.md §99).
    [Ed("Наборы модификаторов", "Свои наборы сценария — действуют всегда, до наборов варианта", Group = "Режим"), EdRef(DefCategory.ModifierSet), JsonOmitEmpty]
    public List<string> ModifierSets { get; set; } = new();
    /// <summary>The variant applied to this copy (set only on the copy a battle is made from).</summary>
    [Ed("Применён вариант"), EdHidden, JsonOmitEmpty] public string AppliedVariant { get; set; } = "";
    /// <summary>The changes of units of the applied sets (a copy only): the battle applies them through the engine of modifiers.</summary>
    [Ed("Модификаторы юнитов варианта"), EdHidden, JsonOmitEmpty] public List<MissionModifierDef> AppliedModifiers { get; set; } = new();
    /// <summary>What the applied sets change, for the sign of the variant in battle (the modifiers marked «Показывать в бою»).</summary>
    [Ed("Изменения варианта"), EdHidden, JsonOmitEmpty] public List<string> AppliedNotes { get; set; } = new();
    [Ed("Подготовка и расстановка", "TD с расстановкой и автотест", Group = "Автобой")] public PlacementDef Placement { get; set; } = new();
    [Ed("Альтернативные цели врагов", "Если база уничтожена или недоступна и в профиле ИИ ничего не задано", Group = "Автобой")]
    public List<AltTargetDef> AltTargets { get; set; } = new() { new AltTargetDef { Kind = AltTargetKind.OtherBase }, new AltTargetDef { Kind = AltTargetKind.AttackDefenders } };
    [Ed("База недоступна через, с", "Маршрут закрыт дольше — враг выбирает альтернативную цель; 0 — никогда", Group = "Автобой"), EdRange(0, 3600)]
    public double AltAfterBlocked { get; set; } = 15;

    public ScenarioObjectDef? Object(string id) => Objects.FirstOrDefault(o => o.Id == id);
    public RouteDef? Route(string id) => Routes.FirstOrDefault(r => r.Id == id);
}

public sealed class ScenarioLimitsDef : ContentPart
{
    [Ed("Совместное нахождение на гексе", "По настройкам типов юнитов / запрещено / разрешено всем")] public StackingMode Stacking { get; set; } = StackingMode.ByUnitType;
    [Ed("Туман войны")] public bool FogOfWar { get; set; } = true;
    [Ed("Лимит времени, с", "0 = без лимита (по истечении — поражение)"), EdRange(0, 36000)] public double TimeLimit { get; set; }
    [Ed("Лимит живых врагов", "Одновременно на поле по всем волнам; 0 — без лимита (остальные ждут)"), EdRange(0, 10_000)] public int MaxAliveEnemies { get; set; }
    [Ed("Лимит созданных врагов", "За весь бой; 0 — без лимита (оставшиеся появления отменяются)"), EdRange(0, 100_000)] public int MaxCreatedEnemies { get; set; }
}
