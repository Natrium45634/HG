using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace HexTactics.Core.Content;

public enum CellEffectVisual
{
    Glow,
    Flames,
    Gas,
    Ice,
    Sparks,
    Liquid,
    Smoke,
    Runes,
    Barrier,
}

public enum CellStackPolicy
{
    Refresh,
    AddLevel,
    AddDuration,
    Ignore,
    Replace,
}

public sealed class SpreadDef
{
    [Ed("Распространяется")] public bool Enabled { get; set; }
    [Ed("Интервал, с"), EdRange(0.05, 3600), EdShowIf(nameof(Enabled), true)] public double Interval { get; set; } = 1;
    [Ed("Шанс на соседа"), EdRange(0, 1), EdShowIf(nameof(Enabled), true)] public double Chance { get; set; } = 0.3;
    [Ed("Только на местность с тегами", "Пусто — любая допустимая"), EdShowIf(nameof(Enabled), true)]
    public List<string> ToTerrainTags { get; set; } = new();
    [Ed("Только в клетки с эффектами", "Id или tag:тег; пусто — любые"), EdRef(DefCategory.CellEffect, AllowTags = true), EdShowIf(nameof(Enabled), true)]
    public List<string> ToCellsWithEffects { get; set; } = new();
    [Ed("Макс. поколений", "Защита от бесконечного распространения"), EdRange(1, 100), EdShowIf(nameof(Enabled), true)]
    public int MaxGenerations { get; set; } = 3;
    [Ed("Длительность потомка", "Доля длительности родителя"), EdRange(0.05, 1), EdShowIf(nameof(Enabled), true)]
    public double ChildDurationFactor { get; set; } = 0.7;
}

public sealed class CellEffectDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.CellEffect;

    [Ed("Длительность, с", "0 = постоянный", Group = "Время"), EdRange(0, 36000)] public double Duration { get; set; } = 8;
    [Ed("Интервал тика, с", Group = "Время"), EdRange(0.05, 3600)] public double TickInterval { get; set; } = 1;

    [Ed("Повторное наложение", Group = "Наложение")] public CellStackPolicy StackPolicy { get; set; } = CellStackPolicy.Refresh;
    [Ed("Макс. уровень", Group = "Наложение"), EdRange(1, 100)] public int MaxLevel { get; set; } = 3;
    [Ed("Эффекты тика масштабируются уровнем", Group = "Наложение")] public bool ScaleWithLevel { get; set; } = true;

    [Ed("Каждый тик (на юнита в клетке)", "Нахождение в клетке", Group = "Действие")] public List<SkillEffectDef> OnTick { get; set; } = new();
    [Ed("При входе юнита", Group = "Действие")] public List<SkillEffectDef> OnEnter { get; set; } = new();
    [Ed("Исчезает после срабатывания при входе", "Ловушки и т. п. (заряд расходуется)", Group = "Действие")] public bool ConsumeOnEnter { get; set; }
    [Ed("При выходе юнита", Group = "Действие")] public List<SkillEffectDef> OnExit { get; set; } = new();
    [Ed("По соседству", "Каждый интервал — на юнитов в радиусе (кроме самой клетки)", Group = "Действие")] public List<SkillEffectDef> OnAdjacent { get; set; } = new();
    [Ed("Радиус соседства", Group = "Действие"), EdRange(1, 10)] public int AdjacentRadius { get; set; } = 1;
    [Ed("Интервал соседства, с", Group = "Действие"), EdRange(0.05, 3600)] public double AdjacentInterval { get; set; } = 1;
    [Ed("Действует на слои", "Пусто — по правилам (наземный)", Group = "Ограничения"), EdRef(DefCategory.Layer)] public List<string> AffectsLayers { get; set; } = new();

    [Ed("Разрешён на местности с тегами", "Пусто — на любой", Group = "Ограничения")] public List<string> AllowedTerrainTags { get; set; } = new();
    [Ed("Запрещён на местности с тегами", Group = "Ограничения")] public List<string> ForbiddenTerrainTags { get; set; } = new();
    [Ed("Иммунные категории существ", Group = "Ограничения")] public List<string> ImmuneCreatureCategories { get; set; } = new();

    [Ed("Доп. стоимость перемещения", Group = "Влияние"), EdRange(-10, 100)] public double MoveCostAdd { get; set; }
    [Ed("Блокирует движение", "Барьер", Group = "Влияние")] public bool BlocksMovement { get; set; }
    [Ed("Блокирует обзор", "Дым, туман", Group = "Влияние")] public bool BlocksLoS { get; set; }
    [Ed("Модификатор замечания атак", "Прибавка к шансу заметить атаку для юнитов в клетке/через неё", Group = "Влияние"), EdRange(-1, 1)]
    public double NoticeModifier { get; set; }
    [Ed("Опасный", "ИИ избегает", Group = "Влияние")] public bool Hazard { get; set; }

    [Ed("Распространение", Group = "Распространение")] public SpreadDef Spread { get; set; } = new();

    [Ed("Цвет", Group = "Вид"), EdColor] public string Color { get; set; } = "#e07030";
    [Ed("Стиль", Group = "Вид")] public CellEffectVisual Visual { get; set; } = CellEffectVisual.Glow;
    [Ed("Значок", "1–2 символа", Group = "Вид")] public string Icon { get; set; } = "";
    [Ed("Приоритет обработки", "Меньше — раньше", Group = "Прочее")] public int Priority { get; set; }
}

public enum SynergyOperandKind
{
    CellEffect,
    CellEffectTag,
    TerrainTag,
}

public sealed class SynergyOperandDef
{
    [Ed("Тип операнда")] public SynergyOperandKind Kind { get; set; } = SynergyOperandKind.CellEffect;
    [Ed("Значение", "Id эффекта клетки или тег")] public string Value { get; set; } = "";

    public override string ToString() => Kind switch
    {
        SynergyOperandKind.CellEffect => Value,
        SynergyOperandKind.CellEffectTag => "эффект#" + Value,
        _ => "местность#" + Value,
    };
}

public sealed class SynergyDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.Synergy;

    [Ed("Операнд A")] public SynergyOperandDef A { get; set; } = new();
    [Ed("Операнд B")] public SynergyOperandDef B { get; set; } = new();

    [Ed("Убрать A")] public bool RemoveA { get; set; } = true;
    [Ed("Убрать B", "Для местности — не применяется (используйте «Сменить местность»)")] public bool RemoveB { get; set; } = true;

    [Ed("Добавить эффекты клетки"), EdRef(DefCategory.CellEffect)] public List<string> AddEffects { get; set; } = new();
    [Ed("Сменить местность на"), EdRef(DefCategory.Terrain)] public string ConvertTerrain { get; set; } = "";
    [Ed("Радиус", "0 = только эта клетка"), EdRange(0, 10)] public int Radius { get; set; }
    [Ed("Эффекты на юнитов в радиусе")] public List<SkillEffectDef> UnitEffects { get; set; } = new();
    [Ed("Эффекты клеток в радиусе"), EdRef(DefCategory.CellEffect)] public List<string> RadiusCellEffects { get; set; } = new();

    [Ed("Кулдаун на клетку, с", "Защита от повторного срабатывания"), EdRange(0, 3600)] public double CooldownPerCell { get; set; } = 1;
    [Ed("Сообщение журнала")] public string LogText { get; set; } = "";
}

public enum StatusKind
{
    Timed,
    Accumulating,
}

public enum ReapplyPolicy
{
    Refresh,
    Extend,
    Stack,
    Intensify,
    Ignore,
    ReplaceIfStronger,
}

public sealed class StatusLevelDef
{
    [Ed("Название уровня")] public string Name { get; set; } = "";
    [Ed("Порог очков", "Для накопительных; для временных — номер стака"), EdRange(0, 100000)] public double Threshold { get; set; }
    [Ed("Модификаторы характеристик")] public List<StatModDef> StatMods { get; set; } = new();
    [Ed("Флаги контроля")] public ControlFlagsDef Flags { get; set; } = new();
    [Ed("Интервал тика, с"), EdRange(0.05, 3600)] public double TickInterval { get; set; } = 1;
    [Ed("Эффекты тика", "Урон, лечение и т. п. по владельцу")] public List<SkillEffectDef> OnTick { get; set; } = new();
    [Ed("Эффекты при достижении уровня")] public List<SkillEffectDef> OnEnter { get; set; } = new();
}

public sealed class StatusDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.Status;

    [Ed("Вид", Group = "Основное")] public StatusKind Kind { get; set; } = StatusKind.Timed;
    [Ed("Отрицательное", "Дебафф: снимается очищением, ИИ избегает", Group = "Основное")] public bool Negative { get; set; } = true;

    [Ed("Длительность, с", "0 = бессрочно", Group = "Временное"), EdRange(0, 36000), EdShowIf(nameof(Kind), StatusKind.Timed)]
    public double Duration { get; set; } = 5;
    [Ed("Повторное наложение", Group = "Временное"), EdShowIf(nameof(Kind), StatusKind.Timed)]
    public ReapplyPolicy Reapply { get; set; } = ReapplyPolicy.Refresh;
    [Ed("Макс. стаков/уровней", Group = "Временное"), EdRange(1, 100), EdShowIf(nameof(Kind), StatusKind.Timed)]
    public int MaxStacks { get; set; } = 1;
    [Ed("Макс. длительность, с", "Для продления", Group = "Временное"), EdRange(0, 36000), EdShowIf(nameof(Kind), StatusKind.Timed)]
    public double MaxDuration { get; set; } = 30;

    [Ed("Макс. очков", Group = "Накопление"), EdRange(1, 100000), EdShowIf(nameof(Kind), StatusKind.Accumulating)]
    public double MaxPoints { get; set; } = 100;
    [Ed("Спад очков/с", Group = "Накопление"), EdRange(0, 10000), EdShowIf(nameof(Kind), StatusKind.Accumulating)]
    public double DecayPerSecond { get; set; } = 2;

    [Ed("Уровни", "Для накопительных — по порогам очков; для временных — по стакам (1, 2…)", Group = "Уровни")]
    public List<StatusLevelDef> Levels { get; set; } = new();

    [Ed("Иммунные категории существ", Group = "Взаимодействия")] public List<string> ImmuneCreatureCategories { get; set; } = new();
    [Ed("Снимает состояния", "При наложении снимает указанные", Group = "Взаимодействия"), EdRef(DefCategory.Status)]
    public List<string> Cancels { get; set; } = new();
    [Ed("Блокируется состояниями", "Не накладывается, пока есть указанные", Group = "Взаимодействия"), EdRef(DefCategory.Status)]
    public List<string> BlockedBy { get; set; } = new();

    [Ed("Цвет", Group = "Вид"), EdColor] public string Color { get; set; } = "#80c040";
    [Ed("Значок", "1–2 символа", Group = "Вид")] public string Icon { get; set; } = "";
    [Ed("Показывать шкалу", Group = "Вид")] public bool ShowBar { get; set; } = true;
}
