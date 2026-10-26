using System.Text.Json.Serialization;
using System.Collections.Generic;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

/// <summary>Well-known stat ids used by the engine. Additional stats can be added as data.</summary>
public static class Stats
{
    public const string HpMax = "hp_max";
    public const string MoveSpeed = "move_speed";
    public const string ApMax = "ap_max";
    public const string ApRegen = "ap_regen";
    public const string DetectionRange = "detection_range";
    public const string Accuracy = "accuracy";
    public const string Evasion = "evasion";
    public const string BlockChance = "block_chance";
    public const string BlockReduction = "block_reduction";
    public const string Armor = "armor";
    public const string CritChance = "crit_chance";
    public const string CritMult = "crit_mult";
    public const string Perception = "perception";
    public const string Stealth = "stealth";
    public const string InterruptResist = "interrupt_resist";
    public const string Power = "power";
    public const string HealPower = "heal_power";
    /// <summary>How many enemy slots the unit can hold back (blocking, part 2 §27).</summary>
    public const string BlockLimit = "block_limit";
    public const string HealReceived = "heal_received";
    public const string CritResist = "crit_resist";
    public const string CritBonus = "crit_bonus";
    /// <summary>Magical power (part 3: target choice, modifiers).</summary>
    public const string MagicPower = "magic_power";
    // Part 3 (docs/DECISIONS_3.md §46): parameters passives and talents may change.
    /// <summary>Bonus to the maximum range of skills aimed at a unit or a cell (hexes).</summary>
    public const string AttackRange = "attack_range";
    /// <summary>Skill preparation is faster: time ÷ (1 + value).</summary>
    public const string CastSpeed = "cast_speed";
    /// <summary>Chance 0..1 to resist control statuses (stun, root, silence, fear, mind control).</summary>
    public const string ControlResist = "control_resist";
    /// <summary>Shields placed on the unit are stronger: × (1 + value).</summary>
    public const string ShieldPower = "shield_power";
    // Part 4 (docs/DECISIONS_4.md §56, §60).
    /// <summary>Damage to a base when the unit reaches it.</summary>
    public const string BaseDamage = "base_damage";
    // Part 4 (docs/DECISIONS_4.md §60).
    public const string Weight = "weight";
    public const string Size = "size";
    public const string PursuitRange = "pursuit_range";

    public static readonly string[] All =
    {
        HpMax, MoveSpeed, ApMax, ApRegen, DetectionRange, Accuracy, Evasion, BlockChance, BlockReduction,
        Armor, CritChance, CritMult, Perception, Stealth, InterruptResist, Power, HealPower, BlockLimit, HealReceived, CritResist, CritBonus, MagicPower,
        AttackRange, CastSpeed, ControlResist, ShieldPower, BaseDamage, Weight, Size, PursuitRange,
    };
}

public enum StatFormat
{
    Number,
    Percent,
}

public sealed class StatDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.Stat;

    [Ed("По умолчанию")] public double Default { get; set; }
    [Ed("Минимум")] public double Min { get; set; }
    [Ed("Максимум")] public double Max { get; set; } = 1000;
    [Ed("Формат")] public StatFormat Format { get; set; } = StatFormat.Number;
    [Ed("Показывать в панели")] public bool ShowInPanel { get; set; } = true;
    [Ed("Порядок расчёта", "Как складываются модификаторы этой характеристики (у брони, сопротивлений и силы атаки может быть разным)")]
    public StatCalcOrder CalcOrder { get; set; } = StatCalcOrder.Standard;
    [Ed("Округление")] public StatRounding Rounding { get; set; } = StatRounding.None;
}

public sealed class ResourceDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.Resource;

    [Ed("Цвет"), EdColor] public string Color { get; set; } = "#4a7fd0";
    [Ed("Максимум по умолчанию"), EdRange(0, 100000)] public double DefaultMax { get; set; } = 100;
    [Ed("Восстановление/с по умолчанию"), EdRange(-1000, 1000)] public double DefaultRegen { get; set; }
    [Ed("Показывать шкалу")] public bool ShowBar { get; set; } = true;
    [Ed("Минимум", "0 — обычный ресурс; отрицательный — разрешён долг (серия навыка «в долг»), шкала показывает «−»"), EdRange(-100000, 0)]
    public double MinValue { get; set; }
}

public enum FactionShape
{
    Circle,
    Diamond,
    Square,
    Triangle,
}

public enum Relation
{
    Ally,
    Enemy,
    Neutral,
}

public sealed class FactionDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.Faction;

    [Ed("Цвет"), EdColor] public string Color { get; set; } = "#3c78d8";
    [Ed("Форма основания", "Различает фракции не только цветом")] public FactionShape Shape { get; set; } = FactionShape.Circle;
    [Ed("Враждебна к"), EdRef(DefCategory.Faction)] public List<string> HostileTo { get; set; } = new();
    [Ed("Союзна с"), EdRef(DefCategory.Faction)] public List<string> AlliedWith { get; set; } = new();
}

public enum UnitShape
{
    Pawn,
    Soldier,
    Archer,
    Mage,
    Beast,
    Brute,
    Construct,
    Orb,
    Totem,
}

public sealed class UnitVisualDef : ContentPart
{
    [Ed("Фигура")] public UnitShape Shape { get; set; } = UnitShape.Pawn;
    [Ed("Цвет фигуры", "Пусто — цвет фракции"), EdColor] public string Color { get; set; } = "";
    [Ed("Масштаб"), EdRange(0.3, 3)] public double Scale { get; set; } = 1.0;
    [Ed("Символ", "1–2 буквы на фишке")] public string Symbol { get; set; } = "";
    [Ed("3D-модель", "Заменяет фишку; если файл отсутствует или повреждён — используется фишка"), EdRef(DefCategory.Asset)] public string Model { get; set; } = "";
    [Ed("Иконка", "Для панелей; пусто — символ"), EdRef(DefCategory.Asset)] public string Icon { get; set; } = "";
    [Ed("Эффект появления"), EdRef(DefCategory.Vfx)] public string SpawnVfx { get; set; } = "";
    [Ed("Эффект гибели"), EdRef(DefCategory.Vfx)] public string DeathVfx { get; set; } = "";
    [Ed("Звук атаки"), EdRef(DefCategory.Asset)] public string AttackSound { get; set; } = "";
    [Ed("Звук получения урона"), EdRef(DefCategory.Asset)] public string HitSound { get; set; } = "";
    [Ed("Звук гибели"), EdRef(DefCategory.Asset)] public string DeathSound { get; set; } = "";
    // Part 3 (docs/DECISIONS_3.md §47): the look only — size, collisions and rules stay in the unit's data.
    [Ed("Цветовые варианты", "Оттенки модели; выбор по номеру юнита — не влияет на правила"), EdColor] public List<string> ColorVariants { get; set; } = new();
    [Ed("Упрощённая модель", "При «упрощённых эффектах»"), EdRef(DefCategory.Asset)] public string SimplifiedModel { get; set; } = "";
    [Ed("Резервная модель", "Если основная не загрузилась; затем — фишка"), EdRef(DefCategory.Asset)] public string FallbackModel { get; set; } = "";
    [Ed("Эффект выделения"), EdRef(DefCategory.Vfx)] public string SelectionVfx { get; set; } = "";
    [Ed("Эффект получения урона"), EdRef(DefCategory.Vfx)] public string HitVfx { get; set; } = "";
}

public sealed class UnitResourceDef : ContentPart
{
    [Ed("Ресурс"), EdRef(DefCategory.Resource, AllowEmpty = false)] public string Resource { get; set; } = "";
    [Ed("Максимум"), EdRange(0, 100000)] public double Max { get; set; } = 100;
    [Ed("Начальное значение"), EdRange(0, 100000)] public double Start { get; set; } = 100;
    [Ed("Восстановление/с"), EdRange(-1000, 1000)] public double Regen { get; set; }
}

public sealed class StartingStatusDef : ContentPart
{
    [Ed("Состояние"), EdRef(DefCategory.Status, AllowEmpty = false)] public string Status { get; set; } = "";
    [Ed("Очки/стаки", "Для накопительных — очки, для временных — число стаков")] public double Amount { get; set; } = 1;
    [Ed("Длительность, с", "0 = по умолчанию из состояния")] public double Duration { get; set; }
}

public sealed class UnitDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.Unit;

    [Ed("Фракция", Group = "Основное"), EdRef(DefCategory.Faction)] public string Faction { get; set; } = "player";
    [Ed("Категория существа", "humanoid, beast, construct… используется иммунитетами и правилами", Group = "Основное")]
    public string CreatureCategory { get; set; } = "humanoid";
    [Ed("Категория движения", Group = "Основное"), EdRef(DefCategory.MoveCategory, AllowEmpty = false)]
    public string MoveCategory { get; set; } = "walk";
    [Ed("Слой", "Наземный, воздушный, подземный…; пусто — по умолчанию из правил", Group = "Основное"), EdRef(DefCategory.Layer)]
    public string Layer { get; set; } = "";
    [Ed("Совместное размещение", "Может ли делить клетку с другими существами", Group = "Основное")]
    public StackingDef Stacking { get; set; } = new();

    [Ed("Характеристики", "Отсутствующие берутся по умолчанию из определения характеристики", Group = "Характеристики"), EdKeyRef(DefCategory.Stat)]
    public Dictionary<string, double> Stats { get; set; } = new();

    [Ed("Защита от урона", "Сопротивления и уязвимости (−500…+500 %), иммунитеты, снижение, преобразование, поглощение, отражение", Group = "Урон")]
    public DefenseDef Defense { get; set; } = new();
    [Ed("Атака", "Бонусы наносимого урона по типам и преобразование", Group = "Урон")]
    public OffenseDef Offense { get; set; } = new();

    [Ed("Сопротивления состояниям", "Состояние → доля снижения накопления/шанса", Group = "Характеристики"), EdKeyRef(DefCategory.Status)]
    public Dictionary<string, double> StatusResistances { get; set; } = new();

    [Ed("Иммунитеты", "Id состояний/эффектов клеток или tag:тег", Group = "Характеристики")]
    public List<string> Immunities { get; set; } = new();

    [Ed("Ресурсы", Group = "Ресурсы")] public List<UnitResourceDef> Resources { get; set; } = new();

    [Ed("Обычная атака", Group = "Навыки"), EdRef(DefCategory.Skill)] public string BasicAttack { get; set; } = "";
    [Ed("Навыки", Group = "Навыки"), EdRef(DefCategory.Skill)] public List<string> Skills { get; set; } = new();
    [Ed("Защитный навык", "Центр радиального меню; пусто — защитная стойка из правил", Group = "Навыки"), EdRef(DefCategory.Skill)]
    public string DefenseSkill { get; set; } = "";
    [Ed("Пассивные способности", Group = "Навыки"), EdRef(DefCategory.Passive)] public List<string> Passives { get; set; } = new();
    [Ed("Дополнительные категории", "Раса, вид, класс, роль (происхождения): их правила и пассивы наследуются с деревом (docs/DECISIONS_4.md §61)", Group = "Навыки"), EdRef(DefCategory.Origin)]
    public List<string> Origins { get; set; } = new();
    [Ed("Стартовые состояния", Group = "Навыки")] public List<StartingStatusDef> StartingStatuses { get; set; } = new();
    [Ed("Автоатаки", "Атакуют сами через общую систему боя: навык, дальность, цели, интервал, правила выбора", Group = "Навыки")]
    public List<AutoAttackDef> AutoAttacks { get; set; } = new();

    [Ed("Профиль ИИ", Group = "Поведение"), EdRef(DefCategory.AiProfile)] public string AiProfile { get; set; } = "aggressive";
    [Ed("Роль в группе", "Формации и групповая атака: ближний бой, дальний бой, маг, поддержка, танк; авто — по обычной атаке", Group = "Поведение")]
    public UnitRole Role { get; set; } = UnitRole.Auto;

    [Ed("Восприятие", "Модель взгляда и зона обнаружения", Group = "Восприятие")] public PerceptionDef Perception { get; set; } = new();
    [Ed("Раскрытие информации", "Что игрок видит в панели этого существа", Group = "Восприятие")] public InfoOverrideDef Info { get; set; } = new();

    [Ed("Блокирование (как блокирующий)", "Лимит — характеристика «Лимит блокирования»", Group = "Блокирование")] public BlockerDef Blocking { get; set; } = new();
    [Ed("Блокирование (как цель)", Group = "Блокирование")] public BlockableDef Blockable { get; set; } = new();

    [Ed("Босс", "Фазы, смена навыков, особые условия", Group = "Босс")] public BossDef Boss { get; set; } = new();
    [Ed("Маркер предупреждения", "Показывать линию пути перед появлением этого типа", Group = "Вид")] public bool SpawnWarning { get; set; } = true;
    [Ed("Внешний вид", Group = "Вид")] public UnitVisualDef Visual { get; set; } = new();
    [Ed("Стоимость угрозы", "Для подбора волн по бюджету; 0 — модель угрозы (docs/DECISIONS_4.md §67)", Group = "Баланс"), EdRange(0, 100_000)]
    public double ThreatCost { get; set; }
    // Part 4, stage 6 (docs/DECISIONS_4.md §65–66).
    [Ed("Ранг", "Не влияет на уровень: появление, награды, вид, подбор волн", Group = "Баланс")] public UnitRank Rank { get; set; } = UnitRank.Normal;
    [Ed("Свой ранг", Group = "Баланс"), EdShowIf(nameof(Rank), UnitRank.Custom)] public string CustomRank { get; set; } = "";
    [Ed("Роль в оценке силы", "Веса оценки силы и профиль масштабирования; «авто» — по вкладам", Group = "Баланс")] public PowerRole PowerRole { get; set; } = PowerRole.Auto;
    [Ed("Профиль масштабирования", "Для «Подогнать под уровень» и уровня экземпляра; пусто — по роли", Group = "Баланс"), EdRef(DefCategory.ScalingProfile)]
    public string ScalingProfile { get; set; } = "";
    // Part 4 (docs/DECISIONS_4.md §60–61).
    [Ed("Основная категория", "Для показа и библиотеки; правила дерева наследуются вместе с дополнительными (происхождения)", Group = "Навыки"), EdRef(DefCategory.Origin)]
    public string MainCategory { get; set; } = "";
    [Ed("Дополнительные типы перемещения", "Действуют, пока выполнено их условие; проходимость — по любому действующему типу", Group = "Перемещение")]
    public List<MoveTypeDef> MoveTypes { get; set; } = new();
}

public enum PassiveTrigger
{
    /// <summary>Always active: stat modifiers only.</summary>
    Always,
    OnDamaged,
    OnAttackNoticed,
    OnHitEnemy,
    OnKill,
    OnAllyDied,
    OnLowHp,
    Periodic,
    OnEnemySpotted,
    // Part 3 (docs/DECISIONS_3.md §46).
    /// <summary>The unit dealt a critical hit.</summary>
    OnCrit,
    /// <summary>The unit finished a step.</summary>
    OnMoved,
    /// <summary>The unit used (released) a skill.</summary>
    OnSkillUsed,
    /// <summary>The unit lost the set amount of health (since the last trigger).</summary>
    OnHpLost,
    /// <summary>An enemy entered the set radius around the unit.</summary>
    OnEnemyEnteredArea,
}

public enum PassiveAction
{
    None,
    EffectsOnSelf,
    EffectsOnSource,
    CounterAttack,
    UseSkillOnSource,
}

public sealed class PassiveDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.Passive;

    // Part 3 (docs/DECISIONS_3.md §46): kind, source, conditions, modifiers, aura, stacking, triggers.
    [Ed("Вид", "Пассивное умение или талант (раса, происхождение, роль, уникальная особенность)", Group = "Основное")] public PassiveKind Kind { get; set; } = PassiveKind.Passive;
    [Ed("Источник", "Откуда получен (подпись и группировка)", Group = "Основное")] public PassiveSource Source { get; set; } = PassiveSource.Unit;
    [Ed("Значок", "1–2 символа в панели юнита", Group = "Основное")] public string Icon { get; set; } = "";
    [Ed("Показывать точные значения", "Выкл. — игрок видит только название и описание", Group = "Основное")] public bool ShowExactValues { get; set; } = true;
    [Ed("Условия действия", "Дерево И/ИЛИ/НЕ; пусто — всегда", Group = "Условия")] public UnitConditionDef? When { get; set; }
    [Ed("Частота проверки, с", "Как часто пересчитываются условия и ауры"), EdRange(0.05, 60)] public double CheckInterval { get; set; } = 0.25;
    [Ed("Модификаторы", "Что меняется и как суммируется", Group = "Эффект")] public List<ModifierDef> Modifiers { get; set; } = new();
    [Ed("Порядок применения", "Меньше — раньше (для одинаковых режимов суммирования не важен)", Group = "Эффект"), EdRange(-1000, 1000)] public int Order { get; set; }
    [Ed("Стекуемость", Group = "Эффект")] public PassiveStacking Stacking { get; set; } = PassiveStacking.Unique;
    [Ed("Макс. стаков"), EdRange(1, 100), EdShowIf(nameof(Stacking), PassiveStacking.Stack)] public int MaxStacks { get; set; } = 3;
    // Part 5 (ТЗ ч. 5, 13.4, 14.2; docs/DECISIONS_5.md §87).
    [Ed("Группа наложения", "Пассивы одной группы — одно и то же усиление из разных источников (вой разных вожаков, уровни одной ауры): " +
        "действует по правилу группы; пусто — без группы", Group = "Эффект")]
    public string StackGroup { get; set; } = "";
    [Ed("Правило группы", "Если задана группа наложения", Group = "Эффект")] public StackGroupRule GroupRule { get; set; } = StackGroupRule.Highest;
    [Ed("Сила в группе", "Для «сильнейший»: больше — сильнее; 0 — по величине модификаторов (с учётом «за каждого»)", Group = "Эффект"), EdRange(0, 100000)]
    public double StackRank { get; set; }
    [Ed("Радиус ауры", "0 — не аура; иначе действует на юнитов вокруг владельца", Group = "Аура"), EdRange(0, 20)] public int AuraRadius { get; set; }
    [Ed("Аура действует на", Group = "Аура")] public AuraAffects AuraAffects { get; set; } = AuraAffects.Allies;
    [Ed("Аура и на владельца", Group = "Аура")] public bool AuraIncludesSelf { get; set; }
    [Ed("Аура только для", "Теги или категории получателей («tag:стая», ID категории); пусто — все", Group = "Аура")] public List<string> AuraTypes { get; set; } = new();
    [Ed("Пассивы ауры", "Что получают юниты в ауре; пусто — модификаторы этого пассива", Group = "Аура"), EdRef(DefCategory.Passive)]
    public List<string> AuraPassives { get; set; } = new();
    [Ed("Действует после срабатывания, с", "Для срабатывающих: модификаторы действуют N с после события", Group = "Срабатывание"), EdRange(0, 3600)]
    public double ActiveSeconds { get; set; }
    [Ed("Событие деактивации", "Досрочно снимает действие после срабатывания", Group = "Срабатывание")] public PassiveTrigger? DeactivateOn { get; set; }
    [Ed("Макс. срабатываний за бой", "0 — без ограничения", Group = "Срабатывание"), EdRange(0, 10000)] public int MaxTriggers { get; set; }
    [Ed("Цепная реакция", "Эффект пассива может снова запустить этот же пассив (с пределом глубины)", Group = "Срабатывание")] public bool ChainReaction { get; set; }
    [Ed("Глубина цепи"), EdRange(1, 10), EdShowIf(nameof(ChainReaction), true)] public int MaxChainDepth { get; set; } = 2;
    [Ed("Потеря здоровья", "Для «потерял N здоровья»"), EdRange(1, 100000), EdShowIf(nameof(Trigger), PassiveTrigger.OnHpLost)] public double HpLoss { get; set; } = 30;
    [Ed("Радиус события", "Для «противник вошёл в область»"), EdRange(1, 20), EdShowIf(nameof(Trigger), PassiveTrigger.OnEnemyEnteredArea)] public int TriggerRadius { get; set; } = 2;
    [Ed("Тег навыка события", "Для «применил навык»: только навыки с тегом; пусто — любые"), EdShowIf(nameof(Trigger), PassiveTrigger.OnSkillUsed)] public string TriggerSkillTag { get; set; } = "";

    [Ed("Срабатывание")] public PassiveTrigger Trigger { get; set; } = PassiveTrigger.Always;
    [Ed("Модификаторы характеристик", "Действуют постоянно")] public List<StatModDef> StatMods { get; set; } = new();
    [Ed("Защита от урона", "Действует постоянно")] public DefenseDef Defense { get; set; } = new();
    [Ed("Атака", "Бонусы урона, действуют постоянно")] public OffenseDef Offense { get; set; } = new();
    [Ed("Шанс"), EdRange(0, 1)] public double Chance { get; set; } = 1;
    [Ed("Перезарядка, с"), EdRange(0, 3600)] public double Cooldown { get; set; }
    [Ed("Интервал, с", "Для периодического срабатывания"), EdRange(0.05, 3600), EdShowIf(nameof(Trigger), PassiveTrigger.Periodic)]
    public double Interval { get; set; } = 1;
    [Ed("Порог HP", "Доля HP для «при низком HP»"), EdRange(0, 1), EdShowIf(nameof(Trigger), PassiveTrigger.OnLowHp)]
    public double HpThreshold { get; set; } = 0.3;
    [Ed("Дополнительное условие", "Пассивка срабатывает, только если условие выполнено (без метки — для самого владельца)")]
    public ConditionDef? Condition { get; set; }
    [Ed("Действие")] public PassiveAction Action { get; set; } = PassiveAction.None;
    [Ed("Эффекты"), EdShowIf(nameof(Action), PassiveAction.EffectsOnSelf, PassiveAction.EffectsOnSource)]
    public List<SkillEffectDef> Effects { get; set; } = new();
    [Ed("Навык"), EdRef(DefCategory.Skill), EdShowIf(nameof(Action), PassiveAction.UseSkillOnSource)]
    public string Skill { get; set; } = "";
    [Ed("Стоимость ОД реакции"), EdRange(0, 100)] public double ApCost { get; set; }
}

/// <summary>Whether the AI starts a fight (ТЗ ч. 5, 13.8).</summary>
public enum AiTemperament
{
    [Ed("Обычный", "Атакует по профилю")] Aggressive,
    [Ed("Мирный, пока не атакуют", "Не нападает первым; на нападение отвечает по выбранной политике")] PassiveUntilAttacked,
}

/// <summary>What a peaceful unit does when attacked.</summary>
public enum AiRetaliation
{
    [Ed("Отвечает напавшему", "Только тем, кто на него напал")] Attacker,
    [Ed("Становится агрессивным", "Дальше — как обычный профиль")] Everyone,
    [Ed("Убегает", "Отходит от напавших")] Flee,
    [Ed("Не отвечает")] Ignore,
}

/// <summary>Which of the passives of one stacking group act (ТЗ ч. 5, 13.4).</summary>
public enum StackGroupRule
{
    [Ed("Сильнейший", "Только самый сильный источник группы")] Highest,
    [Ed("Последний источник заменяет", "Действует полученный последним (аура поверх своего, сценарий поверх категории)")] Latest,
    [Ed("Первый источник", "Действует первый, остальные отмечены как неактивные")] First,
    [Ed("Все действуют", "Складываются по своим режимам")] All,
}

public enum AiTargetPriority
{
    [Ed("Ближайшая")] Nearest,
    [Ed("Самая слабая")] Weakest,
    [Ed("Самая опасная")] MostDangerous,
    [Ed("Наименьший % здоровья")] LowestHpFraction,
    // Part 4 (docs/DECISIONS_4.md §64).
    [Ed("Изолированная", "Дальше всех от своих союзников")] Isolated,
    [Ed("Угрожающая базе", "Ближе всех к своей базе")] ThreatToBase,
    [Ed("По тегам и типам", "Сначала «предпочитаемые цели» профиля")] ByTypes,
}

/// <summary>A behaviour rule of an AI profile: a condition (self = the unit) and actions (actor = the unit).</summary>
public sealed class AiRuleDef : ContentPart
{
    [Ed("Название")] public string Name { get; set; } = "Правило";
    [Ed("Приоритет", "Правила проверяются от большего приоритета; поведение задаёт первое сработавшее"), EdRange(-100, 100)] public int Priority { get; set; }
    [Ed("Если")] public ConditionDef When { get; set; } = new() { Type = ConditionType.HpBelow, Fraction = 0.3 };
    [Ed("То: поведение")] public AiDirective Directive { get; set; } = AiDirective.None;
    [Ed("То: действия")] public List<TriggerActionDef> Actions { get; set; } = new();
    [Ed("Иначе: поведение")] public AiDirective ElseDirective { get; set; } = AiDirective.None;
    [Ed("Иначе: действия")] public List<TriggerActionDef> ElseActions { get; set; } = new();
    [Ed("Однократно", "Иначе — повторно с перезарядкой")] public bool Once { get; set; } = true;
    [Ed("Перезарядка, с"), EdRange(0, 3600), EdShowIf(nameof(Once), false)] public double Cooldown { get; set; } = 5;

    public override string ToString() => $"[{Priority}] если {When.Type} → {Directive}" + (ElseDirective != AiDirective.None || ElseActions.Count > 0 ? $", иначе {ElseDirective}" : "");
}

public sealed class AiProfileDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.AiProfile;

    [Ed("Агрессивность", "0 — осторожный, 1 — всегда атакует"), EdRange(0, 1)] public double Aggression { get; set; } = 0.8;
    [Ed("Правила поведения", "«Если — то — иначе» с приоритетом: поведение (отступить, удерживать, атаковать слабейшего…) и действия из каталога; поведение задаёт первое по приоритету сработавшее правило")]
    public List<AiRuleDef> Rules { get; set; } = new();
    [Ed("Приоритет целей")] public AiTargetPriority TargetPriority { get; set; } = AiTargetPriority.Nearest;
    [Ed("Предпочитаемая дистанция", "0 = ближний бой"), EdRange(0, 30)] public int PreferredRange { get; set; } = 1;
    [Ed("Держать дистанцию", "Отходить, если враг ближе предпочитаемой дистанции")] public bool KeepDistance { get; set; }
    [Ed("Отступать при HP ниже"), EdRange(0, 1)] public double RetreatHpFraction { get; set; }
    [Ed("Удерживать позицию")] public bool HoldPosition { get; set; }
    [Ed("Радиус удержания"), EdRange(0, 30), EdShowIf(nameof(HoldPosition), true)] public int HoldRadius { get; set; } = 2;
    [Ed("Преследовать", "Идти к последней известной позиции цели")] public bool Pursue { get; set; } = true;
    [Ed("Поддержка", "Лечить/усиливать союзников при возможности")] public bool Support { get; set; }
    [Ed("Порог лечения союзника"), EdRange(0, 1), EdShowIf(nameof(Support), true)] public double SupportHpThreshold { get; set; } = 0.6;
    [Ed("Избегание опасностей", "Множитель стоимости опасных клеток в маршруте"), EdRange(0, 100)] public double HazardAvoidance { get; set; } = 4;
    [Ed("Интервал решений, с"), EdRange(0.1, 10)] public double DecisionInterval { get; set; } = 0.5;
    [Ed("Поворачиваться к угрозе", "Разворачиваться лицом к видимому врагу")] public bool FaceThreats { get; set; } = true;
    [Ed("Поднимать тревогу", "Сообщать союзникам о замеченном враге")] public bool Alert { get; set; } = true;
    [Ed("Наступать без обзора", "Если врагов не видно — идти к стартовой зоне игрока (или центру карты)")] public bool AdvanceWhenIdle { get; set; }
    [Ed("Идти по маршруту, не отвлекаясь", "Враг волны идёт к цели маршрута и атакует только тех, кто его блокирует")] public bool RouteFocus { get; set; }
    // Part 5 (ТЗ ч. 5, 13.8): «не хочет» — temperament of the AI, apart from «не может» (a forbid of a category).
    [Ed("Характер", "«Мирный, пока не атакуют» — не начинает бой сам; способность атаковать при этом остаётся")] public AiTemperament Temperament { get; set; } = AiTemperament.Aggressive;
    [Ed("Если на мирного напали"), EdShowIf(nameof(Temperament), AiTemperament.PassiveUntilAttacked)] public AiRetaliation Retaliation { get; set; } = AiRetaliation.Attacker;
    [Ed("Только маршрут", "Никогда не переключается на охоту (ТЗ ч. 5, 17.7): не сходит с маршрута ради целей, не преследует, не ищет противника; " +
        "бьёт только тех, кого достаёт с места, и свою цель в конце маршрута. «Охотится» группы и альтернатива «атаковать защитников» не действуют — с записью в журнал")]
    public bool RouteOnly { get; set; }
    // Part 4 (docs/DECISIONS_4.md §59, §64): profiles of the autobattle.
    [Ed("Предпочитаемые цели", "Теги («tag:x»), категории, ID шаблонов — для приоритета «по тегам и типам»")] public List<string> TargetTypes { get; set; } = new();
    [Ed("Предпочитать контроль", "Сначала навыки с оглушением, замедлением, обездвиживанием")] public bool PreferControl { get; set; }
    [Ed("Охранять базу", "Центр удержания — своя база с наибольшим приоритетом защиты")] public bool GuardBase { get; set; }
    [Ed("Вести союзников", "Союзники рядом атакуют цель вожака")] public bool LeadAllies { get; set; }
    [Ed("Держаться союзников", "Не отходит от своих дальше радиуса удержания")] public bool StayWithAllies { get; set; }
    [Ed("Ломать препятствия", "Путь закрыт разрушаемым препятствием — атакует его")] public bool BreakObstacles { get; set; }
    [Ed("Патрулировать", "Обходит точки вокруг позиции (или маршрут-патруль) и реагирует в зоне")] public bool Patrol { get; set; }
    [Ed("Радиус патруля"), EdRange(1, 30), EdShowIf(nameof(Patrol), true)] public int PatrolRadius { get; set; } = 3;
    [Ed("Встреча с защитниками", "Для врагов на маршруте: преследование, отвлечение, возврат")] public EncounterDef? Encounter { get; set; }
    [Ed("Альтернативные цели", "База уничтожена или недоступна; пусто — из сценария")] public List<AltTargetDef> AltTargets { get; set; } = new();
}
