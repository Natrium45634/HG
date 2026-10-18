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
}

public sealed class AutoTargetRuleDef
{
    [Ed("Правило")] public AutoTargetRule Rule { get; set; } = AutoTargetRule.Nearest;
    [Ed("Тег", "Тег типа юнита или тег сценария"), EdShowIf(nameof(Rule), AutoTargetRule.WithTag)] public string Tag { get; set; } = "";
    [Ed("Вес", "Для взвешенного выбора"), EdRange(0, 100)] public double Weight { get; set; } = 1;

    public override string ToString() => Rule + (Rule == AutoTargetRule.WithTag ? $" {Tag}" : "");
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

public sealed class AutoAttackStopDef
{
    [Ed("Условие")] public AutoAttackStopKind When { get; set; } = AutoAttackStopKind.PlayerCommand;
    [Ed("Доля здоровья"), EdRange(0, 1), EdShowIf(nameof(When), AutoAttackStopKind.HpBelow)] public double HpFraction { get; set; } = 0.25;
    [Ed("Состояние"), EdRef(DefCategory.Status), EdShowIf(nameof(When), AutoAttackStopKind.HasStatus)] public string Status { get; set; } = "";
}

public sealed class AutoAttackDef
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

public sealed class AutoAttackRules
{
    [Ed("Автоатаки включены")] public bool Enabled { get; set; } = true;
    [Ed("Для юнитов игрока")] public bool PlayerUnits { get; set; } = true;
    [Ed("Для юнитов под ИИ", "ИИ и автоатаки используют одни и те же ОД; ручные приказы ИИ имеют приоритет")] public bool AiUnits { get; set; } = true;
    [Ed("Тег защищаемого объекта", "Юниты/объекты с этим тегом — для правила «ближайший к защищаемому»")] public string ProtectedTag { get; set; } = "protected";
    [Ed("Тег босса")] public string BossTag { get; set; } = "boss";
}
