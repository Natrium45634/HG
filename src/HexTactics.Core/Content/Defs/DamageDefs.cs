using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HexTactics.Core.Content;

// Unified damage model (docs/DECISIONS_2.md §29): damage types with a category, a parent type and
// an element; elements with inheritance; defensive and offensive modifiers; shields; per-source rules.

public enum DamageCategory
{
    [Ed("Физический")] Physical,
    [Ed("Магический")] Magical,
    [Ed("Элементальный")] Elemental,
    [Ed("Окружение")] Environment,
    [Ed("Периодический")] Periodic,
    [Ed("Пользовательский")] Custom,
}

public sealed class DamageTypeDef : Def, IInheritable
{
    [JsonIgnore] public override DefCategory Category => DefCategory.DamageType;

    [Ed("Категория", "Физический, магический, элементальный, окружение, периодический или пользовательский")]
    public DamageCategory Kind { get; set; } = DamageCategory.Physical;
    [Ed("Своя категория", "Для пользовательской категории: её имя (модификаторы «категория: имя»)"), EdShowIf(nameof(Kind), DamageCategory.Custom)]
    public string CustomCategory { get; set; } = "";
    [Ed("Родительский тип", "Режущий, дробящий, колющий — потомки физического: модификаторы родителя действуют и на них"), EdRef(DefCategory.DamageType)]
    public string Parent { get; set; } = "";
    [Ed("Элемент", "Стихия урона: модификаторы элемента и его предков действуют на этот тип"), EdRef(DefCategory.Element)]
    public string Element { get; set; } = "";
    [Ed("Цвет", "Пусто — цвет элемента или родителя"), EdColor, EdInherit] public string Color { get; set; } = "";
    [Ed("Значок", "1–2 символа; пусто — от элемента или родителя"), EdInherit] public string Icon { get; set; } = "";
    [Ed("Учитывает броню", "Броня вычитается из урона этого типа")] public bool ArmorApplies { get; set; }
    [Ed("Пробивает щиты", "Урон этого типа проходит мимо щитов")] public bool IgnoresShields { get; set; }

    public string? InheritedHint(ContentDatabase db, string property) => DamageTaxonomy.InheritedHint(db, this, property);
}

public enum ElementTier
{
    [Ed("Базовый")] Basic,
    [Ed("Продвинутый")] Advanced,
    [Ed("Редкий")] Rare,
    [Ed("Уникальный")] Unique,
    [Ed("Пользовательский")] Custom,
}

/// <summary>A status applied by damage of an element (e.g. fire → «Горение»).</summary>
public sealed class ElementStatusDef
{
    [Ed("Состояние"), EdRef(DefCategory.Status, AllowEmpty = false)] public string Status { get; set; } = "";
    [Ed("Шанс"), EdRange(0, 1)] public double Chance { get; set; } = 1;
    [Ed("Очки/стаки", "Для накопительных — очки за удар, для временных — стаки")] public double Amount { get; set; } = 10;

    public override string ToString() => Status;
    [Ed("Длительность, с", "0 — по умолчанию из состояния"), EdRange(0, 3600)] public double Duration { get; set; }
    [Ed("От доли урона", "Очки = урон × коэффициент (0 — фиксированные очки)"), EdRange(0, 100)] public double PerDamage { get; set; }
}

public enum InteractionPartner
{
    [Ed("Состояние цели с элементом")] TargetStatus,
    [Ed("Эффект клетки под целью с элементом")] CellEffect,
    [Ed("Любое из двух")] Either,
}

/// <summary>
/// Element reaction: damage of this element meets a status or cell effect of another element
/// (water + lightning → conduction, fire + water → steam…).
/// </summary>
public sealed class ElementInteractionDef
{
    [Ed("С элементом", "Срабатывает и для потомков этого элемента"), EdRef(DefCategory.Element, AllowEmpty = false)] public string With { get; set; } = "";
    [Ed("Где искать")] public InteractionPartner Partner { get; set; } = InteractionPartner.Either;
    [Ed("Изменение урона, %", "+50 — урон в 1,5 раза больше, −50 — вдвое меньше"), EdRange(-500, 500)] public double DamagePercent { get; set; }
    [Ed("Наложить состояние"), EdRef(DefCategory.Status)] public string ApplyStatus { get; set; } = "";
    [Ed("Очки/стаки состояния")] public double StatusAmount { get; set; } = 1;
    [Ed("Создать эффект клетки"), EdRef(DefCategory.CellEffect)] public string CreateCellEffect { get; set; } = "";
    [Ed("Снять партнёра", "Состояние/эффект клетки другого элемента исчезает")] public bool Consume { get; set; }
    [Ed("Сообщение журнала")] public string LogText { get; set; } = "";

    public override string ToString() => $"+{With} ({DamagePercent:+0;-0;0} %{(Consume ? ", снимает" : "")})";
}

public sealed class ElementDef : Def, IInheritable
{
    [JsonIgnore] public override DefCategory Category => DefCategory.Element;

    [Ed("Родительский элемент", "Незаданные поля наследуются от родителя"), EdRef(DefCategory.Element)] public string Parent { get; set; } = "";
    [Ed("Уровень", "Базовый, продвинутый, редкий, уникальный, пользовательский")] public ElementTier Tier { get; set; } = ElementTier.Basic;
    [Ed("Цвет", "Пусто — от родителя"), EdColor, EdInherit] public string Color { get; set; } = "";
    [Ed("Значок", "1–2 символа; пусто — от родителя"), EdInherit] public string Icon { get; set; } = "";
    [Ed("Визуальный эффект", "Имя эффекта/частиц; пусто — от родителя"), EdInherit] public string Vfx { get; set; } = "";
    [Ed("Типы урона", "Какие типы урона создаёт элемент (для справки и подсказок); пусто — от родителя"), EdRef(DefCategory.DamageType), EdInherit]
    public List<string> DamageTypes { get; set; } = new();
    [Ed("Накладывает состояния", "При уроне этого элемента; пусто — от родителя"), EdInherit] public List<ElementStatusDef> Statuses { get; set; } = new();
    [Ed("Создаёт эффекты клеток", "Под целью при уроне; пусто — от родителя"), EdRef(DefCategory.CellEffect), EdInherit]
    public List<string> CellEffects { get; set; } = new();
    [Ed("Шанс эффекта клетки"), EdRange(0, 1)] public double CellEffectChance { get; set; } = 1;
    [Ed("Взаимодействия", "Свои дополняют унаследованные; совпадение «с элементом» — переопределяет"), EdInherit] public List<ElementInteractionDef> Interactions { get; set; } = new();
    [Ed("Не наследовать списки", "Пустые списки остаются пустыми (не берутся у родителя)")] public bool NoListInheritance { get; set; }

    public string? InheritedHint(ContentDatabase db, string property) => DamageTaxonomy.InheritedHint(db, this, property);
}

/// <summary>
/// What a damage modifier applies to: "*" — any damage, a damage type id (with its descendants),
/// "el:id" — an element (with descendants), "cat:Physical" — a category, "tag:x" — damage types with a tag.
/// </summary>
public sealed class DamageModDef
{
    [Ed("К чему", "Любой урон, тип урона (и его потомки), элемент, категория или тег"), EdDamageMatch] public string Match { get; set; } = "*";
    [Ed("Модификатор, %", "Получаемый урон × (1 + V/100): +100 — двойной, +500 — шестикратный, −50 — вдвое меньше, −100 и ниже — нет урона (не лечит)"),
     EdRange(-500, 500)]
    public double Percent { get; set; }

    public override string ToString() => $"{Match} {Percent:+0;-0}%";
}

public sealed class FlatReductionDef
{
    [Ed("К чему"), EdDamageMatch] public string Match { get; set; } = "*";
    [Ed("Снижение за удар", "Отрицательное — дополнительный получаемый урон"), EdRange(-100000, 100000)] public double Amount { get; set; }
}

public sealed class ConversionDef
{
    [Ed("Из", "Какой урон преобразуется"), EdDamageMatch] public string From { get; set; } = "*";
    [Ed("В тип урона"), EdRef(DefCategory.DamageType, AllowEmpty = false)] public string To { get; set; } = "";
    [Ed("Доля, %", "Часть урона, которая становится другим типом (до сопротивлений)"), EdRange(0, 100)] public double Percent { get; set; } = 50;
}

public enum AbsorbInto
{
    [Ed("В здоровье")] Health,
    [Ed("В ресурс")] Resource,
    [Ed("Никуда (просто гасится)")] Nothing,
}

public enum AbsorbOrder
{
    [Ed("После щитов")] AfterShields,
    [Ed("До щитов")] BeforeShields,
}

public sealed class AbsorptionDef
{
    [Ed("К чему"), EdDamageMatch] public string Match { get; set; } = "*";
    [Ed("Поглощение, %", "Доля оставшегося урона, которая не наносится"), EdRange(0, 100)] public double Percent { get; set; } = 25;
    [Ed("Предел за удар/тик", "0 — без предела"), EdRange(0, 100000)] public double CapPerHit { get; set; }
    [Ed("Превращается в")] public AbsorbInto Into { get; set; } = AbsorbInto.Health;
    [Ed("Ресурс"), EdRef(DefCategory.Resource), EdShowIf(nameof(Into), AbsorbInto.Resource)] public string Resource { get; set; } = "";
    [Ed("Действует на периодический урон")] public bool AffectsPeriodic { get; set; } = true;
    [Ed("Порядок", "Относительно щитов")] public AbsorbOrder Order { get; set; } = AbsorbOrder.AfterShields;
}

public enum ReflectType
{
    [Ed("Исходный тип")] Original,
    [Ed("Тип после преобразования")] Converted,
    [Ed("Заданный тип")] Fixed,
}

public enum ReflectBase
{
    [Ed("От фактической потери здоровья")] HealthLost,
    [Ed("От урона до щитов")] BeforeShields,
}

public sealed class ReflectionDef
{
    [Ed("К чему"), EdDamageMatch] public string Match { get; set; } = "*";
    [Ed("Отражение, %"), EdRange(0, 500)] public double Percent { get; set; } = 20;
    [Ed("Тип отражённого урона")] public ReflectType Type { get; set; } = ReflectType.Original;
    [Ed("Заданный тип"), EdRef(DefCategory.DamageType), EdShowIf(nameof(Type), ReflectType.Fixed)] public string FixedType { get; set; } = "";
    [Ed("Считать от")] public ReflectBase Base { get; set; } = ReflectBase.HealthLost;
    [Ed("Отражает периодический урон")] public bool AffectsPeriodic { get; set; }
    [Ed("Только ближний бой", "Отражать только удары с соседней клетки")] public bool MeleeOnly { get; set; }
}

/// <summary>Defensive properties of a unit, a status level or a passive (they add up).</summary>
public sealed class DefenseDef
{
    [Ed("Сопротивления и уязвимости", "Модификаторы складываются; итог ограничен правилами (по умолчанию −500…+500 %)")]
    public List<DamageModDef> Modifiers { get; set; } = new();
    [Ed("Иммунитет к урону", "Тип, элемент, категория или тег — урон 0"), EdDamageMatch] public List<string> Immune { get; set; } = new();
    [Ed("Фиксированное снижение", "После сопротивлений и брони")] public List<FlatReductionDef> FlatReductions { get; set; } = new();
    [Ed("Преобразование входящего урона")] public List<ConversionDef> Conversions { get; set; } = new();
    [Ed("Поглощение")] public List<AbsorptionDef> Absorptions { get; set; } = new();
    [Ed("Отражение")] public List<ReflectionDef> Reflections { get; set; } = new();
    [Ed("Иммунитет к критам")] public bool CritImmune { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Modifiers.Count == 0 && Immune.Count == 0 && FlatReductions.Count == 0 && Conversions.Count == 0
                           && Absorptions.Count == 0 && Reflections.Count == 0 && !CritImmune;
}

/// <summary>Offensive properties: bonus damage by type and conversion of dealt damage.</summary>
public sealed class OffenseDef
{
    [Ed("Бонусы урона, %", "Наносимый урон × (1 + Σ/100) по типу, элементу или категории")]
    public List<DamageModDef> Bonuses { get; set; } = new();
    [Ed("Преобразование наносимого урона")] public List<ConversionDef> Conversions { get; set; } = new();

    [JsonIgnore] public bool IsEmpty => Bonuses.Count == 0 && Conversions.Count == 0;
}

public enum ShieldStacking
{
    [Ed("Обновить (тот же щит)")] Refresh,
    [Ed("Сложить объём")] Add,
    [Ed("Отдельный щит")] Separate,
    [Ed("Заменить, если сильнее")] ReplaceIfStronger,
}

/// <summary>A shield: a separate absorption pool (docs/DECISIONS_2.md §29).</summary>
public sealed class ShieldSpecDef
{
    [Ed("Название щита")] public string Name { get; set; } = "Щит";
    [Ed("Защищает от", "Пусто — от любого урона"), EdDamageMatch] public List<string> Protects { get; set; } = new();
    [Ed("Приоритет расхода", "Сначала расходуются щиты с большим приоритетом"), EdRange(-100, 100)] public int Priority { get; set; }
    [Ed("Восстановление/с", "0 — не восстанавливается"), EdRange(0, 10000)] public double RegenPerSecond { get; set; }
    [Ed("Задержка восстановления, с", "После получения урона"), EdRange(0, 3600)] public double RegenDelay { get; set; } = 2;
    [Ed("Повторное наложение")] public ShieldStacking Stacking { get; set; } = ShieldStacking.Refresh;
    [Ed("Пропускает периодический урон")] public bool IgnoresPeriodic { get; set; }
    [Ed("Цвет"), EdColor] public string Color { get; set; } = "#8fd8ff";
}

public enum HealMode
{
    [Ed("Фиксированное")] Flat,
    [Ed("% от максимума здоровья")] PercentMax,
    [Ed("% от недостающего здоровья")] PercentMissing,
}

/// <summary>Where damage comes from — kept apart from its type (fire from a skill, lava or burning).</summary>
public enum DamageSource
{
    [Ed("Навык")] Skill,
    [Ed("Автоатака")] AutoAttack,
    [Ed("Периодический (состояние)")] Periodic,
    [Ed("Эффект клетки (от навыка)")] CellEffect,
    [Ed("Местность")] Terrain,
    [Ed("Синергия")] Synergy,
    [Ed("Отражение")] Reflection,
    [Ed("Падение")] Fall,
    [Ed("Сценарий")] Scenario,
    [Ed("Пассивная способность")] Passive,
    [Ed("Прочее")] Other,
}

/// <summary>Per-source exceptions of the unified pipeline, all visible in the rules editor.</summary>
public sealed class DamageSourceRuleDef
{
    [Ed("Источник")] public DamageSource Source { get; set; }
    [Ed("Бросок попадания", "Для источников без собственного броска (навыки бросают сами)")] public bool RollHit { get; set; }
    [Ed("Может критовать")] public bool CanCrit { get; set; }
    [Ed("Бонусы атакующего", "Характеристики и бонусы урона того, кто вызвал урон")] public bool AttackerBonuses { get; set; } = true;
    [Ed("Учитывает броню", "Если тип урона учитывает броню")] public bool Armor { get; set; } = true;
    [Ed("Щиты")] public bool Shields { get; set; } = true;
    [Ed("Поглощение")] public bool Absorption { get; set; } = true;
    [Ed("Отражение")] public bool Reflection { get; set; } = true;
    [Ed("Взаимодействия элементов")] public bool ElementEffects { get; set; } = true;
}

public sealed class CritRulesDef
{
    [Ed("Бонус шанса: ближний бой"), EdRange(-1, 1)] public double MeleeBonus { get; set; }
    [Ed("Бонус шанса: дальний бой"), EdRange(-1, 1)] public double RangedBonus { get; set; }
    [Ed("Бонус шанса: заклинания"), EdRange(-1, 1)] public double SpellBonus { get; set; }
    [Ed("Бонус шанса: фланг"), EdRange(-1, 1)] public double FlankBonus { get; set; } = 0.05;
    [Ed("Бонус шанса: тыл"), EdRange(-1, 1)] public double RearBonus { get; set; } = 0.1;
    [Ed("Мин. множитель крита", "Множитель после защиты от критов не ниже"), EdRange(1, 10)] public double MinMultiplier { get; set; } = 1.1;
}

public sealed class DamageRulesDef
{
    [Ed("Нижний предел суммы модификаторов, %", "−500 — допускается полное гашение"), EdRange(-500, 0)] public double MinTotalPercent { get; set; } = -500;
    [Ed("Верхний предел суммы модификаторов, %"), EdRange(0, 500)] public double MaxTotalPercent { get; set; } = 500;
    [Ed("Правила источников", "Исключения единого порядка расчёта для каждого источника урона")]
    public List<DamageSourceRuleDef> Sources { get; set; } = DefaultSources();
    [Ed("Глубина цепочки отражения", "1 — отражённый урон больше не отражается"), EdRange(0, 5)] public int ReflectMaxDepth { get; set; } = 1;
    [Ed("Криты")] public CritRulesDef Crit { get; set; } = new();
    [Ed("Избыточное лечение → щит, %", "Доля излишка лечения, превращаемая в щит (0 — выкл.)"), EdRange(0, 100)] public double OverhealToShieldPercent { get; set; }
    [Ed("Макс. щит от излишка, % HP"), EdRange(0, 500)] public double OverhealShieldMaxPercent { get; set; } = 30;
    [Ed("Длительность щита от излишка, с"), EdRange(0, 3600)] public double OverhealShieldDuration { get; set; } = 8;
    [Ed("Подробный разбор в журнале", "В административном режиме — каждый шаг расчёта")] public bool AdminBreakdown { get; set; } = true;

    public DamageSourceRuleDef For(DamageSource s)
    {
        foreach (var r in Sources)
            if (r.Source == s)
                return r;
        foreach (var r in DefaultSources())
            if (r.Source == s)
                return r;
        return new DamageSourceRuleDef { Source = s };
    }

    public static List<DamageSourceRuleDef> DefaultSources() => new()
    {
        new() { Source = DamageSource.Skill, CanCrit = true },
        new() { Source = DamageSource.AutoAttack, CanCrit = true },
        new() { Source = DamageSource.Periodic, AttackerBonuses = false, Reflection = false },
        new() { Source = DamageSource.CellEffect, AttackerBonuses = false, Reflection = false },
        new() { Source = DamageSource.Terrain, AttackerBonuses = false, Reflection = false },
        new() { Source = DamageSource.Synergy, AttackerBonuses = false, Reflection = false },
        new() { Source = DamageSource.Reflection, AttackerBonuses = false, Armor = false, Reflection = false, ElementEffects = false },
        new() { Source = DamageSource.Fall, AttackerBonuses = false, Armor = false, Reflection = false, ElementEffects = false },
        new() { Source = DamageSource.Scenario, AttackerBonuses = false, Reflection = false },
        new() { Source = DamageSource.Passive, AttackerBonuses = false, Reflection = false },
        new() { Source = DamageSource.Other, AttackerBonuses = false, Reflection = false },
    };
}
