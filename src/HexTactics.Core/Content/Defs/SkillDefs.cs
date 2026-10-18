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

public sealed class InterruptDef
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

    [Ed("Может критовать", Group = "Урон")] public bool CanCrit { get; set; } = true;
    [Ed("Бонус шанса крита"), EdRange(-1, 1)] public double CritChanceBonus { get; set; }
    [Ed("Крит без доп. урона", "Крит не увеличивает урон — только эффекты крита", Group = "Урон")] public bool CritNoExtraDamage { get; set; }
    [Ed("Эффекты крита", "Применяются к цели при критическом ударе", Group = "Урон")] public List<SkillEffectDef> CritEffects { get; set; } = new();
    [Ed("Пробивает щиты", Group = "Урон")] public bool PiercesShields { get; set; }

    [Ed("Этапы", "Каждый этап — область и эффекты, со своей задержкой", Group = "Этапы")]
    public List<SkillStageDef> Stages { get; set; } = new();

    [Ed("Цвет", Group = "Вид"), EdColor] public string Color { get; set; } = "#e0a040";
    [Ed("Визуальный эффект", Group = "Вид")] public SkillVfx Vfx { get; set; } = SkillVfx.Slash;
    [Ed("Значок", "1–2 символа на кнопке", Group = "Вид")] public string Icon { get; set; } = "";

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

public sealed class SkillStageDef
{
    [Ed("Название этапа")] public string Name { get; set; } = "";
    [Ed("Задержка, с", "После срабатывания навыка"), EdRange(0, 60)] public double Delay { get; set; }
    [Ed("Область")] public AreaDef Area { get; set; } = new();
    [Ed("Эффекты", "Применяются ко всем клеткам области (порядок внутри фазы — по списку)")]
    public List<SkillEffectDef> Effects { get; set; } = new();
    [Ed("Макс. целей", "0 = без ограничения; выбираются ближайшие к центру"), EdRange(0, 1000)] public int MaxTargets { get; set; }
    [Ed("Блокируется препятствиями", "Клетки, закрытые от точки применения, исключаются")] public bool BlockedByObstacles { get; set; } = true;
    [Ed("Проходит сквозь цели", "Для линии: нет — останавливается на первом юните")] public bool Pierce { get; set; } = true;
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

public sealed class AreaDef
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

public sealed class MaskCellDef
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
}

public enum FacingMode
{
    TowardSource,
    AwayFromSource,
    Opposite,
    Random,
    Set,
}

public sealed class SkillEffectDef
{
    [Ed("Действие")] public EffectType Type { get; set; } = EffectType.Damage;

    [Ed("На кого действует"),
     EdShowIf(nameof(Type), EffectType.Damage, EffectType.Heal, EffectType.ApplyStatus, EffectType.RemoveStatus, EffectType.Push,
         EffectType.Pull, EffectType.ChangeFacing, EffectType.ModifyResource, EffectType.ModifyAp, EffectType.Shield, EffectType.RemoveShield)]
    public TargetFilterDef Filter { get; set; } = TargetFilterDef.EnemiesOnly();

    [Ed("Шанс"), EdRange(0, 1)] public double Chance { get; set; } = 1;

    [Ed("Требует попадания", "Применяется только если бросок попадания успешен"),
     EdShowIf(nameof(Type), EffectType.Damage, EffectType.ApplyStatus, EffectType.Push, EffectType.Pull, EffectType.ChangeFacing,
         EffectType.ModifyResource, EffectType.ModifyAp)]
    public bool RequiresHit { get; set; } = true;

    [Ed("Тип урона"), EdRef(DefCategory.DamageType, AllowEmpty = false), EdShowIf(nameof(Type), EffectType.Damage)]
    public string DamageType { get; set; } = "physical";

    [Ed("Величина", "Урон/лечение/очки состояния/изменение ресурса/объём щита"),
     EdShowIf(nameof(Type), EffectType.Damage, EffectType.Heal, EffectType.ApplyStatus, EffectType.ModifyResource, EffectType.ModifyAp, EffectType.Shield)]
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

    [Ed("Состояние"), EdRef(DefCategory.Status, AllowEmpty = false), EdShowIf(nameof(Type), EffectType.ApplyStatus, EffectType.RemoveStatus)]
    public string Status { get; set; } = "";

    [Ed("Длительность, с", "0 = по умолчанию"), EdRange(0, 3600),
     EdShowIf(nameof(Type), EffectType.ApplyStatus, EffectType.AddCellEffect, EffectType.Summon, EffectType.ChangeTerrain, EffectType.Shield)]
    public double Duration { get; set; }

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

    [Ed("Призываемый юнит"), EdRef(DefCategory.Unit, AllowEmpty = false), EdShowIf(nameof(Type), EffectType.Summon)]
    public string SummonUnit { get; set; } = "";

    [JsonIgnore] public bool TargetsUnits => Type switch
    {
        EffectType.ChangeTerrain or EffectType.AddCellEffect or EffectType.RemoveCellEffect or EffectType.DestroyObstacle
            or EffectType.Summon or EffectType.TeleportCaster => false,
        _ => true,
    };

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
            EffectType.Summon => $"Призыв {SummonUnit}",
            EffectType.TeleportCaster => "Переместить исполнителя",
            _ => Type.ToString(),
        };
    }
}
