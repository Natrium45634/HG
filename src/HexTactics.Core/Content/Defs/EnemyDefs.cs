using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HexTactics.Core.Content;

public enum FacingModel
{
    [Ed("Одно направление")] Single,
    [Ed("Несколько направлений")] Multiple,
    [Ed("Круговое восприятие")] Omni,
    [Ed("Без направления")] None,
}

public enum DetectionZoneShape
{
    [Ed("Нет (по дальности)")] None,
    [Ed("Конус")] Cone,
    [Ed("Сектор")] Sector,
    [Ed("Линия")] Line,
}

/// <summary>How a creature perceives its surroundings (docs/DECISIONS_2.md §23).</summary>
public sealed class PerceptionDef
{
    [Ed("Модель взгляда", "Одно направление (секторы как обычно), несколько направлений, круговое или без направления")]
    public FacingModel Model { get; set; } = FacingModel.Single;

    [Ed("Доп. направления", "Смещение от основного направления шагами по 60°: 3 — назад, 1 и 5 — по сторонам"),
     EdShowIf(nameof(Model), FacingModel.Multiple)]
    public List<int> ExtraFacings { get; set; } = new() { 3 };

    [Ed("Свои шансы без секторов", "Для «круговое»/«без направления»: заменить правило «без секторов» из правил боя"),
     EdShowIf(nameof(Model), FacingModel.Omni, FacingModel.None)]
    public bool OwnNoSectorRule { get; set; }

    [Ed("Шанс заметить атаку"), EdRange(0, 1), EdShowIf(nameof(OwnNoSectorRule), true)] public double NoticeChance { get; set; } = 1;
    [Ed("Шанс неожиданности"), EdRange(0, 1), EdShowIf(nameof(OwnNoSectorRule), true)] public double SurpriseChance { get; set; }

    [Ed("Зона обнаружения", "Если задана — видит только в этой зоне перед собой (и вплотную)")] public DetectionZoneShape Zone { get; set; } = DetectionZoneShape.None;
    [Ed("Размер зоны", "Длина/радиус в гексах"), EdRange(1, 30), EdShowIf(nameof(Zone), DetectionZoneShape.Cone, DetectionZoneShape.Sector, DetectionZoneShape.Line)]
    public int ZoneSize { get; set; } = 5;
    [Ed("Угол сектора, °"), EdRange(60, 360), EdShowIf(nameof(Zone), DetectionZoneShape.Sector)] public double ZoneAngle { get; set; } = 120;

    [JsonIgnore] public bool HasFacing => Model is FacingModel.Single or FacingModel.Multiple;
}

public enum Disclosure
{
    [Ed("Всегда")] Always,
    [Ed("Пока виден")] WhenVisible,
    [Ed("После обнаружения")] AfterSeen,
    [Ed("После наблюдения")] AfterObserved,
    [Ed("Никогда")] Never,
}

/// <summary>What the enemy info panel reveals, per category (docs/DECISIONS_2.md §24).</summary>
public sealed class InfoDisclosureDef
{
    [Ed("Панель доступна", "Открывать информационную панель по клику на врага")] public bool PanelEnabled { get; set; } = true;
    [Ed("Имя и тип")] public Disclosure NameAndType { get; set; } = Disclosure.AfterSeen;
    [Ed("Здоровье")] public Disclosure Health { get; set; } = Disclosure.WhenVisible;
    [Ed("Ресурсы")] public Disclosure Resources { get; set; } = Disclosure.WhenVisible;
    [Ed("Состояния и эффекты")] public Disclosure Statuses { get; set; } = Disclosure.WhenVisible;
    [Ed("Сопротивления и уязвимости", "«После наблюдения» — после попадания этим типом урона")] public Disclosure Resistances { get; set; } = Disclosure.AfterObserved;
    [Ed("Навыки", "«После наблюдения» — после того как игрок видел применение")] public Disclosure Skills { get; set; } = Disclosure.AfterObserved;
    [Ed("Направление взгляда")] public Disclosure Facing { get; set; } = Disclosure.WhenVisible;
    [Ed("Намерение", "Что готовит, куда идёт, кого атакует")] public Disclosure Intent { get; set; } = Disclosure.WhenVisible;
}

public sealed class InfoOverrideDef
{
    [Ed("Свои правила раскрытия", "Иначе — из правил боя")] public bool Override { get; set; }
    [Ed("Раскрытие"), EdShowIf(nameof(Override), true)] public InfoDisclosureDef Values { get; set; } = new();
}

public enum BlockZone
{
    [Ed("Все соседние клетки")] AllAdjacent,
    [Ed("Фронтальная дуга (фронт и ближние фланги)")] FrontArc,
    [Ed("Только своя клетка")] SameCell,
}

/// <summary>A unit that holds enemies back (docs/DECISIONS_2.md §27). The limit itself is the stat block_limit.</summary>
public sealed class BlockerDef
{
    [Ed("Зона блокирования")] public BlockZone Zone { get; set; } = BlockZone.AllAdjacent;
    [Ed("Блокирует категории", "Категории существ или tag:тег; пусто — любых")] public List<string> Categories { get; set; } = new();
    [Ed("Блокирует слои", "Пусто — наземный"), EdRef(DefCategory.Layer)] public List<string> Layers { get; set; } = new();
    [Ed("Особые способности", "Теги удержания (например heavy_guard): нужны для врагов, требующих особой способности")] public List<string> Abilities { get; set; } = new();
    [Ed("Блокирует в движении", "Иначе блок начинается, только когда юнит стоит")] public bool WhileMoving { get; set; }
}

public enum BlockedBehavior
{
    [Ed("Атаковать блокирующего")] AttackBlocker,
    [Ed("Ждать")] Wait,
    [Ed("Искать обход")] FindDetour,
}

/// <summary>How a unit reacts to being blocked.</summary>
public sealed class BlockableDef
{
    [Ed("Может быть заблокирован")] public bool CanBeBlocked { get; set; } = true;
    [Ed("Нужна особая способность", "Тег, который должен быть у блокирующего (пусто — не нужна)"), EdShowIf(nameof(CanBeBlocked), true)] public string RequiredAbility { get; set; } = "";
    [Ed("Занимает слотов", "Сколько единиц лимита блокирования расходует"), EdRange(1, 10), EdShowIf(nameof(CanBeBlocked), true)] public int SlotCost { get; set; } = 1;
    [Ed("Обходит блок", "Не блокируется и продолжает движение"), EdShowIf(nameof(CanBeBlocked), true)] public bool CanBypass { get; set; }
    [Ed("Поведение при блоке"), EdShowIf(nameof(CanBeBlocked), true)] public BlockedBehavior WhenBlocked { get; set; } = BlockedBehavior.AttackBlocker;
}
