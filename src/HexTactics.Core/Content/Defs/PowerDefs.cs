using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace HexTactics.Core.Content;

// Power, level and threat (docs/DECISIONS_4.md §65–§67): the project profile in the rules, the rank and role of a
// template, scaling profiles for «Подогнать под уровень» and for the level of an instance.

/// <summary>The rank of a template (docs/DECISIONS_4.md §65): independent of its level.</summary>
public enum UnitRank
{
    [Ed("Обычный")] Normal,
    [Ed("Элитный")] Elite,
    [Ed("Лидер")] Leader,
    [Ed("Босс")] Boss,
    [Ed("Свой")] Custom,
}

/// <summary>The role of a template: the weights of the power model and the default scaling profile.</summary>
public enum PowerRole
{
    [Ed("Авто (по вкладам)")] Auto,
    [Ed("Универсальный")] Universal,
    [Ed("Танк")] Tank,
    [Ed("Боец")] Fighter,
    [Ed("Стрелок")] Shooter,
    [Ed("Убийца")] Assassin,
    [Ed("Поддержка")] Support,
    [Ed("Контролёр")] Controller,
    [Ed("Босс")] Boss,
}

/// <summary>How a component grows (docs/DECISIONS_4.md §65: linear, diminishing, thresholds, caps).</summary>
public enum CurveKind
{
    [Ed("Линейно")] Linear,
    [Ed("Убывающая отдача (степень)", "x^k, k < 1 — каждый следующий прирост ценнее меньше")] Power,
    [Ed("Логарифм", "1 + ln x")] Log,
}

public sealed class ComponentCurveDef
{
    [Ed("Зависимость")] public CurveKind Kind { get; set; } = CurveKind.Linear;
    [Ed("Степень"), EdRange(0.1, 2), EdShowIf(nameof(Kind), CurveKind.Power)] public double Exponent { get; set; } = 0.85;
    [Ed("Предел", "Наибольшее значение составляющей (к эталону); 0 — без предела"), EdRange(0, 1000)] public double Cap { get; set; } = 25;

    public double Apply(double x)
    {
        if (x <= 0) return 0;
        double y = Kind switch
        {
            CurveKind.Power => System.Math.Pow(x, Exponent),
            CurveKind.Log => x >= 1 ? 1 + System.Math.Log(x) : x,
            _ => x,
        };
        return Cap > 0 ? System.Math.Min(Cap, y) : y;
    }

    public override string ToString() => Kind + (Kind == CurveKind.Power ? $" ^{Exponent:0.##}" : "") + (Cap > 0 ? $" ≤{Cap:0.#}" : "");
}

/// <summary>Weights of the components for a role: H, D, R, M are shares (summed to 1), C and S add on top.</summary>
public sealed class RoleWeightsDef
{
    [Ed("Роль")] public PowerRole Role { get; set; } = PowerRole.Universal;
    [Ed("Здоровье и защита (H)"), EdRange(0, 10)] public double H { get; set; } = 0.4;
    [Ed("Урон (D)"), EdRange(0, 10)] public double D { get; set; } = 0.4;
    [Ed("Дальность (R)"), EdRange(0, 10)] public double R { get; set; } = 0.1;
    [Ed("Мобильность (M)"), EdRange(0, 10)] public double M { get; set; } = 0.1;
    [Ed("Контроль (C)", "Вклад на единицу контроля (эталон контроля = 1)"), EdRange(0, 10)] public double C { get; set; } = 0.5;
    [Ed("Поддержка и синергии (S)", "Вклад на единицу пользы союзникам"), EdRange(0, 10)] public double S { get; set; } = 1;

    public override string ToString() => $"{Role}: H {H:0.##} D {D:0.##} R {R:0.##} M {M:0.##} C {C:0.##} S {S:0.##}";
}

/// <summary>The scale «сила → уровень».</summary>
public enum LevelScaleKind
{
    [Ed("Таблица порогов", "Кусочно-линейная по точкам «уровень — сила»")] Table,
    [Ed("Линейная", "Сила = 1 + (уровень − 1) × шаг")] Linear,
    [Ed("Степенная", "Сила = уровень ^ степень")] Power,
}

public sealed class LevelPointDef
{
    [Ed("Уровень"), EdRange(0, 1000)] public double Level { get; set; } = 1;
    [Ed("Сила", "Относительно эталона (эталон = 1)"), EdRange(0, 100_000)] public double Power { get; set; } = 1;

    public override string ToString() => $"ур. {Level:0.#} = {Power:0.##}";
}

public sealed class LevelScaleDef
{
    [Ed("Вид шкалы")] public LevelScaleKind Kind { get; set; } = LevelScaleKind.Table;
    [Ed("Точки", "Уровень и сила; между точками — линейно, за последней — по наклону последнего отрезка"), EdShowIf(nameof(Kind), LevelScaleKind.Table)]
    public List<LevelPointDef> Points { get; set; } = new()
    {
        new LevelPointDef { Level = 1, Power = 1 }, new LevelPointDef { Level = 10, Power = 4 },
        new LevelPointDef { Level = 30, Power = 12 }, new LevelPointDef { Level = 50, Power = 20 },
    };
    [Ed("Шаг силы за уровень"), EdRange(0.001, 100), EdShowIf(nameof(Kind), LevelScaleKind.Linear)] public double Step { get; set; } = 0.4;
    [Ed("Степень"), EdRange(0.05, 5), EdShowIf(nameof(Kind), LevelScaleKind.Power)] public double Exponent { get; set; } = 0.8;

    /// <summary>The level of a power (exact, may be fractional).</summary>
    public double LevelOf(double power)
    {
        power = System.Math.Max(0, power);
        switch (Kind)
        {
            case LevelScaleKind.Linear:
                return 1 + (power - 1) / System.Math.Max(1e-9, Step);
            case LevelScaleKind.Power:
                return System.Math.Pow(power, 1 / System.Math.Max(1e-9, Exponent));
        }
        var pts = Points.OrderBy(p => p.Power).ToList();
        if (pts.Count == 0) return power;
        if (pts.Count == 1) return pts[0].Level * power / System.Math.Max(1e-9, pts[0].Power);
        if (power <= pts[0].Power) return pts[0].Level * power / System.Math.Max(1e-9, pts[0].Power);
        for (int i = 1; i < pts.Count; i++)
            if (power <= pts[i].Power || i == pts.Count - 1)
            {
                var a = pts[i - 1];
                var b = pts[i];
                double t = (power - a.Power) / System.Math.Max(1e-9, b.Power - a.Power);
                return a.Level + t * (b.Level - a.Level);
            }
        return pts[^1].Level;
    }

    /// <summary>The power of a level (the inverse).</summary>
    public double PowerOf(double level)
    {
        switch (Kind)
        {
            case LevelScaleKind.Linear:
                return 1 + (level - 1) * Step;
            case LevelScaleKind.Power:
                return System.Math.Pow(System.Math.Max(0, level), Exponent);
        }
        var pts = Points.OrderBy(p => p.Level).ToList();
        if (pts.Count == 0) return level;
        if (pts.Count == 1 || level <= pts[0].Level) return pts[0].Power * level / System.Math.Max(1e-9, pts[0].Level);
        for (int i = 1; i < pts.Count; i++)
            if (level <= pts[i].Level || i == pts.Count - 1)
            {
                var a = pts[i - 1];
                var b = pts[i];
                double t = (level - a.Level) / System.Math.Max(1e-9, b.Level - a.Level);
                return a.Power + t * (b.Power - a.Power);
            }
        return pts[^1].Power;
    }

    public override string ToString() => Kind == LevelScaleKind.Table ? string.Join(", ", Points) : Kind.ToString();
}

public sealed class RoleScaleDef
{
    [Ed("Роль")] public PowerRole Role { get; set; } = PowerRole.Support;
    [Ed("Шкала")] public LevelScaleDef Scale { get; set; } = new();
}

public sealed class DamageTypeWeightDef
{
    [Ed("Тип урона"), EdRef(DefCategory.DamageType, AllowEmpty = false)] public string Type { get; set; } = "physical";
    [Ed("Доля", "Насколько часто этот урон встречается в проекте"), EdRange(0, 100)] public double Weight { get; set; } = 1;

    public override string ToString() => $"{Type} ×{Weight:0.##}";
}

/// <summary>
/// The project profile «Оценка силы» (docs/DECISIONS_4.md §65): the reference unit of level 1, the main damage
/// types, the weights of the roles, the curves, the scales of level. Changing the reference changes every level.
/// </summary>
public sealed class PowerRulesDef
{
    [Ed("Эталон уровня 1", "Шаблон, относительно которого считаются все составляющие; пусто — встроенный эталон (100 здоровья, удар 10)"), EdRef(DefCategory.Unit)]
    public string ReferenceUnit { get; set; } = "";
    [Ed("Основные типы урона", "Профиль эффективного здоровья; пусто — физический урон наполовину, остальные корневые типы поровну")]
    public List<DamageTypeWeightDef> DamageTypes { get; set; } = new();
    [Ed("Веса ролей")] public List<RoleWeightsDef> Roles { get; set; } = DefaultRoles();
    [Ed("Здоровье и защита: зависимость")] public ComponentCurveDef CurveH { get; set; } = new() { Kind = CurveKind.Power, Exponent = 0.9 };
    [Ed("Урон: зависимость")] public ComponentCurveDef CurveD { get; set; } = new() { Kind = CurveKind.Power, Exponent = 0.95 };
    [Ed("Дальность: зависимость")] public ComponentCurveDef CurveR { get; set; } = new() { Cap = 4 };
    [Ed("Мобильность: зависимость")] public ComponentCurveDef CurveM { get; set; } = new() { Kind = CurveKind.Power, Exponent = 0.7, Cap = 4 };
    [Ed("Зоны дальности", "Порог дальности — множитель (новая зона поражения — скачок)")] public List<LevelPointDef> RangeZones { get; set; } = new()
    {
        new LevelPointDef { Level = 1, Power = 1 }, new LevelPointDef { Level = 2, Power = 1.2 }, new LevelPointDef { Level = 4, Power = 1.45 },
        new LevelPointDef { Level = 7, Power = 1.7 }, new LevelPointDef { Level = 10, Power = 1.85 },
    };
    [Ed("Плотность целей в области", "Ожидаемое число врагов на каждый гекс области сверх первого"), EdRange(0, 1)] public double AreaDensity { get; set; } = 0.3;
    [Ed("Длительность эталонного боя, с", "Для восстановления, самолечения, пассивов со сроком"), EdRange(1, 600)] public double FightSeconds { get; set; } = 20;
    [Ed("Эталон контроля", "Секунд контроля в секунду, дающих 1 единицу C"), EdRange(0.01, 10)] public double ControlReference { get; set; } = 0.25;
    [Ed("Союзников в типичной группе", "Контекст «типичная группа»"), EdRange(0, 50)] public int TypicalAllies { get; set; } = 3;
    [Ed("Шкала уровня")] public LevelScaleDef Scale { get; set; } = new();
    [Ed("Шкалы ролей", "Своя шкала для роли (иначе общая)")] public List<RoleScaleDef> RoleScales { get; set; } = new();
    [Ed("Отдельная шкала боссов", "Для ранга «босс»; пусто — общая")] public LevelScaleDef? BossScale { get; set; }
    [Ed("Стоимость угрозы эталона", "Базовая стоимость угрозы эталонного юнита"), EdRange(0.1, 1000)] public double ReferenceThreat { get; set; } = 2;

    public static List<RoleWeightsDef> DefaultRoles() => new()
    {
        new RoleWeightsDef { Role = PowerRole.Universal, H = 0.4, D = 0.4, R = 0.1, M = 0.1 },
        new RoleWeightsDef { Role = PowerRole.Tank, H = 0.55, D = 0.3, R = 0.05, M = 0.1, C = 0.6 },
        new RoleWeightsDef { Role = PowerRole.Fighter, H = 0.4, D = 0.45, R = 0.05, M = 0.1 },
        new RoleWeightsDef { Role = PowerRole.Shooter, H = 0.3, D = 0.45, R = 0.15, M = 0.1 },
        new RoleWeightsDef { Role = PowerRole.Assassin, H = 0.3, D = 0.45, R = 0.05, M = 0.2 },
        new RoleWeightsDef { Role = PowerRole.Support, H = 0.4, D = 0.3, R = 0.15, M = 0.15, S = 1.2 },
        new RoleWeightsDef { Role = PowerRole.Controller, H = 0.4, D = 0.3, R = 0.15, M = 0.15, C = 0.8 },
        new RoleWeightsDef { Role = PowerRole.Boss, H = 0.45, D = 0.4, R = 0.1, M = 0.05, C = 0.6, S = 1 },
    };

    public RoleWeightsDef Weights(PowerRole role) =>
        Roles.FirstOrDefault(r => r.Role == role) ?? Roles.FirstOrDefault(r => r.Role == PowerRole.Universal) ?? new RoleWeightsDef();

    public LevelScaleDef ScaleFor(PowerRole role, UnitRank rank) =>
        rank == UnitRank.Boss && BossScale != null ? BossScale : RoleScales.FirstOrDefault(r => r.Role == role)?.Scale ?? Scale;
}

/// <summary>What a parameter of a scaling profile changes.</summary>
public enum ScaleTarget
{
    [Ed("Характеристика")] Stat,
    [Ed("Сила навыков", "Бонус наносимого урона шаблона («*»), навыки не меняются")] SkillDamage,
    [Ed("Сопротивления", "Существующие сопротивления шаблона (не сильнее −90 %)")] Resistances,
}

/// <summary>A parameter a scaling profile may change, with its weight and limits (docs/DECISIONS_4.md §66).</summary>
public sealed class ScaleParamDef
{
    [Ed("Что меняется")] public ScaleTarget Target { get; set; } = ScaleTarget.Stat;
    [Ed("Характеристика"), EdRef(DefCategory.Stat), EdShowIf(nameof(Target), ScaleTarget.Stat)] public string Stat { get; set; } = Stats.HpMax;
    [Ed("Вес", "Доля роста: значение × множитель ^ вес (пропорции роли сохраняются)"), EdRange(0, 5)] public double Weight { get; set; } = 1;
    [Ed("Минимум", "0 — без минимума"), EdRange(0, 1_000_000)] public double Min { get; set; }
    [Ed("Максимум", "0 — без максимума"), EdRange(0, 1_000_000)] public double Max { get; set; }
    [Ed("Не более чем в N раз", "Допустимый диапазон изменения от исходного; 0 — без ограничения"), EdRange(0, 1000)] public double MaxChange { get; set; } = 10;
    [Ed("Шаг", "Округление нового значения; 0 — без округления"), EdRange(0, 10_000)] public double Step { get; set; } = 1;
    [Ed("Приоритет", "Подсказка отчёта: что разрешить первым, если цель недостижима"), EdRange(-100, 100)] public int Priority { get; set; }
    [Ed("Запрет автоматического изменения")] public bool Locked { get; set; }

    [JsonIgnore] public string Key => Target == ScaleTarget.Stat ? "stat:" + Stat : Target.ToString();

    public override string ToString() => (Target == ScaleTarget.Stat ? Stat : Target.ToString()) + $" ^{Weight:0.##}" + (Locked ? " (запрет)" : "");
}

/// <summary>A scaling profile: which parameters «Подогнать под уровень» and the level of an instance may change.</summary>
public sealed class ScalingProfileDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.ScalingProfile;

    [Ed("Роль", "Для какой роли профиль подходит (подсказка)")] public PowerRole Role { get; set; } = PowerRole.Universal;
    [Ed("Параметры")] public List<ScaleParamDef> Params { get; set; } = new();
}
