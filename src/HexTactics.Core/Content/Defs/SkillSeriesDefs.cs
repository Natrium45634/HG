using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace HexTactics.Core.Content;

// Skills as sequences (docs/DECISIONS_3.md §45): parameters of every hit of a series, conditions of the
// stages, costs of a series and defensive stances.

public enum StageAttackKind
{
    [Ed("Как у навыка")] Inherit,
    [Ed("Ближний бой")] Melee,
    [Ed("Дальний бой")] Ranged,
    [Ed("Заклинание")] Spell,
}

public enum StageHitMode
{
    [Ed("Как у навыка")] Inherit,
    [Ed("Бросок точности")] Roll,
    [Ed("Всегда попадает")] AlwaysHit,
}

public enum CritRule
{
    [Ed("Как у навыка")] Inherit,
    [Ed("Может критовать")] Allowed,
    [Ed("Без крита")] Never,
}

public enum RetargetRule
{
    [Ed("Как в серии")] Inherit,
    [Ed("Не менять (серия прекращается)")] Never,
    [Ed("Ближайшая к прежней цели")] Nearest,
    [Ed("По правилам выбора цели навыка")] ByRules,
}

public enum StageMove
{
    [Ed("Нет")] None,
    [Ed("Рывок к цели")] DashToTarget,
    [Ed("Отступление от цели")] Retreat,
}

public enum StageCostWhen
{
    [Ed("За удар (до удара)")] PerHit,
    [Ed("При попадании")] OnHit,
    [Ed("При промахе")] OnMiss,
    [Ed("Возврат при убийстве")] RefundOnKill,
    [Ed("Возврат при промахе")] RefundOnMiss,
}

public enum SeriesCritMode
{
    [Ed("Отдельно для каждого удара")] PerHit,
    [Ed("Один бросок на всю серию")] OncePerSeries,
}

public enum ResourceOutPolicy
{
    [Ed("Отменить оставшиеся этапы")] CancelRemaining,
    [Ed("Завершить текущий этап, затем отменить")] FinishStage,
    [Ed("Безопасно прекратить (выполнить завершающие этапы)")] SafeStop,
    [Ed("Продолжить в долг (ресурс с отрицательным минимумом)")] Debt,
}

public enum ConditionFailAction
{
    [Ed("Пропустить этап")] SkipStage,
    [Ed("Прекратить серию")] StopSeries,
}

/// <summary>A cost of one stage (hit) of a series. Empty resource — action points.</summary>
public sealed class StageCostDef : ContentPart
{
    [Ed("Ресурс", "Пусто — очки действия"), EdRef(DefCategory.Resource)] public string Resource { get; set; } = "";
    [Ed("Количество"), EdRange(0, 100000)] public double Amount { get; set; } = 1;
    [Ed("Когда")] public StageCostWhen When { get; set; } = StageCostWhen.PerHit;

    public override string ToString() => $"{(string.IsNullOrEmpty(Resource) ? "ОД" : Resource)} {Amount:0.#} ({When})";
}

// ------------------------------------------------------------------ conditions

public enum UnitConditionType
{
    [Ed("Всегда")] Always,
    [Ed("И (все вложенные)")] All,
    [Ed("ИЛИ (хотя бы одно)")] Any,
    [Ed("НЕ")] Not,
    [Ed("Цель жива")] TargetAlive,
    [Ed("Предыдущий удар попал")] PreviousHit,
    [Ed("Предыдущий эффект сработал")] PreviousEffect,
    [Ed("Есть состояние")] HasStatus,
    [Ed("Здоровье (доля)")] HpFraction,
    [Ed("Ресурс")] Resource,
    [Ed("Стоит на поверхности")] OnTerrain,
    [Ed("Тип существа")] UnitType,
    [Ed("Юнитов в области")] UnitsInArea,
    [Ed("Применил навык недавно")] AfterSkill,
    [Ed("Фаза босса")] BossPhase,
    [Ed("Условие сценария")] Scenario,
    // Part 4 (docs/DECISIONS_4.md §62).
    [Ed("Категория или тег")] Category,
    [Ed("Слой")] Layer,
    [Ed("Расстояние до цели")] Distance,
    [Ed("Переменная сценария")] Environment,
}

public enum ConditionSubject
{
    [Ed("Исполнитель / владелец")] Self,
    [Ed("Цель")] Target,
}

public enum AreaCentre
{
    [Ed("Точка навыка")] Anchor,
    [Ed("Исполнитель / владелец")] Self,
    [Ed("Цель")] Target,
}

public enum UnitSideFilter
{
    [Ed("Противники")] Enemies,
    [Ed("Союзники")] Allies,
    [Ed("Любые")] Any,
}

/// <summary>
/// Condition of a skill stage (and of a passive, docs/DECISIONS_3.md §46): a tree of AND/OR/NOT with leaves
/// about the caster/owner, the target, the previous hit and the area. Scenario conditions of part 2 are a leaf.
/// </summary>
public sealed class UnitConditionDef : ContentPart
{
    [Ed("Условие")] public UnitConditionType Type { get; set; } = UnitConditionType.TargetAlive;
    [Ed("Вложенные условия"), EdShowIf(nameof(Type), UnitConditionType.All, UnitConditionType.Any, UnitConditionType.Not)]
    public List<UnitConditionDef> Children { get; set; } = new();
    [Ed("О ком"), EdShowIf(nameof(Type), UnitConditionType.HasStatus, UnitConditionType.HpFraction, UnitConditionType.Resource, UnitConditionType.OnTerrain,
         UnitConditionType.UnitType, UnitConditionType.BossPhase, UnitConditionType.Category, UnitConditionType.Layer)]
    public ConditionSubject Subject { get; set; } = ConditionSubject.Target;
    [Ed("Состояние"), EdRef(DefCategory.Status), EdShowIf(nameof(Type), UnitConditionType.HasStatus)] public string Status { get; set; } = "";
    [Ed("Ресурс", "Пусто — очки действия"), EdRef(DefCategory.Resource), EdShowIf(nameof(Type), UnitConditionType.Resource)] public string Resource { get; set; } = "";
    [Ed("Сравнение"), EdShowIf(nameof(Type), UnitConditionType.HpFraction, UnitConditionType.Resource, UnitConditionType.UnitsInArea, UnitConditionType.BossPhase, UnitConditionType.Distance)]
    public CompareMode Compare { get; set; } = CompareMode.AtLeast;
    [Ed("Значение", "Доля здоровья 0..1; ресурс; для состояния — минимум очков/стаков (0 — любое); фаза босса — номер с 1"),
     EdShowIf(nameof(Type), UnitConditionType.HasStatus, UnitConditionType.HpFraction, UnitConditionType.Resource, UnitConditionType.BossPhase)]
    public double Value { get; set; } = 0.5;
    [Ed("Поверхности", "ID местности или эффекта клетки; «kind:Water» — вид местности"), EdShowIf(nameof(Type), UnitConditionType.OnTerrain)]
    public List<string> Terrains { get; set; } = new();
    [Ed("Типы", "Категория существа, ID юнита, «tag:тег» или «role:роль»"), EdShowIf(nameof(Type), UnitConditionType.UnitType)]
    public List<string> Types { get; set; } = new();
    [Ed("Центр области"), EdShowIf(nameof(Type), UnitConditionType.UnitsInArea)] public AreaCentre Centre { get; set; } = AreaCentre.Anchor;
    [Ed("Радиус", "Для «расстояние до цели» — гексов"), EdRange(0, 50), EdShowIf(nameof(Type), UnitConditionType.UnitsInArea, UnitConditionType.Distance)] public int Radius { get; set; } = 1;
    [Ed("Кто"), EdShowIf(nameof(Type), UnitConditionType.UnitsInArea)] public UnitSideFilter Side { get; set; } = UnitSideFilter.Enemies;
    [Ed("Количество"), EdRange(0, 1000), EdShowIf(nameof(Type), UnitConditionType.UnitsInArea)] public int Count { get; set; } = 2;
    [Ed("Навык"), EdRef(DefCategory.Skill), EdShowIf(nameof(Type), UnitConditionType.AfterSkill)] public string Skill { get; set; } = "";
    [Ed("В течение, с"), EdRange(0, 3600), EdShowIf(nameof(Type), UnitConditionType.AfterSkill)] public double Seconds { get; set; } = 5;
    [Ed("Условие сценария", "Без метки — для самого владельца"), EdShowIf(nameof(Type), UnitConditionType.Scenario)]
    public ConditionDef? ScenarioCondition { get; set; }
    [Ed("Категории и теги", "ID категории (с предками), «tag:тег», категория существа или ID шаблона"), EdShowIf(nameof(Type), UnitConditionType.Category)]
    public List<string> Categories { get; set; } = new();
    [Ed("Слой", "ID слоя (ground, air, underground…)"), EdShowIf(nameof(Type), UnitConditionType.Layer)] public string Layer { get; set; } = "";
    [Ed("Переменная"), EdShowIf(nameof(Type), UnitConditionType.Environment)] public string Variable { get; set; } = "";
    [Ed("Значение переменной"), EdShowIf(nameof(Type), UnitConditionType.Environment)] public string VariableValue { get; set; } = "";

    [JsonIgnore] public bool IsLogic => Type is UnitConditionType.All or UnitConditionType.Any or UnitConditionType.Not;

    /// <summary>Depth of the tree (validator: a cycle in hand-written JSON is impossible, a very deep tree is suspicious).</summary>
    public int Depth() => 1 + (Children.Count == 0 ? 0 : Children.Max(c => c.Depth()));

    public string Describe()
    {
        string who = Subject == ConditionSubject.Self ? "исполнитель" : "цель";
        string cmp = Compare == CompareMode.AtLeast ? "≥" : "≤";
        return Type switch
        {
            UnitConditionType.Always => "всегда",
            UnitConditionType.All => Children.Count == 0 ? "всегда" : "(" + string.Join(" И ", Children.Select(c => c.Describe())) + ")",
            UnitConditionType.Any => "(" + string.Join(" ИЛИ ", Children.Select(c => c.Describe())) + ")",
            UnitConditionType.Not => "НЕ " + (Children.Count == 1 ? Children[0].Describe() : "(" + string.Join(" И ", Children.Select(c => c.Describe())) + ")"),
            UnitConditionType.TargetAlive => "цель жива",
            UnitConditionType.PreviousHit => "предыдущий удар попал",
            UnitConditionType.PreviousEffect => "предыдущий эффект сработал",
            UnitConditionType.HasStatus => $"{who}: состояние «{Status}»" + (Value > 0 ? $" ≥ {Value:0.#}" : ""),
            UnitConditionType.HpFraction => $"{who}: здоровье {cmp} {Value:P0}",
            UnitConditionType.Resource => $"{who}: {(string.IsNullOrEmpty(Resource) ? "ОД" : Resource)} {cmp} {Value:0.#}",
            UnitConditionType.OnTerrain => $"{who} на поверхности: {string.Join(", ", Terrains)}",
            UnitConditionType.UnitType => $"{who} — {string.Join(", ", Types)}",
            UnitConditionType.UnitsInArea => $"{(Side == UnitSideFilter.Enemies ? "противников" : Side == UnitSideFilter.Allies ? "союзников" : "юнитов")} в радиусе {Radius} {cmp} {Count}",
            UnitConditionType.AfterSkill => $"применил «{Skill}» за последние {Seconds:0.#} с",
            UnitConditionType.BossPhase => $"{who}: фаза босса {cmp} {Value:0}",
            UnitConditionType.Scenario => "сценарий: " + (ScenarioCondition?.Describe() ?? "—"),
            UnitConditionType.Category => $"{who} — {string.Join(", ", Categories)}",
            UnitConditionType.Layer => $"{who} в слое «{Layer}»",
            UnitConditionType.Distance => $"расстояние до цели {cmp} {Radius}",
            UnitConditionType.Environment => $"{Variable} = {VariableValue}",
            _ => Type.ToString(),
        };
    }

    public override string ToString() => Describe();
}

// ------------------------------------------------------------------ defensive skills

public enum DefenseKind
{
    [Ed("Блок")] Block,
    [Ed("Уклонение")] Dodge,
    [Ed("Парирование")] Parry,
    [Ed("Защитная стойка")] Stance,
    [Ed("Временный щит")] Shield,
    [Ed("Снижение урона")] Reduction,
    [Ed("Отражение атаки")] Reflect,
    [Ed("Поглощение типа урона")] Absorb,
}

public enum PierceInteraction
{
    [Ed("Пробитие игнорирует защиту")] Ignore,
    [Ed("Пробитие ослабляет защиту вдвое")] Half,
    [Ed("Пробитие не влияет")] Unaffected,
}

/// <summary>
/// Parameters of a defensive skill (docs/DECISIONS_3.md §45): what the stance does while it lasts. Used when
/// the skill is the unit's «defensive skill» or its kind is «Защита»; it works inside the damage pipeline
/// (hit — dodge and parry; armor step — reduction and blocks).
/// </summary>
public sealed class DefenseStanceDef : ContentPart
{
    [Ed("Включена", "Навык создаёт защитное состояние")] public bool Enabled { get; set; }
    [Ed("Вид защиты", "Подпись, анимации и тексты"), EdShowIf(nameof(Enabled), true)] public DefenseKind Kind { get; set; } = DefenseKind.Stance;
    [Ed("Длительность, с", "0 — пока не отдан другой приказ"), EdRange(0, 3600), EdShowIf(nameof(Enabled), true)] public double Duration { get; set; }
    [Ed("Поддержание в секунду", "Ресурсы или ОД (пустой ресурс) в секунду; не хватает — защита заканчивается"), EdShowIf(nameof(Enabled), true)]
    public List<StageCostDef> Upkeep { get; set; } = new();
    [Ed("Снижение урона, %"), EdRange(0, 100), EdShowIf(nameof(Enabled), true)] public double DamageReduction { get; set; } = 30;
    [Ed("Типы урона", "Какие типы урона снижаются/поглощаются; пусто — все"), EdDamageMatch, EdShowIf(nameof(Enabled), true)]
    public List<string> DamageTypes { get; set; } = new();
    [Ed("Направления атак", "Из каких секторов атаки защита действует; пусто — со всех"), EdShowIf(nameof(Enabled), true)]
    public List<AttackSector> Sectors { get; set; } = new() { AttackSector.Front, AttackSector.NearFlank };
    [Ed("Слои атакующих", "Пусто — любые"), EdRef(DefCategory.Layer), EdShowIf(nameof(Enabled), true)] public List<string> AttackerLayers { get; set; } = new();
    [Ed("Шанс уклонения"), EdRange(0, 1), EdShowIf(nameof(Enabled), true)] public double DodgeChance { get; set; }
    [Ed("Шанс парирования"), EdRange(0, 1), EdShowIf(nameof(Enabled), true)] public double ParryChance { get; set; }
    [Ed("Контратака при парировании", "Базовая атака по атакующему, если он в её дальности"), EdShowIf(nameof(Enabled), true)] public bool CounterOnParry { get; set; }
    [Ed("Отражение, %", "Доля входящего урона, возвращаемая атакующему"), EdRange(0, 100), EdShowIf(nameof(Enabled), true)] public double ReflectPercent { get; set; }
    [Ed("Бонус уклонения", "К характеристике на время защиты"), EdRange(0, 1), EdShowIf(nameof(Enabled), true)] public double EvasionBonus { get; set; }
    [Ed("Бонус блока", "К шансу блока на время защиты"), EdRange(0, 1), EdShowIf(nameof(Enabled), true)] public double BlockBonus { get; set; }
    [Ed("Число атак", "Сколько атак защита может остановить или ослабить; 0 — без ограничения"), EdRange(0, 1000), EdShowIf(nameof(Enabled), true)]
    public int MaxBlocks { get; set; }
    [Ed("Пробитие брони", "Атаки навыков «пробивает защиту»"), EdShowIf(nameof(Enabled), true)] public PierceInteraction Pierce { get; set; } = PierceInteraction.Ignore;
    [Ed("Оглушение прерывает"), EdShowIf(nameof(Enabled), true)] public bool BrokenByStun { get; set; } = true;
    [Ed("Можно двигаться", "Защита сохраняется при перемещении (приказ стойки сразу завершается)"), EdShowIf(nameof(Enabled), true)] public bool AllowMove { get; set; }
    [Ed("Анимация начала", "Роль клипа модели"), EdShowIf(nameof(Enabled), true)] public AnimRole AnimStart { get; set; } = AnimRole.Block;
    [Ed("Анимация удержания"), EdShowIf(nameof(Enabled), true)] public AnimRole AnimHold { get; set; } = AnimRole.Block;
    [Ed("Анимация завершения"), EdShowIf(nameof(Enabled), true)] public AnimRole AnimEnd { get; set; } = AnimRole.Idle;

    public string Describe()
    {
        if (!Enabled) return "нет";
        var parts = new List<string>();
        if (DamageReduction > 0) parts.Add($"−{DamageReduction:0.#} % урона" + (DamageTypes.Count > 0 ? $" ({string.Join(", ", DamageTypes)})" : ""));
        if (DodgeChance > 0) parts.Add($"уклонение {DodgeChance:P0}");
        if (ParryChance > 0) parts.Add($"парирование {ParryChance:P0}" + (CounterOnParry ? " с контратакой" : ""));
        if (ReflectPercent > 0) parts.Add($"отражение {ReflectPercent:0.#} %");
        if (EvasionBonus > 0) parts.Add($"+{EvasionBonus:P0} уклонения");
        if (BlockBonus > 0) parts.Add($"+{BlockBonus:P0} блока");
        if (MaxBlocks > 0) parts.Add($"до {MaxBlocks} атак");
        parts.Add(Duration > 0 ? $"{Duration:0.#} с" : "до другого приказа");
        if (Sectors.Count > 0 && Sectors.Count < 4) parts.Add("сектора: " + string.Join(", ", Sectors));
        return string.Join(", ", parts);
    }
}
