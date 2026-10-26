using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HexTactics.Core.Content;

// Part 6 (ТЗ ч. 6, 1–2; docs/DECISIONS_6.md §96): the rules of a game mode — switches of subsystems and references to the
// rules that already exist, not a second engine of modes — and the squad the player takes into a battle.

/// <summary>Who of the chosen squad stands on the field when the battle begins.</summary>
public enum SquadStart
{
    [Ed("Весь отряд на поле", "Свободная RTS: отряд расставляется в зоне расстановки при начале боя")] AllOnField,
    [Ed("Стартовый состав сценария", "Гибрид: на поле — бойцы из «Стартовый состав на поле», остальные ждут вызова")] StartDeployed,
    [Ed("Никто", "Тактический TD: бойцы выходят только расстановкой или вызовом")] NoneOnField,
}

/// <summary>
/// «Правила режима» (ТЗ ч. 6, 1.1–1.5): the switches of the subsystems of part 6 and a reference to a preset of the
/// battle rules. A scenario chooses one and may set its own value of any field (<see cref="GameRulesOverrideDef"/>).
/// </summary>
public sealed class GameRulesDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.GameRules;

    [Ed("Пресет правил боя", "Числа боя: ОД, урон, обзор…; пусто — глобальные правила. Пресет, выбранный в самом сценарии, сильнее", Group = "Правила"),
     EdRef(DefCategory.RulesPreset)]
    public string RulesPreset { get; set; } = "";
    [Ed("Отряд на поле в начале", "Для сценариев с отрядом игрока: кто из выбранных бойцов стоит на поле сразу", Group = "Отряд")]
    public SquadStart SquadStart { get; set; } = SquadStart.AllOnField;
    [Ed("Блокирование", "Удержание врагов бойцами с лимитом блокирования (часть 2); выключено — никто никого не держит", Group = "Системы")]
    public bool Blocking { get; set; } = true;
    [Ed("Урон по союзникам в области", "Не задано — из правил боя («Урон по союзникам»)", Group = "Системы")]
    public bool? FriendlyFire { get; set; }
}

/// <summary>A scenario's own values over its rules of the mode: an unset field is inherited from the preset (ТЗ ч. 6, 1.5).</summary>
public sealed class GameRulesOverrideDef : ContentPart
{
    [Ed("Отряд на поле в начале")] public SquadStart? SquadStart { get; set; }
    [Ed("Блокирование")] public bool? Blocking { get; set; }
    [Ed("Урон по союзникам в области")] public bool? FriendlyFire { get; set; }
}

/// <summary>A saved squad (ТЗ ч. 6, 2.1): templates in order; a «build» is a template or its version (§96).</summary>
public sealed class SquadPresetDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.SquadPreset;

    [Ed("Бойцы", "Шаблоны по порядку; повтор — второй боец того же шаблона"), EdRef(DefCategory.Unit)] public List<string> Units { get; set; } = new();
    [Ed("Повторы разрешены", "Иначе один шаблон — один боец")] public bool DuplicatesAllowed { get; set; }
}

/// <summary>A limit of one template or tag in a squad.</summary>
public sealed class SquadLimitDef : ContentPart
{
    [Ed("Шаблон, категория или тег", "ID шаблона, категория существ, «tag:x» или просто тег")] public string Match { get; set; } = "";
    [Ed("Не более"), EdRange(0, 100)] public int Max { get; set; } = 1;

    public override string ToString() => $"{Match} ≤ {Max}";
}

/// <summary>
/// The squad of a scenario (ТЗ ч. 6, 2.2–2.5): who may be taken and how many. Every limit is optional. «Размер отряда» is not
/// «одновременно на поле» — the second is the limit of the deployment (§96).
/// </summary>
public sealed class ScenarioSquadDef : ContentPart
{
    [Ed("Отряд игрока", "Выключено — как раньше: только юниты сценария и расстановка")] public bool Enabled { get; set; }
    [Ed("Доступные бойцы", "Шаблоны, из которых набирается отряд; пусто — все шаблоны фракций игрока"), EdRef(DefCategory.Unit),
     EdShowIf(nameof(Enabled), true)]
    public List<string> Pool { get; set; } = new();
    [Ed("Не меньше бойцов"), EdRange(0, 100), EdShowIf(nameof(Enabled), true)] public int MinSize { get; set; }
    [Ed("Не больше бойцов (размер отряда)", "0 — без ограничения"), EdRange(0, 100), EdShowIf(nameof(Enabled), true)] public int MaxSize { get; set; } = 6;
    [Ed("Повторы разрешены", "Два бойца одного шаблона"), EdShowIf(nameof(Enabled), true)] public bool DuplicatesAllowed { get; set; }
    [Ed("Разрешённые теги", "Если заданы — боец должен подходить хотя бы под один (шаблон, категория, «tag:x» или тег)"), EdShowIf(nameof(Enabled), true)]
    public List<string> AllowedTags { get; set; } = new();
    [Ed("Запрещённые теги", "Боец с любым из них недоступен"), EdShowIf(nameof(Enabled), true)] public List<string> ForbiddenTags { get; set; } = new();
    [Ed("Обязательные теги", "Каждый — хотя бы у одного бойца отряда"), EdShowIf(nameof(Enabled), true)] public List<string> RequiredTags { get; set; } = new();
    [Ed("Лимиты", "Не более N бойцов шаблона, категории или тега"), EdShowIf(nameof(Enabled), true)] public List<SquadLimitDef> Limits { get; set; } = new();
    [Ed("Обязательные бойцы", "Всегда в отряде, убрать нельзя"), EdRef(DefCategory.Unit), EdShowIf(nameof(Enabled), true)]
    public List<string> StartingRoster { get; set; } = new();
    [Ed("Стартовый состав на поле", "Для «Отряд на поле в начале: стартовый состав» — кто из отряда стоит на поле сразу"), EdRef(DefCategory.Unit),
     EdShowIf(nameof(Enabled), true)]
    public List<string> StartDeployed { get; set; } = new();
    [Ed("Фиксированный отряд", "Экран выбора не показывается: отряд — «Обязательные бойцы» (или пресет)"), EdShowIf(nameof(Enabled), true)]
    public bool Fixed { get; set; }
    [Ed("Пресет по умолчанию", "Набирается при открытии экрана; для автобоя и фиксированного отряда"), EdRef(DefCategory.SquadPreset), EdShowIf(nameof(Enabled), true)]
    public string DefaultPreset { get; set; } = "";
}
