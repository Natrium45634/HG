using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace HexTactics.Core.Content;

/// <summary>The kinds of nodes of a graph of events (ТЗ ч. 6, 7.2). Every one compiles into what the triggers already do.</summary>
public enum RuleNodeKind
{
    // Sources of the flow.
    [Ed("Событие", "Источник: смерть юнита, начало волны, смена состояния объекта…")] Event,
    [Ed("Таймер", "Источник: через N с боя (повторяемый — каждые N с)")] Timer,
    [Ed("Сигнал (приём)", "Источник: сигнал с этим именем от узла «Послать сигнал»")] Signal,
    // Logic (Bool).
    [Ed("Условие", "Значение: любое условие сценария")] Condition,
    [Ed("И")] And,
    [Ed("ИЛИ")] Or,
    [Ed("НЕ")] Not,
    // Control of the flow.
    [Ed("Ветвление", "Условие истинно — выход «да», иначе «нет»")] Branch,
    [Ed("Последовательность", "Выходы по порядку связей")] Sequence,
    [Ed("Задержка", "Через N с")] Delay,
    [Ed("Однократно")] Once,
    [Ed("Не чаще", "Не чаще раза в N с")] Cooldown,
    [Ed("Ограниченный повтор", "N раз с интервалом")] LimitedRepeat,
    [Ed("Ворота", "Пропускают поток, пока открыты")] Gate,
    [Ed("Открыть ворота")] GateOpen,
    [Ed("Закрыть ворота")] GateClose,
    // Effects.
    [Ed("Действие", "Любое действие сценария: волна, состояние объекта, сообщение, ресурс, цель, конец…")] Action,
    [Ed("Послать сигнал", "Запускает узлы «Сигнал (приём)» с тем же именем")] EmitSignal,
}

/// <summary>The type of a port (ТЗ ч. 6, 7.3): a flow goes to a flow, a value of truth to a value of truth.</summary>
public enum RulePortType
{
    Flow,
    Bool,
}

/// <summary>A node of a graph: its kind, its place on the canvas and the parameters of its kind.</summary>
public sealed class RuleNodeDef : ContentPart
{
    [Ed("ID")] public string Id { get; set; } = "";
    [Ed("Вид")] public RuleNodeKind Kind { get; set; } = RuleNodeKind.Action;
    [Ed("Подпись", "Пусто — по виду и параметрам")] public string Label { get; set; } = "";
    [Ed("X"), EdHidden] public float X { get; set; }
    [Ed("Y"), EdHidden] public float Y { get; set; }
    /// <summary>The version of the scheme of the node's ports (ТЗ 7.3).</summary>
    [Ed("Версия узла"), EdHidden] public int NodeVersion { get; set; } = 1;

    [Ed("Событие"), EdShowIf(nameof(Kind), RuleNodeKind.Event)] public ScenarioEventType Event { get; set; } = ScenarioEventType.UnitDied;
    [Ed("Фильтр: объект или ID"), EdShowIf(nameof(Kind), RuleNodeKind.Event)] public string EventObject { get; set; } = "";
    [Ed("Фильтр: юнит", "Метка, tag:x, категория или шаблон"), EdShowIf(nameof(Kind), RuleNodeKind.Event)] public string EventUnit { get; set; } = "";
    [Ed("Фильтр: волна", "С 1; 0 — любая"), EdRange(0, 1000), EdShowIf(nameof(Kind), RuleNodeKind.Event)] public int EventWave { get; set; }
    [Ed("Секунды", "Таймер, задержка, «не чаще», интервал повтора"), EdRange(0, 36000),
     EdShowIf(nameof(Kind), RuleNodeKind.Timer, RuleNodeKind.Delay, RuleNodeKind.Cooldown, RuleNodeKind.LimitedRepeat)]
    public double Seconds { get; set; } = 1;
    [Ed("Повторяемый"), EdShowIf(nameof(Kind), RuleNodeKind.Timer)] public bool Repeat { get; set; }
    [Ed("Сколько раз"), EdRange(1, 1000), EdShowIf(nameof(Kind), RuleNodeKind.LimitedRepeat)] public int Count { get; set; } = 3;
    [Ed("Имя", "Сигнала или ворот"), EdShowIf(nameof(Kind), RuleNodeKind.Signal, RuleNodeKind.EmitSignal, RuleNodeKind.Gate, RuleNodeKind.GateOpen, RuleNodeKind.GateClose)]
    public string Name { get; set; } = "";
    [Ed("Открыты в начале"), EdShowIf(nameof(Kind), RuleNodeKind.Gate)] public bool Open { get; set; } = true;
    [Ed("Условие", "У события — только для «Выполнение условия»"), EdShowIf(nameof(Kind), RuleNodeKind.Condition, RuleNodeKind.Event)] public ConditionDef? Condition { get; set; }
    [Ed("Действие"), EdShowIf(nameof(Kind), RuleNodeKind.Action)] public TriggerActionDef? Action { get; set; }

    public override string ToString() => $"{Id}: {Kind}";
}

/// <summary>A link from an output port of a node to an input port of another.</summary>
public sealed class RuleLinkDef : ContentPart
{
    [Ed("Из узла")] public string From { get; set; } = "";
    [Ed("Выход")] public string FromPort { get; set; } = "out";
    [Ed("В узел")] public string To { get; set; } = "";
    [Ed("Вход")] public string ToPort { get; set; } = "in";

    public override string ToString() => $"{From}.{FromPort} → {To}.{ToPort}";
}

/// <summary>
/// A graph of events of a scenario (ТЗ ч. 6, 7; docs/DECISIONS_6.md §101): a visual shell over the triggers. It is stored
/// with the scenario and compiled into ordinary triggers when a battle starts; there is no second machine of the story.
/// </summary>
public sealed class RuleGraphDef : ContentPart
{
    [Ed("ID")] public string Id { get; set; } = "";
    [Ed("Название")] public string Name { get; set; } = "Граф событий";
    [Ed("Включён")] public bool Enabled { get; set; } = true;
    [Ed("Узлы"), EdHidden] public List<RuleNodeDef> Nodes { get; set; } = new();
    [Ed("Связи"), EdHidden] public List<RuleLinkDef> Links { get; set; } = new();

    [JsonIgnore] public string DisplayName => Name != "" ? Name : Id;
    public RuleNodeDef? Node(string id) => Nodes.Find(n => n.Id == id);
}
