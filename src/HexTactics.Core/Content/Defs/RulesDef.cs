using System.Collections.Generic;

namespace HexTactics.Core.Content;

/// <summary>
/// Global configurable rules. Every value here is documented in docs/DECISIONS.md and
/// editable in the rules editor. Nothing gameplay-relevant is hardcoded elsewhere.
/// </summary>
public sealed class RulesDef : ContentPart
{
    [Ed("Время")] public TimeRules Time { get; set; } = new();
    [Ed("Единицы мира", "Радиус гекса — единица мира; шаг высоты рельефа на экране (ТЗ ч. 5, 6.2)")] public WorldRules World { get; set; } = new();
    [Ed("Очки действия")] public ApRules Ap { get; set; } = new();
    [Ed("Поворот")] public TurnRules Turn { get; set; } = new();
    [Ed("Движение")] public MoveRules Move { get; set; } = new();
    [Ed("Слои существ")] public LayerRules Layers { get; set; } = new();
    [Ed("Высота")] public HeightRules Height { get; set; } = new();
    [Ed("Обнаружение и неожиданность")] public DetectionRules Detection { get; set; } = new();
    [Ed("Раскрытие информации о врагах")] public InfoDisclosureDef Info { get; set; } = new();
    [Ed("Бой")] public CombatRules Combat { get; set; } = new();
    [Ed("Расчёт урона", "Единый порядок для всех источников и его исключения")] public DamageRulesDef Damage { get; set; } = new();
    [Ed("Автоатаки")] public AutoAttackRules AutoAttacks { get; set; } = new();
    [Ed("Маркеры появления врагов")] public SpawnWarningRules SpawnWarnings { get; set; } = new();
    [Ed("Эффекты окружения", "Визуальные эффекты и звуки событий: появление эффектов, ловушки, разрушение, смена местности, падение")]
    public EnvironmentVisualsDef Visuals { get; set; } = new();
    [Ed("Ресурсы: лимиты", "Размеры и форматы импортируемых текстур, моделей, анимаций и звуков (docs/DECISIONS_3.md §47)")]
    public AssetLimitsDef Assets { get; set; } = new();
    [Ed("Видимость")] public VisionRules Vision { get; set; } = new();
    [Ed("Очередь команд")] public QueueRules Queue { get; set; } = new();
    [Ed("Ограничения")] public LimitRules Limits { get; set; } = new();
    [Ed("Застревание на маршруте", "Когда враг на маршруте считается застрявшим и как часто пробует следующую реакцию (версия правил 2)")]
    public StuckRules Stuck { get; set; } = new();
    [Ed("Автопауза")] public AutopauseRules Autopause { get; set; } = new();
    [Ed("Смерть")] public DeathRules Death { get; set; } = new();
    [Ed("Оценка силы", "Профиль проекта: эталон уровня 1, веса ролей, шкала уровня (docs/DECISIONS_4.md §65)")] public PowerRulesDef Power { get; set; } = new();
    [Ed("Производительность", "Пороги предупреждений: живые юниты, группы, маршруты, пассивы, эффекты, ИИ, параллельные прогоны (docs/DECISIONS_4.md §72)")]
    public PerformanceRulesDef Performance { get; set; } = new();
}

public sealed class TimeRules : ContentPart
{
    [Ed("Тиков в секунду", "Фиксированный шаг симуляции"), EdRange(5, 120)] public int TicksPerSecond { get; set; } = 20;
    [Ed("Скорости", "Множители времени для кнопок скорости")] public List<double> SpeedPresets { get; set; } = new() { 0.25, 0.5, 1, 2, 4, 8, 16 };
    [Ed("Скорость по умолчанию", "Индекс в списке скоростей"), EdRange(0, 20)] public int DefaultSpeedIndex { get; set; } = 2;
    [Ed("Макс. тиков за кадр", "Если кадр не успевает — игра замедляется, тики не пропускаются"), EdRange(1, 1000)]
    public int MaxTicksPerFrame { get; set; } = 40;
    [Ed("Начинать на паузе")] public bool StartPaused { get; set; } = true;
    [Ed("Версия правил симуляции", "1 — как в 1.0 (один генератор случайности на всё); 2 — раздельные потоки: бой, ИИ и маршруты, спавн " +
        "(docs/DECISIONS_5.md §84). Смена версии меняет исход боёв с тем же seed"), EdRange(1, 2)]
    public int SimVersion { get; set; } = 1;
}

/// <summary>
/// World units of the board (docs/DECISIONS_5.md §85): the radius of a hex is the unit — sizes of models and offsets are
/// given in radii; the step of one level of relief is how high it is drawn. The rules (sight, attacks from above) count
/// levels, not units, so the step changes the look only.
/// </summary>
public sealed class WorldRules : ContentPart
{
    [Ed("Радиус гекса", "Единица мира: размеры моделей, смещения и масштабы задаются в радиусах гекса"), EdReadOnly] public double HexRadius { get; set; } = 1;
    [Ed("Шаг высоты рельефа", "Высота одного уровня на экране, в радиусах гекса; правила считают уровни, а не единицы"), EdRange(0.1, 2)]
    public double ElevationStep { get; set; } = 0.35;
}

public sealed class ApRules : ContentPart
{
    [Ed("ОД за единицу стоимости движения"), EdRange(0, 100)] public double MovePerCost { get; set; } = 1.0;
    [Ed("Реакции не трогают резерв", "Реакции тратят только свободные ОД (текущие − резерв)")] public bool ReactionsRespectReserve { get; set; } = true;
    [Ed("ОД стойки защиты"), EdRange(0, 100)] public double DefendCost { get; set; } = 1.0;
    [Ed("Бонус уклонения в стойке"), EdRange(0, 1)] public double DefendEvasionBonus { get; set; } = 0.15;
    [Ed("Бонус блока в стойке"), EdRange(0, 1)] public double DefendBlockBonus { get; set; } = 0.25;
    [Ed("Восстановление ОД в стойке", "Множитель"), EdRange(0, 10)] public double DefendRegenMultiplier { get; set; } = 0.5;
}

public sealed class TurnRules : ContentPart
{
    [Ed("ОД за шаг 60°"), EdRange(0, 100)] public double ApPerStep { get; set; } = 0.25;
    [Ed("Время шага 60°, с"), EdRange(0, 10)] public double SecondsPerStep { get; set; } = 0.1;
    [Ed("Поворачиваться по ходу движения")] public bool AutoFaceOnMove { get; set; } = true;
    [Ed("Поворот по ходу стоит ОД")] public bool AutoFaceCostsAp { get; set; }
    [Ed("Поворачиваться к цели перед атакой")] public bool AutoFaceTarget { get; set; } = true;
    [Ed("Ручной поворот стоит ОД", "Команда «Поворот» (стрелка взгляда): по умолчанию бесплатна (docs/DECISIONS_3.md §40)")]
    public bool ManualCostsAp { get; set; }
    [Ed("Ручной поворот занимает время", "Иначе — мгновенно")] public bool ManualTakesTime { get; set; }
}

/// <summary>Order of tie-breaks between routes of equal cost (docs/DECISIONS_3.md §41).</summary>
public enum RouteTieBreak
{
    [Ed("Меньше поворотов")] Turns,
    [Ed("Меньше шагов")] Steps,
}

/// <summary>A forced move into a taken hex (ТЗ ч. 5, 15.3).</summary>
public enum CollisionPolicy
{
    [Ed("Остановиться", "Цель останавливается перед занятым гексом (как в 1.0)")] Stop,
    [Ed("Толкнуть дальше", "Стоящий на пути сдвигается на остаток (с его массой), цепочкой до предела")] ChainPush,
    [Ed("Урон от удара", "Остановка и урон обоим за каждый непройденный гекс")] ImpactDamage,
    [Ed("Сдвинуть в сторону, если можно", "Цель уходит в свободный соседний гекс сбоку от пути, иначе останавливается")] DisplaceIfLegal,
}

public sealed class MoveRules : ContentPart
{
    [Ed("Категории движения")] public List<string> MovementCategories { get; set; } = new() { "walk", "fly", "swim" };
    [Ed("Союзники блокируют путь")] public bool AlliesBlockPath { get; set; } = true;
    [Ed("Штраф опасных клеток", "Добавка к стоимости опасной клетки при поиске пути игрока (обход при почти равной цене)"), EdRange(0, 100)] public double HazardPenalty { get; set; } = 0.01;
    [Ed("Обмен местами", "Два союзника, идущие навстречу друг другу, меняются гексами за один шаг (docs/DECISIONS_3.md §44)")]
    public bool AllowSwap { get; set; } = true;
    [Ed("Стоящие союзники пропускают", "Союзник без приказа меняется местами с проходящим и возвращается на своё место — узкий проход не запирается теми, кто уже пришёл")]
    public bool YieldToAllies { get; set; } = true;
    // Part 4 (docs/DECISIONS_4.md §60).
    [Ed("Эталонная масса", "Отбрасывание: дистанция × эталон / масса цели (вниз), не меньше 0"), EdRange(1, 100_000)] public double ReferenceWeight { get; set; } = 100;
    // Part 5 (ТЗ ч. 5, 15.2–15.3; docs/DECISIONS_5.md §87).
    [Ed("Формула отталкивания", "Сколько гексов пройдёт цель (вниз, не больше импульса); пусто — impulse × reference / mass. " +
        "Переменные: impulse (дистанция эффекта), mass, reference (эталон), resist (устойчивость к контролю, 0–1), cost (стоимость клетки цели). " +
        "Пример «всё или ничего»: if(impulse * 40 >= mass, impulse, 0)"),
     EdFormula("impulse", "mass", "reference", "resist", "cost")]
    public string KnockbackFormula { get; set; } = "";
    [Ed("Столкновение при толчке", "Если на пути занятый гекс")] public CollisionPolicy Collision { get; set; } = CollisionPolicy.Stop;
    [Ed("Урон удара за гекс", "«Урон от удара»: каждому из двоих за каждый непройденный гекс"), EdRange(0, 10_000), EdShowIf(nameof(Collision), CollisionPolicy.ImpactDamage)]
    public double ImpactDamage { get; set; } = 5;
    [Ed("Глубина цепочки", "«Толкнуть дальше»: сколько юнитов подряд можно сдвинуть"), EdRange(1, 10), EdShowIf(nameof(Collision), CollisionPolicy.ChainPush)]
    public int ChainDepth { get; set; } = 2;
    [Ed("Размер для узких мест", "Существа такого размера и больше не проходят гекс между двумя непроходимыми соседями напротив друг друга"), EdRange(2, 100)]
    public double NarrowSize { get; set; } = 2;
    [Ed("Разрыв ничьих маршрута", "При равной стоимости маршрута игрока: по порядку критериев")]
    public List<RouteTieBreak> TieBreak { get; set; } = new() { RouteTieBreak.Turns, RouteTieBreak.Steps };
}

public sealed class HeightRules : ContentPart
{
    [Ed("Макс. подъём за шаг"), EdRange(0, 20)] public int MaxClimb { get; set; } = 1;
    [Ed("Макс. спуск за шаг"), EdRange(0, 20)] public int MaxDrop { get; set; } = 2;
    [Ed("Стоимость подъёма за уровень"), EdRange(0, 100)] public double ClimbCostPerLevel { get; set; } = 1.0;
    [Ed("Стоимость спуска за уровень"), EdRange(0, 100)] public double DescendCostPerLevel { get; set; }
    [Ed("Точность за уровень превосходства"), EdRange(-1, 1)] public double AccuracyPerLevel { get; set; } = 0.05;
    [Ed("Дальность за уровень (дистанц.)"), EdRange(0, 10)] public double RangePerLevel { get; set; } = 0.5;
    [Ed("Высота глаз, уровней"), EdRange(0, 10)] public double EyeHeight { get; set; } = 1.0;
    [Ed("Рельеф блокирует обзор")] public bool TerrainBlocksLoS { get; set; } = true;
}

public enum AttackSector
{
    [Ed("Фронт")] Front = 0,
    [Ed("Ближний фланг")] NearFlank = 1,
    [Ed("Дальний фланг")] FarFlank = 2,
    [Ed("Тыл")] Rear = 3,
    /// <summary>The target has no facing (omni / none): sector modifiers do not apply.</summary>
    [Ed("Без секторов")] None = 4,
}

public sealed class SectorRule : ContentPart
{
    [Ed("Сектор")] public AttackSector Sector { get; set; }
    [Ed("Шанс неожиданности"), EdRange(0, 1)] public double SurpriseChance { get; set; }
    [Ed("Шанс заметить"), EdRange(0, 1)] public double NoticeChance { get; set; }
}

public sealed class DetectionRules : ContentPart
{
    [Ed("Секторы", "Фронт, ближний фланг, дальний фланг, тыл")]
    public List<SectorRule> Sectors { get; set; } = new()
    {
        new SectorRule { Sector = AttackSector.Front, SurpriseChance = 0.0, NoticeChance = 1.0 },
        new SectorRule { Sector = AttackSector.NearFlank, SurpriseChance = 0.3, NoticeChance = 0.7 },
        new SectorRule { Sector = AttackSector.FarFlank, SurpriseChance = 0.7, NoticeChance = 0.3 },
        new SectorRule { Sector = AttackSector.Rear, SurpriseChance = 1.0, NoticeChance = 0.0 },
    };

    [Ed("Без секторов: шанс заметить", "Для целей без направления взгляда или с круговым восприятием"), EdRange(0, 1)] public double NoSectorNotice { get; set; } = 1.0;
    [Ed("Без секторов: шанс неожиданности"), EdRange(0, 1)] public double NoSectorSurprise { get; set; }
    [Ed("Множитель замечания, если цель не видит атакующего"), EdRange(0, 1)] public double UnseenNoticeFactor { get; set; }
    [Ed("Множитель замечания при ослеплении"), EdRange(0, 1)] public double BlindNoticeFactor { get; set; }
    [Ed("Штраф замечания за гекс"), EdRange(0, 1)] public double NoticePenaltyPerHex { get; set; }
    [Ed("Дистанция без штрафа"), EdRange(0, 50)] public int NoticeFreeDistance { get; set; } = 1;
    [Ed("Штраф замечания от темноты", "Умножается на темноту карты (0..1)"), EdRange(0, 1)] public double DarknessNoticePenalty { get; set; } = 0.3;
    [Ed("Бонус неожиданности, если цель не видит"), EdRange(0, 1)] public double SurpriseBonusVsUnaware { get; set; }
    [Ed("Уклонение от незамеченной атаки", "Множитель уклонения"), EdRange(0, 1)] public double UnnoticedEvasionFactor { get; set; }

    [Ed("Неожиданность: бонус точности"), EdRange(-1, 1)] public double SurpriseAccuracy { get; set; } = 0.25;
    [Ed("Неожиданность: множитель урона"), EdRange(0, 10)] public double SurpriseDamageMul { get; set; } = 1.25;
    [Ed("Неожиданность: бонус крита"), EdRange(-1, 1)] public double SurpriseCrit { get; set; } = 0.15;
    [Ed("Неожиданность: бонус прерывания"), EdRange(-1, 1)] public double SurpriseInterrupt { get; set; } = 0.5;

    public SectorRule For(AttackSector s)
    {
        if (s == AttackSector.None) return new SectorRule { Sector = s, NoticeChance = NoSectorNotice, SurpriseChance = NoSectorSurprise };
        foreach (var r in Sectors)
            if (r.Sector == s)
                return r;
        return new SectorRule { Sector = s };
    }
}

public sealed class CombatRules : ContentPart
{
    [Ed("Базовый шанс попадания"), EdRange(0, 1)] public double BaseHit { get; set; } = 0.85;
    [Ed("Мин. шанс попадания"), EdRange(0, 1)] public double MinHit { get; set; } = 0.05;
    [Ed("Макс. шанс попадания"), EdRange(0, 1)] public double MaxHit { get; set; } = 0.98;
    [Ed("Вес укрытия"), EdRange(0, 2)] public double CoverFactor { get; set; } = 1.0;
    [Ed("Мин. доля урона после брони"), EdRange(0, 1)] public double MinArmorFraction { get; set; } = 0.2;
    [Ed("Мин. урон при попадании"), EdRange(0, 100)] public double MinDamageOnHit { get; set; } = 1;
    [Ed("Урон по союзникам", "Области задевают союзников, если эффект это допускает")] public bool FriendlyFire { get; set; } = true;
    // Part 6, stage 6.11 (§105).
    [Ed("Дружественный огонь", "Правила по видам воздействия; не задано — «Урон по союзникам» для областей, остальное — по фильтрам эффектов")]
    public FriendlyFireRulesDef? FriendlyFireRules { get; set; }
    [Ed("Телепорт через поля")] public TeleportFields TeleportFields { get; set; } = TeleportFields.DestinationOnly;
    [Ed("Лечение может быть критическим")] public bool HealCanCrit { get; set; }

    [Ed("Формула шанса попадания", "Пусто — встроенная. Переменные: base, accuracy, evasion, height, surprise, cover"),
     EdFormula("base", "accuracy", "evasion", "height", "surprise", "cover", "distance")]
    public string HitFormula { get; set; } = "";

    [Ed("Формула урона", "Пусто — встроенная. Переменные: raw, armor, resist, crit, surprise, block"),
     EdFormula("raw", "armor", "resist", "crit", "surprise", "block")]
    public string DamageFormula { get; set; } = "";
}

public sealed class VisionRules : ContentPart
{
    [Ed("Интервал пересчёта, с"), EdRange(0.05, 5)] public double UpdateInterval { get; set; } = 0.25;
    [Ed("Туман войны", "Игрок видит только то, что видят его юниты")] public bool FogOfWar { get; set; } = true;
    [Ed("Влияние скрытности на дальность", "Дальность × (1 − скрытность × k)"), EdRange(0, 1)] public double StealthRangeFactor { get; set; } = 1.0;
    [Ed("Дальность обнаружения невидимых"), EdRange(0, 10)] public int InvisibleDetectRange { get; set; } = 1;
    [Ed("Дальность обзора при ослеплении"), EdRange(0, 10)] public int BlindDetectRange { get; set; } = 1;
}

public enum FailureAction
{
    Skip,
    CancelQueue,
    Wait,
    Repath,
}

public sealed class FailurePolicyDef : ContentPart
{
    [Ed("Действие")] public FailureAction Action { get; set; } = FailureAction.Skip;
    [Ed("Ждать, с"), EdRange(0, 600)] public double WaitSeconds { get; set; }
    [Ed("Затем", "Если ожидание/пересчёт не помогли")] public FailureAction Then { get; set; } = FailureAction.Skip;
    [Ed("Попыток пересчёта"), EdRange(0, 20)] public int MaxAttempts { get; set; } = 3;

    public FailurePolicyDef Clone() => (FailurePolicyDef)MemberwiseClone();

    public override string ToString() => Action switch
    {
        FailureAction.Wait => $"ждать {WaitSeconds:0.#} с, затем {Then}",
        FailureAction.Repath => $"пересчитать путь, затем {Then}",
        _ => Action.ToString(),
    };
}

public enum FailureReason
{
    None,
    NotEnoughAp,
    NotEnoughResource,
    PathBlocked,
    NoPath,
    TargetLost,
    OutOfRange,
    NoLineOfSight,
    Cooldown,
    Disabled,
    Invalid,
}

public sealed class QueueRules : ContentPart
{
    [Ed("Не хватает ОД")] public FailurePolicyDef NotEnoughAp { get; set; } = new() { Action = FailureAction.Wait, WaitSeconds = 10, Then = FailureAction.Skip };
    [Ed("Не хватает ресурса")] public FailurePolicyDef NotEnoughResource { get; set; } = new() { Action = FailureAction.Wait, WaitSeconds = 10, Then = FailureAction.Skip };
    [Ed("Путь заблокирован")] public FailurePolicyDef PathBlocked { get; set; } = new() { Action = FailureAction.Repath, WaitSeconds = 1, Then = FailureAction.CancelQueue, MaxAttempts = 3 };
    [Ed("Пути нет")] public FailurePolicyDef NoPath { get; set; } = new() { Action = FailureAction.CancelQueue };
    [Ed("Цель потеряна")] public FailurePolicyDef TargetLost { get; set; } = new() { Action = FailureAction.Skip };
    [Ed("Цель вне дальности")] public FailurePolicyDef OutOfRange { get; set; } = new() { Action = FailureAction.Wait, WaitSeconds = 5, Then = FailureAction.Skip };
    [Ed("Нет линии видимости")] public FailurePolicyDef NoLineOfSight { get; set; } = new() { Action = FailureAction.Wait, WaitSeconds = 5, Then = FailureAction.Skip };
    [Ed("Перезарядка")] public FailurePolicyDef Cooldown { get; set; } = new() { Action = FailureAction.Wait, WaitSeconds = 30, Then = FailureAction.Skip };
    [Ed("Под контролем")] public FailurePolicyDef Disabled { get; set; } = new() { Action = FailureAction.Wait, WaitSeconds = 10, Then = FailureAction.Skip };
    [Ed("Макс. ожидание, с", "Жёсткий предел любого ожидания"), EdRange(1, 3600)] public double MaxWaitSeconds { get; set; } = 60;

    public FailurePolicyDef For(FailureReason reason) => reason switch
    {
        FailureReason.NotEnoughAp => NotEnoughAp,
        FailureReason.NotEnoughResource => NotEnoughResource,
        FailureReason.PathBlocked => PathBlocked,
        FailureReason.NoPath => NoPath,
        FailureReason.TargetLost => TargetLost,
        FailureReason.OutOfRange => OutOfRange,
        FailureReason.NoLineOfSight => NoLineOfSight,
        FailureReason.Cooldown => Cooldown,
        FailureReason.Disabled => Disabled,
        _ => new FailurePolicyDef { Action = FailureAction.Skip },
    };
}

/// <summary>«Нет продвижения» on a route (ТЗ ч. 5, 11.4; docs/DECISIONS_5.md §86): standing, back and forth, endless replanning.</summary>
public sealed class StuckRules : ContentPart
{
    [Ed("Нет продвижения, с", "Столько секунд без продвижения по маршруту (стоит или ходит туда-сюда) — следующая реакция на блокировку"), EdRange(0.5, 120)]
    public double Seconds { get; set; } = 4;
    [Ed("Уступает на, с", "Сколько стоит в стороне, пропуская встречного"), EdRange(0.2, 30)] public double YieldSeconds { get; set; } = 2;
}

public sealed class LimitRules : ContentPart
{
    [Ed("Макс. глубина реакций"), EdRange(1, 64)] public int MaxReactionDepth { get; set; } = 8;
    [Ed("Макс. событий за тик"), EdRange(100, 1000000)] public int MaxEventsPerTick { get; set; } = 5000;
    [Ed("Макс. эффектов на клетке"), EdRange(1, 64)] public int MaxEffectsPerCell { get; set; } = 8;
    [Ed("Макс. цепочка синергий"), EdRange(1, 64)] public int MaxSynergyChain { get; set; } = 6;
    [Ed("Макс. юнитов"), EdRange(1, 10000)] public int MaxUnits { get; set; } = 500;
    [Ed("Макс. клеток карты"), EdRange(1, 100000)] public int MaxMapCells { get; set; } = 16384;
    [Ed("Макс. стаков состояния"), EdRange(1, 1000)] public int MaxStatusStacks { get; set; } = 20;
    [Ed("Макс. существ на гексе", "Для режима «разрешено всем»"), EdRange(1, 24)] public int MaxPerHex { get; set; } = 6;
    [Ed("Разрешить глобальные навыки")] public bool AllowGlobalSkills { get; set; } = true;
}

public sealed class AutopauseRules : ContentPart
{
    [Ed("Обнаружен враг")] public bool EnemySpotted { get; set; } = true;
    [Ed("Критический урон союзнику")] public bool AllyCriticalDamage { get; set; } = true;
    [Ed("Порог критического HP"), EdRange(0, 1)] public double CriticalHpFraction { get; set; } = 0.3;
    [Ed("Гибель юнита")] public bool UnitDied { get; set; } = true;
    [Ed("Прерывание навыка")] public bool SkillInterrupted { get; set; }
    [Ed("Маршрут недоступен")] public bool PathUnavailable { get; set; } = true;
    [Ed("Начало волны")] public bool WaveStarted { get; set; } = true;
    [Ed("Отмена команды")] public bool CommandFailed { get; set; }
    [Ed("Очередь юнита опустела")] public bool QueueEmpty { get; set; }
}

public sealed class DeathRules : ContentPart
{
    [Ed("Призванные исчезают со смертью призывателя")] public bool SummonsDieWithOwner { get; set; } = true;
    [Ed("Эффект клетки при смерти"), EdRef(DefCategory.CellEffect)] public string CorpseCellEffect { get; set; } = "";
}
