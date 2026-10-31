using System.Text.Json.Serialization;
using System.Collections.Generic;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

public enum SkillKind
{
    Attack,
    Spell,
    Support,
    Movement,
    Utility,
    /// <summary>Defensive skill: block, dodge, parry, stance, shield… (centre of the radial menu, docs/DECISIONS_3.md §45).</summary>
    Defense,
}

/// <summary>How a skill can be used (docs/DECISIONS_3.md §43).</summary>
public enum SkillUseMode
{
    [Ed("Прицельный")] Aimed,
    [Ed("Автоматический")] Auto,
    [Ed("Прицельный и автоматический")] Both,
}

/// <summary>Ring of the radial menu (docs/DECISIONS_3.md §42).</summary>
public enum MenuRing
{
    [Ed("Авто (по силе)")] Auto,
    [Ed("Первое кольцо")] First,
    [Ed("Второе кольцо")] Second,
}

public enum FacingAfterUse
{
    [Ed("К цели")] TowardTarget,
    [Ed("Прежнее направление")] Keep,
    [Ed("Заданное направление")] Set,
}

/// <summary>What a skill order does when its target dies, disappears or becomes unreachable (docs/DECISIONS_3.md §43).</summary>
public enum TargetLostPolicy
{
    [Ed("Отменить приказ")] Cancel,
    [Ed("Выбрать новую цель")] NewTarget,
    [Ed("Ждать возвращения цели")] WaitReturn,
    [Ed("Идти к последней известной позиции")] LastKnown,
    [Ed("Завершить начатую последовательность")] FinishSequence,
}

public enum CostMoment
{
    [Ed("При запуске")] Start,
    [Ed("При срабатывании")] Release,
}

/// <summary>
/// How the skill is aimed. Self/Direction: the area is anchored at the caster and oriented
/// by the chosen direction. Cell/Unit: the area is anchored at the target cell and oriented
/// from the caster towards the target. See docs/DECISIONS.md §10.
/// </summary>
public enum TargetingMode
{
    Self,
    Direction,
    Cell,
    Unit,
}

public enum HitMode
{
    Roll,
    AlwaysHit,
}

public enum SkillVfx
{
    Slash,
    Projectile,
    Burst,
    Beam,
    Glow,
}

public sealed class InterruptDef : ContentPart
{
    [Ed("Прерывается уроном")] public bool ByDamage { get; set; } = true;
    [Ed("Шанс прерывания уроном"), EdRange(0, 1), EdShowIf(nameof(ByDamage), true)] public double ChanceOnDamage { get; set; } = 0.3;
    [Ed("Прерывается контролем", "Оглушение, немота, контроль разума")] public bool ByControl { get; set; } = true;
    [Ed("Прерывается перемещением", "Толчок/притяжение")] public bool ByForcedMove { get; set; } = true;
    [Ed("Возврат ресурсов при прерывании", "Доля 0..1"), EdRange(0, 1)] public double Refund { get; set; } = 0.5;
    [Ed("Перезарядка при прерывании")] public bool CooldownOnInterrupt { get; set; }
}

public sealed class SkillDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.Skill;

    [Ed("Тип", Group = "Основное")] public SkillKind Kind { get; set; } = SkillKind.Attack;

    [Ed("Стоимость ОД", Group = "Стоимость"), EdRange(0, 1000)] public double ApCost { get; set; } = 2;
    [Ed("Стоимость ресурсов", Group = "Стоимость")] public List<ResourceCostDef> ResourceCosts { get; set; } = new();

    [Ed("Наведение", Group = "Наведение")] public TargetingMode Targeting { get; set; } = TargetingMode.Unit;
    [Ed("Мин. дальность", Group = "Наведение"), EdRange(0, 100), EdShowIf(nameof(Targeting), TargetingMode.Cell, TargetingMode.Unit)]
    public int RangeMin { get; set; } = 1;
    [Ed("Макс. дальность", Group = "Наведение"), EdRange(0, 100), EdShowIf(nameof(Targeting), TargetingMode.Cell, TargetingMode.Unit)]
    public int RangeMax { get; set; } = 1;
    [Ed("Дистанционный", "Получает бонус дальности от высоты, цель получает укрытие", Group = "Наведение")]
    public bool Ranged { get; set; }
    [Ed("Нужна линия видимости", Group = "Наведение")] public bool RequiresLoS { get; set; } = true;
    [Ed("Допустимые цели", "Для наведения на юнита", Group = "Наведение"), EdShowIf(nameof(Targeting), TargetingMode.Unit)]
    public TargetFilterDef ValidTargets { get; set; } = TargetFilterDef.EnemiesOnly();
    [Ed("Можно в пустую клетку", Group = "Наведение"), EdShowIf(nameof(Targeting), TargetingMode.Cell)]
    public bool AllowEmptyCell { get; set; } = true;
    [Ed("Попадание", "Бросок точности или всегда попадает", Group = "Наведение")] public HitMode HitMode { get; set; } = HitMode.Roll;
    [Ed("Слои целей", "Какие слои поражает; пусто — по правилам (ближний бой — наземный, дальний и заклинания — наземный и воздушный)", Group = "Наведение"),
     EdRef(DefCategory.Layer)]
    public List<string> TargetLayers { get; set; } = new();
    [Ed("Слои исполнителя", "Из каких слоёв можно применять; пусто — из любого", Group = "Наведение"), EdRef(DefCategory.Layer)]
    public List<string> CasterLayers { get; set; } = new();

    [Ed("Подготовка, с", "Время телеграфа перед срабатыванием", Group = "Время"), EdRange(0, 60)] public double PrepTime { get; set; } = 0.3;
    [Ed("Восстановление, с", "Юнит занят после срабатывания", Group = "Время"), EdRange(0, 60)] public double RecoveryTime { get; set; } = 0.2;
    [Ed("Перезарядка, с", Group = "Время"), EdRange(0, 3600)] public double Cooldown { get; set; }
    [Ed("Поворачиваться к цели", Group = "Время")] public bool FaceTarget { get; set; } = true;
    [Ed("Телеграфировать область", "Показывать область угрозы во время подготовки тем, кто видит исполнителя", Group = "Время")]
    public bool Telegraph { get; set; } = true;

    [Ed("Прерывание", Group = "Прерывание")] public InterruptDef Interrupt { get; set; } = new();
    // Part 6, stage 6.11 (§105).
    [Ed("Дружественный огонь", "Своё правило навыка (прямой атаки и области); пусто — из сценария и правил", Group = "Наведение")]
    public FriendlyFireDef? FriendlyFire { get; set; }

    // Use (docs/DECISIONS_3.md §42, §43).
    [Ed("Режим применения", "Прицельный — игрок указывает цель; автоматический — цель выбирается правилами в момент применения", Group = "Применение")]
    public SkillUseMode UseMode { get; set; } = SkillUseMode.Aimed;
    [Ed("Автоматический выбор цели", "Критерии, фильтры и приоритеты; ничьи — следующим правилом или случайно (генератор боя)", Group = "Применение"),
     EdShowIf(nameof(UseMode), SkillUseMode.Auto, SkillUseMode.Both)]
    public TargetSelectorDef AutoTarget { get; set; } = new();
    [Ed("Потеря цели", "Цель погибла, исчезла, вышла из дальности или стала недоступной", Group = "Применение")]
    public TargetLostPolicy OnTargetLost { get; set; } = TargetLostPolicy.Cancel;
    [Ed("Приоритет в меню", "0 — по силе навыка (от слабого к сильному); иначе — порядок вручную", Group = "Применение"), EdRange(0, 1000)]
    public int MenuPriority { get; set; }
    [Ed("Кольцо радиального меню", Group = "Применение")] public MenuRing Ring { get; set; } = MenuRing.Auto;
    [Ed("Подходить перед атакой", "Цель вне дальности или видимости: ближний бой — к свободному соседнему гексу, дальний — к позиции в дальности с линией видимости", Group = "Применение")]
    public bool Approach { get; set; } = true;
    [Ed("Движение перед применением", "Можно ли перемещаться, чтобы применить навык (выкл. — только с места)", Group = "Применение")]
    public bool AllowMoveBeforeUse { get; set; } = true;
    [Ed("Продолжать маршрут после атаки", "Выкл. — оставшиеся точки маршрута снимаются", Group = "Применение")]
    public bool ContinueRoute { get; set; } = true;
    [Ed("Взгляд после атаки", Group = "Применение")] public FacingAfterUse FacingAfter { get; set; } = FacingAfterUse.TowardTarget;
    [Ed("Направление после атаки", Group = "Применение"), EdShowIf(nameof(FacingAfter), FacingAfterUse.Set)]
    public HexDirection FacingAfterDirection { get; set; } = HexDirection.E;
    [Ed("Списывать ресурсы", "При запуске подготовки или в момент срабатывания", Group = "Применение")] public CostMoment PayAt { get; set; } = CostMoment.Start;
    [Ed("Начинать перезарядку", Group = "Применение")] public CostMoment CooldownAt { get; set; } = CostMoment.Release;

    [Ed("Может критовать", Group = "Урон")] public bool CanCrit { get; set; } = true;
    [Ed("Бонус шанса крита"), EdRange(-1, 1)] public double CritChanceBonus { get; set; }
    [Ed("Крит без доп. урона", "Крит не увеличивает урон — только эффекты крита", Group = "Урон")] public bool CritNoExtraDamage { get; set; }
    [Ed("Эффекты крита", "Применяются к цели при критическом ударе", Group = "Урон")] public List<SkillEffectDef> CritEffects { get; set; } = new();
    [Ed("Пробивает щиты", Group = "Урон")] public bool PiercesShields { get; set; }
    [Ed("Пробивает защиту", "Защитные стойки целей действуют по своему правилу «пробитие брони»", Group = "Урон")] public bool PiercesDefense { get; set; }

    // Series of hits (docs/DECISIONS_3.md §45).
    [Ed("Крит в серии", "Отдельный бросок для каждого удара или один на всю серию (по цели)", Group = "Серия")]
    public SeriesCritMode SeriesCrit { get; set; } = SeriesCritMode.PerHit;
    [Ed("Прекращать при гибели цели", "Оставшиеся удары отменяются (если этап не перенаправляет удар)", Group = "Серия")]
    public bool StopOnTargetDeath { get; set; } = true;
    [Ed("Смена цели", "После гибели цели — только при явной настройке", Group = "Серия")]
    public RetargetRule RetargetOnDeath { get; set; } = RetargetRule.Never;
    [Ed("Ресурс кончился во время серии", Group = "Серия")] public ResourceOutPolicy OnResourceOut { get; set; } = ResourceOutPolicy.CancelRemaining;
    [Ed("Прекращать при гибели исполнителя", "Выкл. — отложенные этапы срабатывают и после гибели (взрыв, ловушка)", Group = "Серия")]
    public bool StopOnCasterDeath { get; set; } = true;
    [Ed("Прекращать при контроле", "Оглушение/немота исполнителя отменяют оставшиеся удары", Group = "Серия")]
    public bool StopOnControl { get; set; }
    [Ed("Занят до конца серии", "Исполнитель не начинает новых действий, пока не прошли все удары (выкл. — только восстановление)", Group = "Серия")]
    public bool HoldDuringSeries { get; set; }
    [Ed("Бесплатно при условии", "Если условие выполнено при запуске — стоимость запуска не списывается", Group = "Стоимость")]
    public UnitConditionDef? FreeIf { get; set; }

    [Ed("Даёт пассивы", "Пока юнит знает навык (источник «навык»)", Group = "Основное"), EdRef(DefCategory.Passive)]
    public List<string> GrantsPassives { get; set; } = new();

    [Ed("Защита", "Параметры защитного навыка: снижение, уклонение, парирование, блоки, секторы, поддержание", Group = "Защита")]
    public DefenseStanceDef Defense { get; set; } = new();

    [Ed("Этапы", "Каждый этап — область и эффекты, со своей задержкой", Group = "Этапы")]
    public List<SkillStageDef> Stages { get; set; } = new();

    [Ed("Цвет", Group = "Вид"), EdColor] public string Color { get; set; } = "#e0a040";
    [Ed("Визуальный эффект", Group = "Вид")] public SkillVfx Vfx { get; set; } = SkillVfx.Slash;
    [Ed("Значок", "1–2 символа на кнопке", Group = "Вид")] public string Icon { get; set; } = "";
    [Ed("Иконка-изображение", "Ресурс «Иконка» (назначение «иконка навыка»); пусто — значок", Group = "Вид"), EdRef(DefCategory.Asset)] public string IconAsset { get; set; } = "";
    [Ed("Эффект подготовки", "Ресурс «Визуальный эффект» у исполнителя", Group = "Вид"), EdRef(DefCategory.Vfx)] public string CastVfx { get; set; } = "";
    [Ed("Эффект срабатывания", "В точке навыка", Group = "Вид"), EdRef(DefCategory.Vfx)] public string ImpactVfx { get; set; } = "";
    [Ed("Звук", Group = "Вид"), EdRef(DefCategory.Asset)] public string Sound { get; set; } = "";

    /// <summary>Directions are player-chosen for self/direction targeting.</summary>
    [JsonIgnore] public bool UsesChosenDirection => Targeting == TargetingMode.Self || Targeting == TargetingMode.Direction;

    public bool IsHarmful
    {
        get
        {
            foreach (var st in Stages)
            foreach (var e in st.Effects)
                if (e.Type == EffectType.Damage)
                    return true;
            return Kind == SkillKind.Attack;
        }
    }
}

public sealed class SkillStageDef : ContentPart
{
    [Ed("Название этапа")] public string Name { get; set; } = "";
    [Ed("Задержка, с", "После срабатывания навыка"), EdRange(0, 60)] public double Delay { get; set; }
    [Ed("Область")] public AreaDef Area { get; set; } = new();
    [Ed("Эффекты", "Применяются ко всем клеткам области (порядок внутри фазы — по списку)")]
    public List<SkillEffectDef> Effects { get; set; } = new();
    [Ed("Макс. целей", "0 = без ограничения; выбираются ближайшие к центру"), EdRange(0, 1000)] public int MaxTargets { get; set; }
    [Ed("Блокируется препятствиями", "Клетки, закрытые от точки применения, исключаются")] public bool BlockedByObstacles { get; set; } = true;
    [Ed("Проходит сквозь цели", "Для линии: нет — останавливается на первом юните")] public bool Pierce { get; set; } = true;

    // Parameters of the hit (docs/DECISIONS_3.md §45): every stage of a series is one hit.
    [Ed("Тип атаки", "Влияет на бонус крита, укрытие и защиту", Group = "Удар")] public StageAttackKind AttackKind { get; set; } = StageAttackKind.Inherit;
    [Ed("Множитель урона", "1 = 100 % величины эффектов урона этапа (0,7 — 70 % силы атаки)", Group = "Удар"), EdRange(0, 100)]
    public double DamageMultiplier { get; set; } = 1;
    [Ed("Фиксированная прибавка", "К урону каждого эффекта урона после множителя", Group = "Удар"), EdRange(-100000, 100000)]
    public double FlatBonus { get; set; }
    [Ed("Тип урона удара", "Пусто — тип эффекта; элемент задаётся типом урона", Group = "Удар"), EdRef(DefCategory.DamageType)]
    public string DamageType { get; set; } = "";
    [Ed("Попадание", Group = "Удар")] public StageHitMode HitMode { get; set; } = StageHitMode.Inherit;
    [Ed("Модификатор точности", "Прибавка к шансу попадания (0,1 = +10 %)", Group = "Удар"), EdRange(-1, 1)] public double AccuracyBonus { get; set; }
    [Ed("Критический удар", Group = "Удар")] public CritRule Crit { get; set; } = CritRule.Inherit;
    [Ed("Бонус шанса крита", Group = "Удар"), EdRange(-1, 1)] public double CritChanceBonus { get; set; }
    [Ed("Множитель крита", "0 — обычный (характеристика исполнителя)", Group = "Удар"), EdRange(0, 100)] public double CritMultiplier { get; set; }
    [Ed("Множитель шанса состояний", "Шанс эффектов «состояние» этапа × множитель", Group = "Удар"), EdRange(0, 100)]
    public double StatusChanceMultiplier { get; set; } = 1;
    [Ed("Стоимость удара", "За удар, при попадании, при промахе, возврат при убийстве/промахе", Group = "Удар")]
    public List<StageCostDef> Costs { get; set; } = new();
    [Ed("Тег удара", "Для пассивок и реакций (например finisher)", Group = "Удар")] public string Tag { get; set; } = "";
    [Ed("Смена цели", "Если цель серии погибла", Group = "Удар")] public RetargetRule Retarget { get; set; } = RetargetRule.Inherit;

    [Ed("Условие", "Проверяется перед этапом; пусто — всегда", Group = "Последовательность")] public UnitConditionDef? Condition { get; set; }
    [Ed("Если условие не выполнено", Group = "Последовательность")] public ConditionFailAction OnConditionFail { get; set; } = ConditionFailAction.SkipStage;
    [Ed("Повторить раз", "Этап выполняется N раз (конечное число; > 100 — ошибка)", Group = "Последовательность"), EdRange(1, 1000)]
    public int Repeat { get; set; } = 1;
    [Ed("Интервал повтора, с", Group = "Последовательность"), EdRange(0, 60)] public double RepeatInterval { get; set; } = 0.3;
    [Ed("Завершающий этап", "Выполняется и при безопасном прекращении серии (отступление, приземление)", Group = "Последовательность")]
    public bool Final { get; set; }
    [Ed("Движение исполнителя", "До удара этапа", Group = "Последовательность")] public StageMove Move { get; set; } = StageMove.None;
    [Ed("Гексов движения", "Рывок — до соседней с целью клетки, не дальше; отступление — на столько гексов"), EdRange(1, 20),
     EdShowIf(nameof(Move), StageMove.DashToTarget, StageMove.Retreat)]
    public int MoveDistance { get; set; } = 3;
    [Ed("ОД за гекс движения"), EdRange(0, 100), EdShowIf(nameof(Move), StageMove.DashToTarget, StageMove.Retreat)] public double MoveApPerHex { get; set; }

    [Ed("Анимация", "Роль клипа модели исполнителя", Group = "Вид")] public AnimRole Animation { get; set; } = AnimRole.Attack;
    [Ed("Клип", "Необязательно: имя клипа вместо роли", Group = "Вид")] public string AnimationClip { get; set; } = "";
    [Ed("Звук", Group = "Вид"), EdRef(DefCategory.Asset)] public string Sound { get; set; } = "";
    [Ed("Визуальный эффект", Group = "Вид"), EdRef(DefCategory.Vfx)] public string Vfx { get; set; } = "";
    [Ed("Снаряд", "Модель, траектория, скорость, след, эффекты; только вид — урон считает этап", Group = "Вид")] public ProjectileDef Projectile { get; set; } = new();

    [JsonIgnore] public bool HasDamage => Effects.Exists(e => e.Type == EffectType.Damage);
}

public enum AreaShape
{
    Single,
    Line,
    Cone,
    Sector,
    Radius,
    Ring,
    Cross,
    Chain,
    Mask,
    Global,
}

public sealed class AreaDef : ContentPart
{
    [Ed("Форма")] public AreaShape Shape { get; set; } = AreaShape.Single;

    [Ed("Размер", "Длина линии/радиус/дальность"), EdRange(0, 50),
     EdShowIf(nameof(Shape), AreaShape.Line, AreaShape.Cone, AreaShape.Sector, AreaShape.Radius, AreaShape.Ring, AreaShape.Cross)]
    public int Size { get; set; } = 1;

    [Ed("Внутренний радиус", "Для кольца"), EdRange(0, 50), EdShowIf(nameof(Shape), AreaShape.Ring)]
    public int InnerRadius { get; set; } = 1;

    [Ed("Угол сектора, °"), EdRange(60, 360), EdShowIf(nameof(Shape), AreaShape.Sector)]
    public double Angle { get; set; } = 120;

    [Ed("Прыжков цепи"), EdRange(0, 20), EdShowIf(nameof(Shape), AreaShape.Chain)]
    public int ChainJumps { get; set; } = 3;

    [Ed("Дальность прыжка"), EdRange(1, 20), EdShowIf(nameof(Shape), AreaShape.Chain)]
    public int ChainRange { get; set; } = 2;

    [Ed("Включать центр", "Клетку-якорь (исполнителя для наведения на себя/направление)"),
     EdShowIf(nameof(Shape), AreaShape.Line, AreaShape.Cone, AreaShape.Sector, AreaShape.Radius, AreaShape.Cross)]
    public bool IncludeCenter { get; set; }

    [Ed("Клетки маски", "Смещения относительно якоря при направлении на восток"), EdHidden]
    public List<MaskCellDef> Cells { get; set; } = new();
}

public sealed class MaskCellDef : ContentPart
{
    [Ed("Смещение")] public HexCoord Offset { get; set; }
    [Ed("Доп. эффекты клетки", "Применяются только в этой клетке маски")] public List<SkillEffectDef> Effects { get; set; } = new();
}

public enum EffectType
{
    Damage,
    Heal,
    ApplyStatus,
    RemoveStatus,
    Push,
    Pull,
    ChangeFacing,
    ModifyResource,
    ModifyAp,
    Shield,
    RemoveShield,
    ChangeTerrain,
    AddCellEffect,
    RemoveCellEffect,
    DestroyObstacle,
    Summon,
    TeleportCaster,
    // Part 2, catalog of operations (docs/DECISIONS_2.md §33).
    /// <summary>Stat change: for a time, or until the end of the battle (cannot be dispelled).</summary>
    ModifyStat,
    /// <summary>Points of an accumulating status (or stacks of a timed one), up or down.</summary>
    ModifyAccumulation,
    SetRoute,
    SetAiProfile,
    Message,
    PlayVfx,
    PlaySound,
    SetFlag,
    ActivateTrigger,
    // Part 6 (§104).
    /// <summary>Changes the scale (morale, sanity…) of the target by the amount.</summary>
    ChangeScale,
}

public enum FacingMode
{
    TowardSource,
    AwayFromSource,
    Opposite,
    Random,
    Set,
}

public sealed class SkillEffectDef : ContentPart
{
    [Ed("Действие")] public EffectType Type { get; set; } = EffectType.Damage;

    [Ed("На кого действует"),
     EdShowIf(nameof(Type), EffectType.Damage, EffectType.Heal, EffectType.ApplyStatus, EffectType.RemoveStatus, EffectType.Push,
         EffectType.Pull, EffectType.ChangeFacing, EffectType.ModifyResource, EffectType.ModifyAp, EffectType.Shield, EffectType.RemoveShield,
         EffectType.ModifyStat, EffectType.ModifyAccumulation, EffectType.SetRoute, EffectType.SetAiProfile, EffectType.ChangeScale)]
    public TargetFilterDef Filter { get; set; } = TargetFilterDef.EnemiesOnly();

    [Ed("Шанс"), EdRange(0, 1)] public double Chance { get; set; } = 1;

    [Ed("Требует попадания", "Применяется только если бросок попадания успешен"),
     EdShowIf(nameof(Type), EffectType.Damage, EffectType.ApplyStatus, EffectType.Push, EffectType.Pull, EffectType.ChangeFacing,
         EffectType.ModifyResource, EffectType.ModifyAp)]
    public bool RequiresHit { get; set; } = true;

    [Ed("Тип урона"), EdRef(DefCategory.DamageType, AllowEmpty = false), EdShowIf(nameof(Type), EffectType.Damage)]
    public string DamageType { get; set; } = "physical";

    [Ed("Величина", "Урон/лечение/очки состояния/изменение ресурса/объём щита; для накопления — изменение очков (стаков), может быть отрицательным"),
     EdShowIf(nameof(Type), EffectType.Damage, EffectType.Heal, EffectType.ApplyStatus, EffectType.ModifyResource, EffectType.ModifyAp, EffectType.Shield,
         EffectType.ModifyAccumulation, EffectType.ChangeScale)]
    public double Amount { get; set; } = 10;

    [Ed("Масштаб от характеристики", "Величина += характеристика исполнителя × коэффициент"), EdRef(DefCategory.Stat),
     EdShowIf(nameof(Type), EffectType.Damage, EffectType.Heal, EffectType.Shield)]
    public string ScalingStat { get; set; } = "";

    [Ed("Коэффициент масштаба"), EdShowIf(nameof(Type), EffectType.Damage, EffectType.Heal, EffectType.Shield)]
    public double ScalingFactor { get; set; }

    [Ed("Формула", "Необязательно. Заменяет величину. Переменные: amount, caster_*, target_*, level"),
     EdFormula("amount", "level", "distance", "caster_power", "caster_hp", "caster_hp_max", "target_hp", "target_hp_max", "target_armor"),
     EdShowIf(nameof(Type), EffectType.Damage, EffectType.Heal)]
    public string Formula { get; set; } = "";

    [Ed("Режим лечения", "Фиксированное, % от максимума или % от недостающего здоровья (величина — проценты)"), EdShowIf(nameof(Type), EffectType.Heal)]
    public HealMode HealMode { get; set; } = HealMode.Flat;

    [Ed("Щит", "Объём — «Величина», длительность — «Длительность»"), EdShowIf(nameof(Type), EffectType.Shield)]
    public ShieldSpecDef Shield { get; set; } = new();

    [Ed("Название снимаемого щита", "Пусто — все щиты"), EdShowIf(nameof(Type), EffectType.RemoveShield)]
    public string ShieldName { get; set; } = "";

    [Ed("Состояние"), EdRef(DefCategory.Status, AllowEmpty = false), EdShowIf(nameof(Type), EffectType.ApplyStatus, EffectType.RemoveStatus, EffectType.ModifyAccumulation)]
    public string Status { get; set; } = "";

    [Ed("Длительность, с", "0 = по умолчанию; для изменения характеристики — до конца боя"), EdRange(0, 3600),
     EdShowIf(nameof(Type), EffectType.ApplyStatus, EffectType.AddCellEffect, EffectType.Summon, EffectType.ChangeTerrain, EffectType.Shield, EffectType.ModifyStat)]
    public double Duration { get; set; }

    [Ed("Характеристика"), EdRef(DefCategory.Stat), EdShowIf(nameof(Type), EffectType.ModifyStat)]
    public string Stat { get; set; } = "";

    [Ed("Прибавка к характеристике"), EdShowIf(nameof(Type), EffectType.ModifyStat)]
    public double StatAdd { get; set; }

    [Ed("Множитель характеристики", "Доля: 0.2 = +20%"), EdShowIf(nameof(Type), EffectType.ModifyStat)]
    public double StatMul { get; set; }

    [Ed("Маршрут карты"), EdShowIf(nameof(Type), EffectType.SetRoute)]
    public string Route { get; set; } = "";

    [Ed("Профиль ИИ"), EdRef(DefCategory.AiProfile), EdShowIf(nameof(Type), EffectType.SetAiProfile)]
    public string AiProfile { get; set; } = "";

    [Ed("Текст / ID", "Сообщение; ID визуального или звукового эффекта; имя флага или триггера сценария"),
     EdShowIf(nameof(Type), EffectType.Message, EffectType.PlayVfx, EffectType.PlaySound, EffectType.SetFlag, EffectType.ActivateTrigger)]
    public string Text { get; set; } = "";

    [Ed("Расстояние"), EdRange(1, 20), EdShowIf(nameof(Type), EffectType.Push, EffectType.Pull)]
    public int Distance { get; set; } = 1;

    [Ed("Направление взгляда"), EdShowIf(nameof(Type), EffectType.ChangeFacing)]
    public FacingMode Facing { get; set; } = FacingMode.TowardSource;

    [Ed("Задать направление"), EdShowIf(nameof(Facing), FacingMode.Set)]
    public HexDirection SetDirection { get; set; } = HexDirection.E;

    [Ed("Ресурс"), EdRef(DefCategory.Resource, AllowEmpty = false), EdShowIf(nameof(Type), EffectType.ModifyResource)]
    public string Resource { get; set; } = "";

    [Ed("Местность"), EdRef(DefCategory.Terrain, AllowEmpty = false), EdShowIf(nameof(Type), EffectType.ChangeTerrain)]
    public string Terrain { get; set; } = "";

    [Ed("Эффект клетки"), EdRef(DefCategory.CellEffect, AllowEmpty = false), EdShowIf(nameof(Type), EffectType.AddCellEffect, EffectType.RemoveCellEffect)]
    public string CellEffect { get; set; } = "";

    [Ed("Уровень/заряды"), EdRange(1, 100), EdShowIf(nameof(Type), EffectType.AddCellEffect)]
    public int Level { get; set; } = 1;

    [Ed("Урон препятствию"), EdRange(0, 100000), EdShowIf(nameof(Type), EffectType.DestroyObstacle)]
    public int ObstacleDamage { get; set; } = 1000;

    [Ed("Призываемый юнит", "Простой призыв: один юнит на клетку навыка; при заданном описании призыва не нужен"), EdRef(DefCategory.Unit), EdShowIf(nameof(Type), EffectType.Summon)]
    public string SummonUnit { get; set; } = "";
    [Ed("Призыв", "Описание из «Призывов»: количество, расстановка, время жизни, лимиты, ИИ (ТЗ ч. 6, 20)"), EdRef(DefCategory.Summon), EdShowIf(nameof(Type), EffectType.Summon)]
    public string Summon { get; set; } = "";

    [JsonIgnore] public bool TargetsUnits => Type switch
    {
        EffectType.ChangeTerrain or EffectType.AddCellEffect or EffectType.RemoveCellEffect or EffectType.DestroyObstacle
            or EffectType.Summon or EffectType.TeleportCaster => false,
        _ when IsGlobal => false,
        _ => true,
    };

    /// <summary>Operations without a target (messages, effects, flags, triggers): once per application.</summary>
    [JsonIgnore] public bool IsGlobal => Type is EffectType.Message or EffectType.PlayVfx or EffectType.PlaySound or EffectType.SetFlag or EffectType.ActivateTrigger;

    public string Describe()
    {
        return Type switch
        {
            EffectType.Damage => $"Урон {Amount:0.#} ({DamageType})",
            EffectType.Heal => HealMode == HealMode.Flat ? $"Лечение {Amount:0.#}" : $"Лечение {Amount:0.#}% {(HealMode == HealMode.PercentMax ? "от максимума" : "от недостающего")}",
            EffectType.Shield => $"Щит «{Shield.Name}» {Amount:0.#}",
            EffectType.RemoveShield => string.IsNullOrEmpty(ShieldName) ? "Снять щиты" : $"Снять щит «{ShieldName}»",
            EffectType.ApplyStatus => $"Состояние {Status} ({Amount:0.#})",
            EffectType.RemoveStatus => $"Снять {Status}",
            EffectType.Push => $"Толчок на {Distance}",
            EffectType.Pull => $"Притяжение на {Distance}",
            EffectType.ChangeFacing => $"Поворот: {Facing}",
            EffectType.ModifyResource => $"{Resource} {Amount:+0.#;-0.#}",
            EffectType.ModifyAp => $"ОД {Amount:+0.#;-0.#}",
            EffectType.ChangeTerrain => $"Местность → {Terrain}",
            EffectType.AddCellEffect => $"Эффект клетки {CellEffect}",
            EffectType.RemoveCellEffect => $"Убрать эффект {CellEffect}",
            EffectType.DestroyObstacle => "Разрушить препятствие",
            EffectType.Summon => Summon != "" ? $"Призыв «{Summon}»" : $"Призыв {SummonUnit}",
            EffectType.TeleportCaster => "Переместить исполнителя",
            EffectType.ModifyStat => $"{Stat} {(StatAdd != 0 ? $"{StatAdd:+0.##;-0.##}" : "")}{(StatMul != 0 ? $" {StatMul:+0%;-0%}" : "")} " + (Duration > 0 ? $"на {Duration:0.#} с" : "до конца боя"),
            EffectType.ModifyAccumulation => $"Накопление {Status} {Amount:+0.#;-0.#}",
            EffectType.SetRoute => $"Маршрут → {Route}",
            EffectType.SetAiProfile => $"Профиль ИИ → {AiProfile}",
            EffectType.Message => $"Сообщение «{Text}»",
            EffectType.PlayVfx => $"Визуальный эффект {Text}",
            EffectType.PlaySound => $"Звук {Text}",
            EffectType.SetFlag => $"Флаг «{Text}»",
            EffectType.ActivateTrigger => $"Триггер «{Text}»",
            _ => Type.ToString(),
        };
    }
}
