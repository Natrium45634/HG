using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace HexTactics.Core.Content;

// Campaigns, rewards and criteria of the outcome (ТЗ ч. 6, 9–10; docs/DECISIONS_6.md §103). A campaign links scenarios that
// already exist (and their variants) — it copies none of them.

/// <summary>A number of the outcome of a battle, taken from its report (ТЗ 10.3: no second statistics).</summary>
public enum OutcomeMetric
{
    [Ed("Победа", "1 — победа, 0 — нет")] Victory,
    [Ed("Здоровье баз, доля", "Наименьшая доля здоровья баз игрока (0…1); баз нет — 1")] BaseHpRemaining,
    [Ed("Пропущено врагов", "Враги, дошедшие до цели")] Leaks,
    [Ed("Время боя, с")] TimeToWin,
    [Ed("Потери игрока", "Погибшие юниты игрока")] Deaths,
    [Ed("Вызвано бойцов", "Вызовы за очки развёртывания")] UnitsDeployed,
}

/// <summary>«Показатель не меньше / не больше значения».</summary>
public sealed class OutcomeCriterionDef : ContentPart
{
    [Ed("Показатель")] public OutcomeMetric Metric { get; set; } = OutcomeMetric.Victory;
    [Ed("Сравнение")] public CompareMode Compare { get; set; } = CompareMode.AtLeast;
    [Ed("Значение")] public double Value { get; set; } = 1;

    public bool Holds(IReadOnlyDictionary<OutcomeMetric, double> r) =>
        r.TryGetValue(Metric, out var v) && (Compare == CompareMode.AtLeast ? v >= Value - 1e-9 : v <= Value + 1e-9);

    public string Describe() => Metric == OutcomeMetric.Victory && Compare == CompareMode.AtLeast && Value >= 1 ? "победа"
        : $"{EdUtil.EnumLabel(Metric)} {(Compare == CompareMode.AtLeast ? "≥" : "≤")} {Value:0.##}";

    public override string ToString() => Describe();
}

public enum CampaignConditionType
{
    [Ed("Всегда")] Always,
    [Ed("И (все вложенные)")] All,
    [Ed("ИЛИ (хотя бы одно)")] Any,
    [Ed("НЕ")] Not,
    [Ed("Задание пройдено")] NodeCompleted,
    [Ed("Пройдено N заданий", "Из списка; пусто — любых")] CompletedCount,
    [Ed("Флаг кампании")] Flag,
    [Ed("Результат задания", "Лучший результат задания удовлетворяет порогу")] Result,
}

/// <summary>
/// The access to a mission (ТЗ 9.4): «пройдено задание N», «пройдено N из…», «есть флаг», «результат задания — не хуже порога»,
/// combined with И / ИЛИ / НЕ — the same structured builder as the conditions of scenarios, not a language of expressions.
/// </summary>
public sealed class CampaignConditionDef : ContentPart
{
    [Ed("Условие")] public CampaignConditionType Type { get; set; } = CampaignConditionType.NodeCompleted;
    [Ed("Вложенные"), EdShowIf(nameof(Type), CampaignConditionType.All, CampaignConditionType.Any, CampaignConditionType.Not)]
    public List<CampaignConditionDef> Children { get; set; } = new();
    [Ed("Задание (ID узла)"), EdShowIf(nameof(Type), CampaignConditionType.NodeCompleted, CampaignConditionType.Result)] public string Node { get; set; } = "";
    [Ed("Из заданий", "ID узлов; пусто — любые"), EdShowIf(nameof(Type), CampaignConditionType.CompletedCount)] public List<string> Nodes { get; set; } = new();
    [Ed("Сколько"), EdRange(1, 1000), EdShowIf(nameof(Type), CampaignConditionType.CompletedCount)] public int Count { get; set; } = 1;
    [Ed("Флаг"), EdShowIf(nameof(Type), CampaignConditionType.Flag)] public string Flag { get; set; } = "";
    [Ed("Порог"), EdShowIf(nameof(Type), CampaignConditionType.Result)] public OutcomeCriterionDef? Criterion { get; set; }

    public string Describe() => Type switch
    {
        CampaignConditionType.Always => "всегда",
        CampaignConditionType.All => "(" + string.Join(" и ", Children.Select(c => c.Describe())) + ")",
        CampaignConditionType.Any => "(" + string.Join(" или ", Children.Select(c => c.Describe())) + ")",
        CampaignConditionType.Not => "не " + (Children.FirstOrDefault()?.Describe() ?? "—"),
        CampaignConditionType.NodeCompleted => $"пройдено «{Node}»",
        CampaignConditionType.CompletedCount => $"пройдено {Count} из " + (Nodes.Count == 0 ? "любых" : "{" + string.Join(", ", Nodes) + "}"),
        CampaignConditionType.Flag => $"флаг «{Flag}»",
        CampaignConditionType.Result => $"«{Node}»: {Criterion?.Describe() ?? "—"}",
        _ => Type.ToString(),
    };

    public override string ToString() => Describe();
}

/// <summary>A mission of a campaign: a scenario that exists (and its variant), the access to it, where it leads, its rewards.</summary>
public sealed class CampaignNodeDef : ContentPart
{
    [Ed("ID")] public string Id { get; set; } = "";
    [Ed("Сценарий"), EdRef(DefCategory.Scenario, AllowEmpty = false)] public string Scenario { get; set; } = "";
    [Ed("Вариант", "Пусто — обычный"), EdRef(DefCategory.DifficultyVariant)] public string Variant { get; set; } = "";
    [Ed("Этап", "Подпись, например «Глава 1»")] public string StageLabel { get; set; } = "";
    [Ed("Описание", "Пусто — описание сценария")] public string Description { get; set; } = "";
    [Ed("Доступ", "Пусто — пройдено задание, ведущее сюда (стартовое — открыто)")] public CampaignConditionDef? Requires { get; set; }
    [Ed("Далее", "ID следующих заданий (ветвление и слияние)")] public List<string> Next { get; set; } = new();
    [Ed("Награды"), EdRef(DefCategory.RewardSet)] public string RewardSet { get; set; } = "";
    [Ed("Нужно открыть", "Задание видно, но закрыто, пока наградой не открыт его сценарий"), JsonOmitEmpty] public bool NeedsUnlock { get; set; }
    [Ed("X"), EdHidden, JsonOmitEmpty] public float X { get; set; }
    [Ed("Y"), EdHidden, JsonOmitEmpty] public float Y { get; set; }

    public override string ToString() => StageLabel != "" ? $"{StageLabel}: {Id}" : Id;
}

/// <summary>Rings of a campaign (ТЗ 9.5).</summary>
public enum CampaignCycles
{
    [Ed("Запрещены", "Повтор задания — переигрыванием, не кольцом")] Forbidden,
    [Ed("Разрешены с путём к окончанию")] WithExit,
    [Ed("Бесконечная кампания", "Кольцо без выхода — режим показан игроку")] Endless,
}

/// <summary>A campaign (ТЗ 9.1–9.2): the start, the missions, the links, the flags.</summary>
public sealed class CampaignDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.Campaign;

    [Ed("Вступление")] public string Intro { get; set; } = "";
    [Ed("Стартовые задания", "ID узлов, открытых с начала")] public List<string> StartNodes { get; set; } = new();
    [Ed("Задания")] public List<CampaignNodeDef> Nodes { get; set; } = new();
    [Ed("Флаги кампании", "Объявленные флаги (награды и условия доступа)")] public List<string> Flags { get; set; } = new();
    [Ed("Кольца")] public CampaignCycles Cycles { get; set; } = CampaignCycles.Forbidden;

    public CampaignNodeDef? Node(string id) => Nodes.Find(n => n.Id == id);
}

public enum RewardType
{
    [Ed("Открыть сценарий")] UnlockScenario,
    [Ed("Открыть шаблон юнита", "Становится доступен в отряде")] UnlockUnit,
    [Ed("Флаг кампании")] SetFlag,
    [Ed("Валюта кампании", "Только ресурс проекта")] Currency,
    [Ed("Медаль")] Medal,
}

/// <summary>How often a reward is given (ТЗ 10.4).</summary>
public enum RewardPolicy
{
    [Ed("Только первое прохождение")] FirstClearOnly,
    [Ed("Каждое прохождение")] Repeatable,
    [Ed("Раз на вариант сложности")] PerDifficulty,
    [Ed("Одна на выбор из группы")] ExclusiveChoice,
}

/// <summary>An entry of a set of rewards (ТЗ 10.2).</summary>
public sealed class RewardEntryDef : ContentPart
{
    [Ed("ID", "Ключ выдачи; не меняйте после выхода кампании")] public string Id { get; set; } = "";
    [Ed("Награда")] public RewardType Type { get; set; } = RewardType.Medal;
    [Ed("Сценарий"), EdRef(DefCategory.Scenario), EdShowIf(nameof(Type), RewardType.UnlockScenario)] public string Scenario { get; set; } = "";
    [Ed("Шаблон юнита"), EdRef(DefCategory.Unit), EdShowIf(nameof(Type), RewardType.UnlockUnit)] public string Unit { get; set; } = "";
    [Ed("Флаг"), EdShowIf(nameof(Type), RewardType.SetFlag)] public string Flag { get; set; } = "";
    [Ed("Ресурс"), EdRef(DefCategory.Resource), EdShowIf(nameof(Type), RewardType.Currency)] public string Currency { get; set; } = "";
    [Ed("Количество"), EdRange(0, 1000000), EdShowIf(nameof(Type), RewardType.Currency)] public double Amount { get; set; } = 1;
    [Ed("Медаль", "Название"), EdShowIf(nameof(Type), RewardType.Medal)] public string Medal { get; set; } = "";
    [Ed("Только если", "Порог результата боя; пусто — без условия")] public OutcomeCriterionDef? Criterion { get; set; }
    [Ed("И при поражении", "Иначе — только при победе")] public bool OnDefeat { get; set; }
    [Ed("Выдача")] public RewardPolicy Policy { get; set; } = RewardPolicy.FirstClearOnly;
    [Ed("Группа выбора"), EdShowIf(nameof(Policy), RewardPolicy.ExclusiveChoice)] public string ChoiceGroup { get; set; } = "";

    public string Describe() => Type switch
    {
        RewardType.UnlockScenario => $"открыт сценарий «{Scenario}»",
        RewardType.UnlockUnit => $"открыт юнит «{Unit}»",
        RewardType.SetFlag => $"флаг «{Flag}»",
        RewardType.Currency => $"+{Amount:0.##} {Currency}",
        _ => $"медаль «{Medal}»",
    };

    public override string ToString() => Describe();
}

/// <summary>A set of rewards of a mission (ТЗ 10.1): declarative entries — not the experience of units of part 4.</summary>
public sealed class RewardSetDef : Def
{
    [JsonIgnore] public override DefCategory Category => DefCategory.RewardSet;

    [Ed("Награды")] public List<RewardEntryDef> Entries { get; set; } = new();
}
