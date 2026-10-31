using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HexTactics.Core.Content;

/// <summary>What a modifier of a mission changes (ТЗ ч. 6, 5.3) — only things that already exist.</summary>
public enum MissionModOp
{
    [Ed("Характеристика юнитов", "Общий движок модификаторов: прибавка и множитель")] Stat,
    [Ed("Наложить состояние", "Уже описанное состояние — на юнитов при появлении")] Status,
    [Ed("Правило режима", "Блокирование, урон по союзникам")] Rule,
    [Ed("Развёртывание", "Очки, лимит на поле, начисление")] Deployment,
    [Ed("Отряд", "Размер, запрещённые и разрешённые теги")] Squad,
    [Ed("Волна", "Число врагов, дополнительная группа, задержка")] Wave,
    // Part 6, stage 6.10 (§104).
    [Ed("Шкала юнитов", "Сдвиг боевого духа, санити… при появлении")] Scale,
}

public enum ModRuleField
{
    [Ed("Блокирование")] Blocking,
    [Ed("Урон по союзникам в области")] FriendlyFire,
}

public enum ModDeployField
{
    [Ed("Стартовые очки")] StartingPoints,
    [Ed("Потолок очков")] MaxPoints,
    [Ed("На поле одновременно")] MaxDeployed,
    [Ed("Начисление: сколько")] GainAmount,
    [Ed("Начисление: раз в, с")] GainInterval,
}

public enum ModSquadField
{
    [Ed("Размер отряда (не больше)")] MaxSize,
    [Ed("Запретить теги")] ForbidTags,
    [Ed("Разрешить только теги")] AllowOnlyTags,
}

/// <summary>Two modifiers of one field: both act, or the later one replaces the earlier (ТЗ ч. 6, 5.2 «stackingPolicy»).</summary>
public enum ModStacking
{
    [Ed("Складываются")] Stack,
    [Ed("Последний заменяет")] Replace,
}

/// <summary>Which units a modifier of a unit is about (ТЗ ч. 6, 5.2 «selector»); empty — every unit.</summary>
public sealed class MissionSelectorDef : ContentPart
{
    [Ed("Сторона", "Пусто — любая"), EdRef(DefCategory.Faction)] public string Faction { get; set; } = "";
    [Ed("Юниты", "Шаблон, категория или tag:тег; пусто — любые")] public List<string> Units { get; set; } = new();
    [Ed("Группа волны", "ID группы; пусто — любая")] public string Group { get; set; } = "";
    [Ed("Волна", "Номер с 1; 0 — любая"), EdRange(0, 1000)] public int Wave { get; set; }
    [Ed("База спавна", "ID объекта; пусто — любая")] public string SpawnBase { get; set; } = "";

    public bool IsEmpty => Faction == "" && Units.Count == 0 && Group == "" && Wave == 0 && SpawnBase == "";

    public override string ToString()
    {
        var parts = new List<string>();
        if (Faction != "") parts.Add(Faction);
        if (Units.Count > 0) parts.Add(string.Join("/", Units));
        if (Group != "") parts.Add("группа " + Group);
        if (Wave > 0) parts.Add("волна " + Wave);
        if (SpawnBase != "") parts.Add("база " + SpawnBase);
        return parts.Count == 0 ? "все юниты" : string.Join(", ", parts);
    }
}

/// <summary>
/// One change of a mission (ТЗ ч. 6, 5.2; docs/DECISIONS_6.md §99). The changes of the rules, the deployment, the squad and the
/// waves are made to a copy of the scenario before the battle; those of units (a stat, a status) — through the engine of
/// modifiers and the statuses, from «С секунды» to «До секунды».
/// </summary>
public sealed class MissionModifierDef : ContentPart
{
    [Ed("ID", "Для конфликтов и ссылок")] public string Id { get; set; } = "";
    [Ed("Название")] public string Label { get; set; } = "";
    [Ed("Описание")] public string Description { get; set; } = "";
    [Ed("Что меняет")] public MissionModOp Op { get; set; } = MissionModOp.Stat;

    [Ed("Кого", "Юниты, на которых действует"), EdShowIf(nameof(Op), MissionModOp.Stat, MissionModOp.Status, MissionModOp.Scale)] public MissionSelectorDef Selector { get; set; } = new();
    [Ed("Характеристика"), EdShowIf(nameof(Op), MissionModOp.Stat)] public StatModDef Stat { get; set; } = new();
    [Ed("Состояние"), EdRef(DefCategory.Status), EdShowIf(nameof(Op), MissionModOp.Status)] public string Status { get; set; } = "";
    [Ed("Очки состояния"), EdRange(0, 1000), EdShowIf(nameof(Op), MissionModOp.Status)] public double StatusPoints { get; set; } = 1;
    [Ed("С секунды боя", "0 — с начала"), EdRange(0, 36000), EdShowIf(nameof(Op), MissionModOp.Stat, MissionModOp.Status, MissionModOp.Scale)] public double ActiveFrom { get; set; }
    [Ed("До секунды боя", "0 — до конца"), EdRange(0, 36000), EdShowIf(nameof(Op), MissionModOp.Stat, MissionModOp.Status)] public double ActiveUntil { get; set; }

    [Ed("Правило"), EdShowIf(nameof(Op), MissionModOp.Rule)] public ModRuleField Rule { get; set; }
    [Ed("Включено"), EdShowIf(nameof(Op), MissionModOp.Rule)] public bool RuleOn { get; set; } = true;

    [Ed("Поле развёртывания"), EdShowIf(nameof(Op), MissionModOp.Deployment)] public ModDeployField DeployField { get; set; }
    [Ed("Поле отряда"), EdShowIf(nameof(Op), MissionModOp.Squad)] public ModSquadField SquadField { get; set; }
    [Ed("Изменение", "Прибавка (−2 — на два меньше)"), EdShowIf(nameof(Op), MissionModOp.Deployment, MissionModOp.Squad, MissionModOp.Scale)] public double Delta { get; set; }
    [Ed("Теги", "Для «Запретить / Разрешить только»"), EdShowIf(nameof(Op), MissionModOp.Squad)] public List<string> Tags { get; set; } = new();

    [Ed("Номер волны", "С 1"), EdRange(1, 1000), EdShowIf(nameof(Op), MissionModOp.Wave)] public int WaveIndex { get; set; } = 1;
    [Ed("Число врагов ×", "1 — как есть"), EdRange(0, 20), EdShowIf(nameof(Op), MissionModOp.Wave)] public double CountMul { get; set; } = 1;
    [Ed("Число врагов +"), EdRange(-100, 100), EdShowIf(nameof(Op), MissionModOp.Wave)] public int CountAdd { get; set; }
    [Ed("Доп. группа: юнит", "Пусто — без дополнительной группы"), EdRef(DefCategory.Unit), EdShowIf(nameof(Op), MissionModOp.Wave)] public string ExtraUnit { get; set; } = "";
    [Ed("Доп. группа: сколько"), EdRange(1, 100), EdShowIf(nameof(Op), MissionModOp.Wave)] public int ExtraCount { get; set; } = 1;
    [Ed("Задержка волны +, с"), EdRange(-600, 600), EdShowIf(nameof(Op), MissionModOp.Wave)] public double DelayAdd { get; set; }

    [Ed("Наложение", "Если другой модификатор меняет то же поле")] public ModStacking Stacking { get; set; } = ModStacking.Stack;
    [Ed("Свои теги", "По ним другие наборы объявляют несовместимость")] public List<string> OwnTags { get; set; } = new();
    [Ed("Несовместим с тегами", "Набор или модификатор с таким тегом или ID — конфликт до запуска")] public List<string> IncompatibleTags { get; set; } = new();
    [Ed("Показывать в бою", "Значок варианта в бою перечисляет его")] public bool ShowInBattle { get; set; } = true;
    [Ed("Заметки редактора")] public string EditorNotes { get; set; } = "";

    public string Title => Label != "" ? Label : Id != "" ? Id : Describe();

    /// <summary>A line about the change.</summary>
    public string Describe() => Op switch
    {
        MissionModOp.Stat => $"{Selector}: {Stat.Stat}" + (Stat.Add != 0 ? $" {Stat.Add:+0.##;-0.##}" : "") + (Stat.Mul != 0 ? $" {Stat.Mul:+0%;-0%}" : "") + Window(),
        MissionModOp.Status => $"{Selector}: состояние «{Status}» ({StatusPoints:0.#})" + Window(),
        MissionModOp.Rule => $"{(Rule == ModRuleField.Blocking ? "Блокирование" : "Урон по союзникам")}: {(RuleOn ? "вкл." : "выкл.")}",
        MissionModOp.Deployment => $"Развёртывание, {DeployField}: {Delta:+0.##;-0.##}",
        MissionModOp.Squad => SquadField == ModSquadField.MaxSize ? $"Отряд не больше: {Delta:+0;-0}" : $"Отряд, {(SquadField == ModSquadField.ForbidTags ? "запрет" : "только")}: {string.Join(", ", Tags)}",
        MissionModOp.Wave => $"Волна {WaveIndex}:" + (CountMul != 1 ? $" ×{CountMul:0.##}" : "") + (CountAdd != 0 ? $" {CountAdd:+0;-0}" : "")
                             + (ExtraUnit != "" ? $" + группа {ExtraUnit}×{ExtraCount}" : "") + (DelayAdd != 0 ? $" задержка {DelayAdd:+0.#;-0.#} с" : ""),
        _ => Op.ToString(),
    };

    private string Window() => ActiveFrom > 0 || ActiveUntil > 0 ? $" ({ActiveFrom:0.#}–{(ActiveUntil > 0 ? ActiveUntil.ToString("0.#") : "конец")} с)" : "";
}

/// <summary>A reusable, versioned set of modifiers of a mission (ТЗ ч. 6, 5.1).</summary>
public sealed class ModifierSetDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.ModifierSet;

    [Ed("Модификаторы", "По порядку: при «Последний заменяет» действует последний")] public List<MissionModifierDef> Modifiers { get; set; } = new();
    [Ed("Несовместим с", "ID или теги других наборов")] public List<string> IncompatibleWith { get; set; } = new();
}

/// <summary>
/// A variant of a scenario (ТЗ ч. 6, 5.6): the same map, objects and spawners with sets of modifiers on top — «обычная»,
/// «сложная», «осада». The scenario itself is never changed.
/// </summary>
public sealed class DifficultyVariantDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.DifficultyVariant;

    [Ed("Сценарий"), EdRef(DefCategory.Scenario, AllowEmpty = false)] public string Scenario { get; set; } = "";
    [Ed("Наборы модификаторов", "По порядку, после своих наборов сценария"), EdRef(DefCategory.ModifierSet)] public List<string> ModifierSets { get; set; } = new();
    [Ed("Открывается после", "ID сценария, который надо пройти (кампании — этап 6.8); пусто — открыт")] public string UnlockedBy { get; set; } = "";
    [Ed("Рекомендуемая сила отряда", "0 — не указана"), EdRange(0, 100)] public double RecommendedPower { get; set; }
    [Ed("Ожидаемая угроза врагов", "0 — считается по волнам"), EdRange(0, 100000)] public double ExpectedThreat { get; set; }
}
