using System.Collections.Generic;
using HexTactics.Core.Hex;

namespace HexTactics.Core.Content;

// Scenario objectives, boss phases and spawn warnings (TZ part 2 §4, §6; docs/DECISIONS_2.md §25, §37).

public enum ObjectiveKind
{
    [Ed("Зачистка всех врагов")] Eliminate,
    [Ed("Оборона гексов/объекта")] Defend,
    [Ed("Убийство босса")] KillBoss,
    [Ed("Выживание")] Survive,
    [Ed("Отражение волн")] Waves,
    [Ed("Сопровождение")] Escort,
    [Ed("Захват и удержание зоны")] Capture,
    [Ed("Уничтожение объектов")] Destroy,
    [Ed("Предотвратить событие")] Prevent,
    [Ed("Своё условие")] Custom,
}

public enum ObjectiveLogic
{
    [Ed("Все обязательные")] AllRequired,
    [Ed("Хотя бы одна обязательная")] AnyRequired,
    [Ed("Обязательные по порядку")] InOrder,
}

public sealed class ObjectiveDef : ContentPart
{
    [Ed("ID цели", "Уникален в сценарии; на него ссылаются условия и зависимости")] public string Id { get; set; } = "goal";
    [Ed("Название")] public string Name { get; set; } = "Цель";
    [Ed("Описание"), EdMultiline] public string Description { get; set; } = "";
    [Ed("Тип")] public ObjectiveKind Kind { get; set; } = ObjectiveKind.Eliminate;
    [Ed("Обязательная", "Необязательные не влияют на победу (бонусные)")] public bool Required { get; set; } = true;
    [Ed("Приоритет", "Порядок в списке целей"), EdRange(-100, 100)] public int Priority { get; set; }
    [Ed("Зависит от целей", "Активна только после выполнения указанных")] public List<string> DependsOn { get; set; } = new();
    [Ed("Таймер, с", "0 — без таймера; по истечении: провал (или выполнение для «выживания»/«предотвратить»)"), EdRange(0, 36000)]
    public double TimeLimit { get; set; }

    [Ed("Гексы обороны / зоны", "Клетки цели"), EdShowIf(nameof(Kind), ObjectiveKind.Defend, ObjectiveKind.Capture)] public List<HexCoord> Cells { get; set; } = new();
    [Ed("Метка карты", "Вместо/вдобавок к гексам: клетка и радиус метки"),
     EdShowIf(nameof(Kind), ObjectiveKind.Defend, ObjectiveKind.Capture, ObjectiveKind.Escort)]
    public string Marker { get; set; } = "";
    [Ed("Допустимо пропустить врагов"), EdRange(0, 10000), EdShowIf(nameof(Kind), ObjectiveKind.Defend)] public int AllowedLeaks { get; set; }
    [Ed("Учитывать категории", "Категории существ или tag:тег; пусто — всех"), EdShowIf(nameof(Kind), ObjectiveKind.Defend)] public List<string> CountCategories { get; set; } = new();
    [Ed("Убирать прорвавшихся", "Враг, вошедший в гекс обороны, исчезает"), EdShowIf(nameof(Kind), ObjectiveKind.Defend)] public bool RemoveLeaked { get; set; } = true;
    [Ed("Защищаемые юниты (метка)", "Оборона объекта: провал при гибели"), EdShowIf(nameof(Kind), ObjectiveKind.Defend)] public string ProtectTag { get; set; } = "";

    [Ed("Метка юнита", "Босс / сопровождаемый / объекты для уничтожения"),
     EdShowIf(nameof(Kind), ObjectiveKind.KillBoss, ObjectiveKind.Escort, ObjectiveKind.Destroy)]
    public string UnitTag { get; set; } = "";
    [Ed("Сколько уничтожить", "0 — все с меткой"), EdRange(0, 1000), EdShowIf(nameof(Kind), ObjectiveKind.Destroy)] public int Count { get; set; }
    [Ed("Время, с", "Выживание / удержание зоны"), EdRange(0, 36000), EdShowIf(nameof(Kind), ObjectiveKind.Survive, ObjectiveKind.Capture)]
    public double Seconds { get; set; } = 60;
    [Ed("Сбрасывать при потере зоны", "Иначе прогресс удержания сохраняется"), EdShowIf(nameof(Kind), ObjectiveKind.Capture)] public bool ResetOnLoss { get; set; }
    [Ed("Волн отразить", "0 — все волны сценария"), EdRange(0, 1000), EdShowIf(nameof(Kind), ObjectiveKind.Waves)] public int WaveCount { get; set; }

    [Ed("Условие выполнения", "Для «своё условие»; для остальных — дополнительное (И)")] public ConditionDef Complete { get; set; } = new() { Type = ConditionType.Always };
    [Ed("Условие провала", "Например, для «предотвратить событие»")] public ConditionDef Fail { get; set; } = new() { Type = ConditionType.Not, Children = { new ConditionDef { Type = ConditionType.Always } } };

    [Ed("Показывать на карте")] public bool ShowOnMap { get; set; } = true;
    [Ed("Скрыта до активации")] public bool HiddenUntilActive { get; set; }
}

public enum WarningMode
{
    [Ed("Растущая линия")] Growing,
    [Ed("Комета (отрезок)")] Comet,
}

public enum WarningFog
{
    [Ed("Показывать")] Show,
    [Ed("Скрыть полностью")] Hide,
    [Ed("После разведки точки появления")] AfterScouting,
    [Ed("Частично (по видимым клеткам)")] Partial,
}

/// <summary>Global spawn warning settings (rules).</summary>
public sealed class SpawnWarningRules : ContentPart
{
    [Ed("Маркеры включены")] public bool Enabled { get; set; } = true;
    [Ed("Мин. предупреждение, с", "Линия идёт не меньше этого времени (дольше — если путь длинный)"), EdRange(0, 600)] public double MinSeconds { get; set; } = 3;
    [Ed("Исчезает до появления, с"), EdRange(0, 30)] public double VanishBefore { get; set; } = 1;
    [Ed("Скорость линии", "Множитель к обычной скорости врага (1 — в такт его шагам)"), EdRange(0.1, 10)] public double SpeedFactor { get; set; } = 1;
    [Ed("Толщина, доля стороны гекса"), EdRange(0.05, 1)] public double Width { get; set; } = 1.0 / 3.0;
    [Ed("Вид")] public WarningMode Mode { get; set; } = WarningMode.Growing;
    [Ed("Длина кометы, клеток"), EdRange(1, 20), EdShowIf(nameof(Mode), WarningMode.Comet)] public int CometCells { get; set; } = 3;
    [Ed("Туман войны")] public WarningFog Fog { get; set; } = WarningFog.Show;
}

public sealed class ScenarioWarningsDef : ContentPart
{
    [Ed("Маркеры в этом сценарии")] public bool Enabled { get; set; } = true;
    [Ed("Свой режим тумана")] public bool OverrideFog { get; set; }
    [Ed("Туман войны"), EdShowIf(nameof(OverrideFog), true)] public WarningFog Fog { get; set; } = WarningFog.AfterScouting;
}

/// <summary>
/// A phase of a boss (ТЗ ч. 6, 8; docs/DECISIONS_6.md §102): the transition into it (from which phases, the condition, the
/// priority, once or again) and what it does — only actions of the scenario. Nothing is reset unless the phase says so.
/// </summary>
public sealed class BossPhaseDef : ContentPart
{
    [Ed("ID", "Пусто — номер фазы (1, 2…)"), JsonOmitEmpty] public string Id { get; set; } = "";
    [Ed("Название фазы")] public string Name { get; set; } = "Фаза";
    [Ed("Условие начала", "Например, «здоровье ниже 50 %» (без метки — сам босс); таймер, гибель союзника, объект в состоянии, юнит в области")]
    public ConditionDef When { get; set; } = new() { Type = ConditionType.HpBelow, Fraction = 0.5 };
    [Ed("Из фаз", "ID фаз, из которых возможен переход: «start» — до первой фазы, «*» — из любой; пусто — после предыдущей по списку"), JsonOmitEmpty]
    public List<string> From { get; set; } = new();
    [Ed("Приоритет", "Готовы несколько переходов в один тик — сначала больший; при равенстве — по списку"), EdRange(-100, 100), JsonOmitEmpty]
    public int Priority { get; set; }
    [Ed("Может повториться", "Иначе фаза наступает один раз за бой"), JsonOmitEmpty] public bool CanRepeat { get; set; }
    [Ed("Действия", "Смена навыков и автоатак, профиля ИИ, состояния (сопротивления фазы), волна, объект, призыв, местность…")]
    public List<TriggerActionDef> Actions { get; set; } = new();
    [Ed("Сообщение")] public string Message { get; set; } = "";
    // ТЗ 8.4: by default the transition keeps damage, statuses, cooldowns and AP — only these explicit rules change them.
    [Ed("Сбросить перезарядки", "Явное правило; по умолчанию перезарядки сохраняются"), JsonOmitEmpty] public bool ResetCooldowns { get; set; }
    [Ed("Восполнить ОД", "Явное правило; по умолчанию ОД сохраняются"), JsonOmitEmpty] public bool RefillAp { get; set; }
    [Ed("Лечение, доля здоровья", "Явное правило; 0 — здоровье не меняется"), EdRange(0, 1), JsonOmitEmpty] public double Heal { get; set; }
    [Ed("Снять состояния", "Явное правило; пусто — состояния остаются"), EdRef(DefCategory.Status), JsonOmitEmpty] public List<string> ClearStatuses { get; set; } = new();

    public override string ToString() => Id != "" ? $"{Name} [{Id}]" : Name;
}

public sealed class BossDef : ContentPart
{
    [Ed("Это босс", "Полоса здоровья вверху экрана, правило автоатак «босс»")] public bool IsBoss { get; set; }
    [Ed("Титул"), EdShowIf(nameof(IsBoss), true)] public string Title { get; set; } = "";
    [Ed("Фазы", "Переходы проверяются каждый тик: «из фаз», условие, приоритет; без «может повториться» — один раз"), EdShowIf(nameof(IsBoss), true)]
    public List<BossPhaseDef> Phases { get; set; } = new();
    [Ed("Разрешённые навыки фаз", "Навыки, которые фазы могут добавить или сделать автоатакой (ТЗ 8.3); другие — предупреждение проверки"),
     EdRef(DefCategory.Skill), EdShowIf(nameof(IsBoss), true), JsonOmitEmpty]
    public List<string> AllowedSkills { get; set; } = new();
}

/// <summary>
/// «Телеграф атаки» (ТЗ ч. 6, 8.5; §102): a setting of the scenario over an existing skill — the warning lasts at least N s, the
/// area, the outline, the timer. The hit, its area and the cooldown are still the skill's; an interrupted cast takes the
/// warning away and does nothing.
/// </summary>
public sealed class TelegraphRuleDef : ContentPart
{
    [Ed("Навык"), EdRef(DefCategory.Skill, AllowEmpty = false)] public string Skill { get; set; } = "";
    [Ed("Кто", "Метка, tag:x, категория или шаблон; пусто — любой исполнитель")] public string Caster { get; set; } = "";
    [Ed("Мин. предупреждение, с", "Подготовка навыка длится не меньше этого"), EdRange(0, 30)] public double MinLead { get; set; } = 1.5;
    [Ed("Показывать область")] public bool ShowArea { get; set; } = true;
    [Ed("Цвет контура")] public string Color { get; set; } = "#ff5040";
    [Ed("Таймер", "Секунды до удара над областью")] public bool ShowTimer { get; set; } = true;
    [Ed("Надпись", "Над областью, например «Удар хвостом!»")] public string Text { get; set; } = "";

    public override string ToString() => $"{Skill} ≥ {MinLead:0.##} с";
}
