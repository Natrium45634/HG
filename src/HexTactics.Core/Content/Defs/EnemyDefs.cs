using System.Collections.Generic;
using HexTactics.Core.Hex;
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
public sealed class PerceptionDef : ContentPart
{
    [Ed("Модель взгляда", "Одно направление (секторы как обычно), несколько направлений, круговое или без направления")]
    public FacingModel Model { get; set; } = FacingModel.Single;

    [Ed("Доп. направления", "Смещение от основного направления шагами по 60°: 3 — назад, 1 и 5 — по сторонам"),
     EdShowIf(nameof(Model), FacingModel.Multiple)]
    public List<int> ExtraFacings { get; set; } = new() { 3 };

    [Ed("Линии обзора", "Для «несколько направлений»: основная линия (обязательна), доп. линии с шириной и дальностью, слепые зоны. Пусто — «доп. направления»"),
     EdShowIf(nameof(Model), FacingModel.Multiple)]
    public List<SightLineDef> Lines { get; set; } = new();

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

    /// <summary>Sight lines in use: Multiple with lines; null — classic facing (+ extra facings).</summary>
    [JsonIgnore] public List<SightLineDef>? ActiveLines => Model == FacingModel.Multiple && Lines.Count > 0 ? Lines : null;

    /// <summary>The primary line (marked, or the first non-blind one).</summary>
    public SightLineDef? PrimaryLine()
    {
        foreach (var l in Lines)
            if (l.Primary && !l.Blind)
                return l;
        foreach (var l in Lines)
            if (!l.Blind)
                return l;
        return null;
    }

    /// <summary>Directions the creature faces (for attack sectors): the facing and the extra lines/facings.</summary>
    public IEnumerable<Hex.HexDirection> FacingsOf(Hex.HexDirection facing)
    {
        if (Model != FacingModel.Multiple)
        {
            yield return facing;
            yield break;
        }
        var lines = ActiveLines;
        if (lines == null)
        {
            yield return facing;
            foreach (var off in ExtraFacings) yield return facing.Rotate(off);
            yield break;
        }
        int primary = PrimaryLine()?.Offset ?? 0;
        foreach (var l in lines)
            if (!l.Blind)
                yield return l.DirectionFor(facing, primary);
    }
}

/// <summary>A line (sector) of sight of a creature with several directions of view (docs/DECISIONS_3.md §40).</summary>
public sealed class SightLineDef : ContentPart
{
    [Ed("Основная", "Основная линия взгляда: по ней задаётся направление юнита (стрелка)")] public bool Primary { get; set; }
    [Ed("Направление", "Шаги по 60° от основной линии (0 — она сама); для фиксированной — абсолютное направление: 0 — восток, 1 — северо-восток…"), EdRange(0, 5)]
    public int Offset { get; set; }
    [Ed("Ширина сектора, °", "60 — один клин гекса, 360 — круговой обзор"), EdRange(1, 360)] public double Width { get; set; } = 60;
    [Ed("Дальность", "0 — общая дальность обнаружения"), EdRange(0, 50)] public int Range { get; set; }
    [Ed("Слепая зона", "В этом секторе юнит не видит (кроме вплотную)")] public bool Blind { get; set; }
    [Ed("Фиксированная", "Не вращается вместе с основной линией")] public bool Fixed { get; set; }
    [Ed("Видит сквозь препятствия")] public bool ThroughObstacles { get; set; }

    public Hex.HexDirection DirectionFor(Hex.HexDirection facing, int primaryOffset) =>
        Fixed ? (Hex.HexDirection)(((Offset % 6) + 6) % 6) : facing.Rotate(Offset - primaryOffset);

    public override string ToString() => (Primary ? "основная " : Blind ? "слепая " : "") + $"{Offset}·60° {Width:0}°" + (Range > 0 ? $" ≤{Range}" : "");
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
public sealed class InfoDisclosureDef : ContentPart
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

public sealed class InfoOverrideDef : ContentPart
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

/// <summary>Whom a blocker takes first when it cannot take everyone (ТЗ ч. 6, 4.2 «blockTargetPriority»).</summary>
public enum BlockTargetOrder
{
    [Ed("Ближайших")] Nearest,
    [Ed("Самых тяжёлых (больше слотов)")] Heaviest,
    [Ed("Самых лёгких (меньше слотов)")] Lightest,
    [Ed("С наименьшим здоровьем")] LowestHealth,
}

/// <summary>
/// A unit that holds enemies back (docs/DECISIONS_2.md §27; part 6 — docs/DECISIONS_6.md §98). The limit itself is the
/// stat block_limit, so passives and statuses change it like any stat.
/// </summary>
public sealed class BlockerDef : ContentPart
{
    [Ed("Зона блокирования")] public BlockZone Zone { get; set; } = BlockZone.AllAdjacent;
    [Ed("Дальность, гексов", "1 — соседние гексы; больше — длинное оружие, щит-поле (блок — контакт по правилу, не занятие клетки)"), EdRange(1, 5)]
    public int Range { get; set; } = 1;
    [Ed("Блокирует категории", "Категории существ или tag:тег; пусто — любых")] public List<string> Categories { get; set; } = new();
    [Ed("Не блокирует категории", "Категории существ или tag:тег — этих не держит, даже если подходят выше")] public List<string> ExceptCategories { get; set; } = new();
    [Ed("Блокирует слои", "Пусто — наземный"), EdRef(DefCategory.Layer)] public List<string> Layers { get; set; } = new();
    [Ed("Особые способности", "Теги удержания (например heavy_guard): нужны для врагов, требующих особой способности")] public List<string> Abilities { get; set; } = new();
    [Ed("Блокирует в движении", "Иначе блок начинается, только когда юнит стоит")] public bool WhileMoving { get; set; }
    [Ed("Блокирует под оглушением", "Иначе оглушение снимает все его связи")] public bool WhileStunned { get; set; }
    [Ed("Кого первым", "Когда слотов не хватает на всех")] public BlockTargetOrder TargetOrder { get; set; } = BlockTargetOrder.Nearest;
    [Ed("Приоритет", "Два блокирующих на одного врага: больший приоритет, затем ближе, затем меньший ID"), EdRange(-100, 100)]
    public int Priority { get; set; }
    [Ed("Держит не дольше, с", "0 — сколько угодно; потом враг проходит"), EdRange(0, 600)] public double MaxDuration { get; set; }
    [Ed("После этого пропускает на, с", "Сколько отпущенного по времени врага никто не блокирует"), EdRange(0, 60)] public double PassTime { get; set; } = 2;
}

public enum BlockedBehavior
{
    [Ed("Атаковать блокирующего")] AttackBlocker,
    [Ed("Только стоять")] Wait,
    [Ed("Искать обход")] FindDetour,
    [Ed("Прорыв навыком", "Навык прорыва по блокирующему; если недоступен — обычная атака")] BreakThrough,
}

/// <summary>How a unit reacts to being blocked.</summary>
public sealed class BlockableDef : ContentPart
{
    [Ed("Может быть заблокирован")] public bool CanBeBlocked { get; set; } = true;
    [Ed("Нужна особая способность", "Тег, который должен быть у блокирующего (пусто — не нужна)"), EdShowIf(nameof(CanBeBlocked), true)] public string RequiredAbility { get; set; } = "";
    [Ed("Занимает слотов", "Сколько единиц лимита блокирования расходует"), EdRange(1, 10), EdShowIf(nameof(CanBeBlocked), true)] public int SlotCost { get; set; } = 1;
    [Ed("Обходит блок", "Не блокируется и продолжает движение (стены и ямы — по-прежнему препятствие)"), EdShowIf(nameof(CanBeBlocked), true)] public bool CanBypass { get; set; }
    [Ed("Не блокируется теми, кто", "Категории или tag:тег блокирующих, которых он не замечает"), EdShowIf(nameof(CanBeBlocked), true)]
    public List<string> IgnoreBlockers { get; set; } = new();
    [Ed("Поведение при блоке"), EdShowIf(nameof(CanBeBlocked), true)] public BlockedBehavior WhenBlocked { get; set; } = BlockedBehavior.AttackBlocker;
    [Ed("Навык прорыва", "Пусто — лучшая атака"), EdRef(DefCategory.Skill), EdShowIf(nameof(WhenBlocked), BlockedBehavior.BreakThrough)]
    public string BreakSkill { get; set; } = "";
}
