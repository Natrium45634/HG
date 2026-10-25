using System.Collections.Generic;

namespace HexTactics.Core.Content;

// Auto-attacks (docs/DECISIONS_2.md §28): allies and enemies alike, through the common skill and damage path.

public enum AutoTargetRule
{
    [Ed("Ближайший")] Nearest,
    [Ed("Наименьшее здоровье")] LowestHp,
    [Ed("Наименьшая доля здоровья")] LowestHpFraction,
    [Ed("Самый опасный")] MostDangerous,
    [Ed("Ближайший к защищаемому объекту")] NearestToProtected,
    [Ed("Босс")] Boss,
    [Ed("С тегом")] WithTag,
    [Ed("Тот, кого я блокирую")] BlockedByMe,
    [Ed("Тот, кто блокирует меня")] BlockingMe,
    [Ed("Текущая цель")] CurrentTarget,
    // Part 3 (docs/DECISIONS_3.md §43): the full list of criteria of automatic target choice.
    [Ed("Случайная")] Random,
    [Ed("Самая дальняя")] Farthest,
    [Ed("Наибольшее здоровье")] HighestHp,
    [Ed("Наибольшее макс. здоровье")] HighestMaxHp,
    [Ed("Наименьшее макс. здоровье")] LowestMaxHp,
    [Ed("Наибольшая физическая сила")] HighestPower,
    [Ed("Наименьшая физическая сила")] LowestPower,
    [Ed("Наибольшая магическая сила")] HighestMagicPower,
    [Ed("Наименьшая магическая сила")] LowestMagicPower,
    [Ed("Босс или элита")] BossOrElite,
    [Ed("С уязвимостью к урону навыка")] Vulnerable,
    [Ed("С состоянием")] WithStatus,
    [Ed("Добивается одной атакой")] Killable,
    [Ed("Больше всего противников рядом")] MostEnemiesNearby,
}

public sealed class AutoTargetRuleDef : ContentPart
{
    [Ed("Правило")] public AutoTargetRule Rule { get; set; } = AutoTargetRule.Nearest;
    [Ed("Тег", "Тег типа юнита или тег сценария"), EdShowIf(nameof(Rule), AutoTargetRule.WithTag)] public string Tag { get; set; } = "";
    [Ed("Состояние"), EdRef(DefCategory.Status), EdShowIf(nameof(Rule), AutoTargetRule.WithStatus)] public string Status { get; set; } = "";
    [Ed("Радиус «рядом»", "Для «больше всего противников рядом»"), EdRange(1, 10), EdShowIf(nameof(Rule), AutoTargetRule.MostEnemiesNearby)]
    public int Radius { get; set; } = 1;
    [Ed("Только такие цели", "Фильтр: остальные цели не выбираются вовсе (иначе — приоритет: «если есть — их, иначе остальных»)")]
    public bool Strict { get; set; }
    [Ed("Вес", "Для взвешенного выбора"), EdRange(0, 100)] public double Weight { get; set; } = 1;

    public override string ToString() => Rule + (Rule == AutoTargetRule.WithTag ? $" {Tag}" : Rule == AutoTargetRule.WithStatus ? $" {Status}" : "") + (Strict ? " (фильтр)" : "");

    /// <summary>Criteria with a yes/no answer (they can be a strict filter).</summary>
    public bool IsBoolean => Rule is AutoTargetRule.Boss or AutoTargetRule.BossOrElite or AutoTargetRule.WithTag or AutoTargetRule.WithStatus or AutoTargetRule.Vulnerable
        or AutoTargetRule.Killable or AutoTargetRule.BlockedByMe or AutoTargetRule.BlockingMe or AutoTargetRule.CurrentTarget;
}

public enum TargetSelectionMode
{
    [Ed("Последовательно (первое правило; ничьи — следующим)")] Sequential,
    [Ed("Взвешенная сумма")] Weighted,
}

public enum ResourceShortage
{
    [Ed("Пропустить атаку")] Skip,
    [Ed("Ждать ресурсов")] Wait,
    [Ed("Тратить зарезервированные ОД")] UseReserved,
}

public enum AutoAttackStopKind
{
    [Ed("Есть ручная команда")] PlayerCommand,
    [Ed("Здоровье ниже доли")] HpBelow,
    [Ed("Есть состояние")] HasStatus,
    [Ed("Юнит в защитной стойке")] Defending,
}

public sealed class AutoAttackStopDef : ContentPart
{
    [Ed("Условие")] public AutoAttackStopKind When { get; set; } = AutoAttackStopKind.PlayerCommand;
    [Ed("Доля здоровья"), EdRange(0, 1), EdShowIf(nameof(When), AutoAttackStopKind.HpBelow)] public double HpFraction { get; set; } = 0.25;
    [Ed("Состояние"), EdRef(DefCategory.Status), EdShowIf(nameof(When), AutoAttackStopKind.HasStatus)] public string Status { get; set; } = "";
}

public sealed class AutoAttackDef : ContentPart
{
    [Ed("Включена")] public bool Enabled { get; set; } = true;
    [Ed("Навык", "Пусто — обычная атака юнита"), EdRef(DefCategory.Skill)] public string Skill { get; set; } = "";
    [Ed("Дальность", "0 — из навыка"), EdRange(0, 50)] public int Range { get; set; }
    [Ed("Размер области", "0 — из навыка"), EdRange(0, 20)] public int AreaSize { get; set; }
    [Ed("Допустимые цели")] public TargetFilterDef Targets { get; set; } = new() { Enemies = true, Neutrals = false };
    [Ed("Слои целей", "Пусто — как у навыка"), EdRef(DefCategory.Layer)] public List<string> TargetLayers { get; set; } = new();
    [Ed("Только обнаруженные цели", "Цель должна быть видна стороне юнита")] public bool RequireDetection { get; set; } = true;
    [Ed("Нужна линия видимости", "Вдобавок к требованию навыка")] public bool RequireLoS { get; set; } = true;
    [Ed("Стоимость ОД", "−1 — из навыка"), EdRange(-1, 100)] public double ApCost { get; set; } = -1;
    [Ed("Интервал, с", "Между атаками"), EdRange(0.1, 60)] public double Interval { get; set; } = 1.5;
    [Ed("Выбор цели")] public TargetSelectionMode Selection { get; set; } = TargetSelectionMode.Sequential;
    [Ed("Правила выбора", "По порядку; пусто — ближайший")] public List<AutoTargetRuleDef> Rules { get; set; } = new();
    [Ed("Случайно среди равных", "Только через генератор симуляции (воспроизводимо)")] public bool RandomTies { get; set; }
    [Ed("Атаковать в движении", "Между шагами приказа движения")] public bool WhileMoving { get; set; }
    [Ed("Атаковать, когда блокирует")] public bool WhenBlocking { get; set; } = true;
    [Ed("Атаковать, когда заблокирован")] public bool WhenBlocked { get; set; } = true;
    [Ed("Не атаковать, если")] public List<AutoAttackStopDef> StopWhen { get; set; } = new() { new AutoAttackStopDef() };
    [Ed("При нехватке ОД/ресурсов")] public ResourceShortage OnShortage { get; set; } = ResourceShortage.Skip;
}

public sealed class AutoAttackRules : ContentPart
{
    [Ed("Автоатаки включены")] public bool Enabled { get; set; } = true;
    [Ed("Для юнитов игрока")] public bool PlayerUnits { get; set; } = true;
    [Ed("Для юнитов под ИИ", "ИИ и автоатаки используют одни и те же ОД; ручные приказы ИИ имеют приоритет")] public bool AiUnits { get; set; } = true;
    [Ed("Тег защищаемого объекта", "Юниты/объекты с этим тегом — для правила «ближайший к защищаемому»")] public string ProtectedTag { get; set; } = "protected";
    [Ed("Тег босса")] public string BossTag { get; set; } = "boss";
    [Ed("Тег элиты", "Для правила «босс или элита»")] public string EliteTag { get; set; } = "elite";
}

/// <summary>
/// Rules of automatic target choice of a skill (docs/DECISIONS_3.md §43): criteria in order (or weighted),
/// filters, random choice among equals through the simulation's generator.
/// </summary>
public sealed class TargetSelectorDef : ContentPart
{
    [Ed("Правила выбора", "По порядку: каждое следующее решает ничьи предыдущего; пусто — ближайшая цель")]
    public List<AutoTargetRuleDef> Rules { get; set; } = new();
    [Ed("Способ")] public TargetSelectionMode Mode { get; set; } = TargetSelectionMode.Sequential;
    [Ed("Случайно среди равных", "Через генератор симуляции — воспроизводимо")] public bool RandomTies { get; set; }

    public override string ToString() => Rules.Count == 0 ? "ближайшая" : string.Join(" → ", Rules);
}
