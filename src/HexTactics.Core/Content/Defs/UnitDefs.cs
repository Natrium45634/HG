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

    public static readonly string[] All =
    {
        HpMax, MoveSpeed, ApMax, ApRegen, DetectionRange, Accuracy, Evasion, BlockChance, BlockReduction,
        Armor, CritChance, CritMult, Perception, Stealth, InterruptResist, Power, HealPower,
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
}

public sealed class ResourceDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.Resource;

    [Ed("Цвет"), EdColor] public string Color { get; set; } = "#4a7fd0";
    [Ed("Максимум по умолчанию"), EdRange(0, 100000)] public double DefaultMax { get; set; } = 100;
    [Ed("Восстановление/с по умолчанию"), EdRange(-1000, 1000)] public double DefaultRegen { get; set; }
    [Ed("Показывать шкалу")] public bool ShowBar { get; set; } = true;
}

public sealed class DamageTypeDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.DamageType;

    [Ed("Цвет"), EdColor] public string Color { get; set; } = "#d0d0d0";
    [Ed("Учитывает броню", "Броня вычитается из урона этого типа")] public bool ArmorApplies { get; set; }
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

public sealed class UnitVisualDef
{
    [Ed("Фигура")] public UnitShape Shape { get; set; } = UnitShape.Pawn;
    [Ed("Цвет фигуры", "Пусто — цвет фракции"), EdColor] public string Color { get; set; } = "";
    [Ed("Масштаб"), EdRange(0.3, 3)] public double Scale { get; set; } = 1.0;
    [Ed("Символ", "1–2 буквы на фишке")] public string Symbol { get; set; } = "";
}

public sealed class UnitResourceDef
{
    [Ed("Ресурс"), EdRef(DefCategory.Resource, AllowEmpty = false)] public string Resource { get; set; } = "";
    [Ed("Максимум"), EdRange(0, 100000)] public double Max { get; set; } = 100;
    [Ed("Начальное значение"), EdRange(0, 100000)] public double Start { get; set; } = 100;
    [Ed("Восстановление/с"), EdRange(-1000, 1000)] public double Regen { get; set; }
}

public sealed class StartingStatusDef
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

    [Ed("Характеристики", "Отсутствующие берутся по умолчанию из определения характеристики", Group = "Характеристики"), EdKeyRef(DefCategory.Stat)]
    public Dictionary<string, double> Stats { get; set; } = new();

    [Ed("Сопротивления", "Тип урона → доля (0.5 = −50% урона, −0.5 = уязвимость, 1 = иммунитет)", Group = "Характеристики"), EdKeyRef(DefCategory.DamageType)]
    public Dictionary<string, double> Resistances { get; set; } = new();

    [Ed("Сопротивления состояниям", "Состояние → доля снижения накопления/шанса", Group = "Характеристики"), EdKeyRef(DefCategory.Status)]
    public Dictionary<string, double> StatusResistances { get; set; } = new();

    [Ed("Иммунитеты", "Id состояний/эффектов клеток или tag:тег", Group = "Характеристики")]
    public List<string> Immunities { get; set; } = new();

    [Ed("Ресурсы", Group = "Ресурсы")] public List<UnitResourceDef> Resources { get; set; } = new();

    [Ed("Обычная атака", Group = "Навыки"), EdRef(DefCategory.Skill)] public string BasicAttack { get; set; } = "";
    [Ed("Навыки", Group = "Навыки"), EdRef(DefCategory.Skill)] public List<string> Skills { get; set; } = new();
    [Ed("Пассивные способности", Group = "Навыки"), EdRef(DefCategory.Passive)] public List<string> Passives { get; set; } = new();
    [Ed("Стартовые состояния", Group = "Навыки")] public List<StartingStatusDef> StartingStatuses { get; set; } = new();

    [Ed("Профиль ИИ", Group = "Поведение"), EdRef(DefCategory.AiProfile)] public string AiProfile { get; set; } = "aggressive";

    [Ed("Внешний вид", Group = "Вид")] public UnitVisualDef Visual { get; set; } = new();
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

    [Ed("Срабатывание")] public PassiveTrigger Trigger { get; set; } = PassiveTrigger.Always;
    [Ed("Модификаторы характеристик", "Действуют постоянно")] public List<StatModDef> StatMods { get; set; } = new();
    [Ed("Шанс"), EdRange(0, 1)] public double Chance { get; set; } = 1;
    [Ed("Перезарядка, с"), EdRange(0, 3600)] public double Cooldown { get; set; }
    [Ed("Интервал, с", "Для периодического срабатывания"), EdRange(0.05, 3600), EdShowIf(nameof(Trigger), PassiveTrigger.Periodic)]
    public double Interval { get; set; } = 1;
    [Ed("Порог HP", "Доля HP для «при низком HP»"), EdRange(0, 1), EdShowIf(nameof(Trigger), PassiveTrigger.OnLowHp)]
    public double HpThreshold { get; set; } = 0.3;
    [Ed("Действие")] public PassiveAction Action { get; set; } = PassiveAction.None;
    [Ed("Эффекты"), EdShowIf(nameof(Action), PassiveAction.EffectsOnSelf, PassiveAction.EffectsOnSource)]
    public List<SkillEffectDef> Effects { get; set; } = new();
    [Ed("Навык"), EdRef(DefCategory.Skill), EdShowIf(nameof(Action), PassiveAction.UseSkillOnSource)]
    public string Skill { get; set; } = "";
    [Ed("Стоимость ОД реакции"), EdRange(0, 100)] public double ApCost { get; set; }
}

public enum AiTargetPriority
{
    Nearest,
    Weakest,
    MostDangerous,
    LowestHpFraction,
}

public sealed class AiProfileDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.AiProfile;

    [Ed("Агрессивность", "0 — осторожный, 1 — всегда атакует"), EdRange(0, 1)] public double Aggression { get; set; } = 0.8;
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
}
