using System;
using System.Collections.Generic;
using System.Linq;

namespace HexTactics.Core.Content;

/// <summary>A problem of a graph of events: an error stops the graph (the battle runs without it), a warning does not.</summary>
public sealed record GraphProblem(bool Error, string Node, string Message);

/// <summary>
/// Graphs of events (ТЗ ч. 6, 7; docs/DECISIONS_6.md §101): the scheme of the ports of every kind of node, the check of a graph
/// and its compilation into ordinary triggers of the scenario. A source becomes a trigger on its event; a node of control —
/// a trigger started by another one («только запуском»): «ветвление» — «если — то, иначе», «задержка» — «через N с»,
/// «однократно», «не чаще», «ограниченный повтор» — the limits of the trigger itself, «ворота» — a flag; the logic becomes
/// one condition (И/ИЛИ/НЕ). The battle runs the triggers as always: there is no second machine of the story.
/// </summary>
public static partial class RuleGraphs
{
    public sealed record Port(string Name, RulePortType Type, bool Many, string Caption);

    private static readonly Port FlowIn = new("in", RulePortType.Flow, true, "вход");
    private static readonly Port FlowOut = new("out", RulePortType.Flow, true, "далее");

    /// <summary>The inputs of a kind of node.</summary>
    public static IReadOnlyList<Port> Inputs(RuleNodeKind k) => k switch
    {
        RuleNodeKind.Event or RuleNodeKind.Timer or RuleNodeKind.Signal or RuleNodeKind.Condition => Array.Empty<Port>(),
        RuleNodeKind.And or RuleNodeKind.Or => new[] { new Port("in", RulePortType.Bool, true, "условия") },
        RuleNodeKind.Not => new[] { new Port("in", RulePortType.Bool, false, "условие") },
        RuleNodeKind.Branch => new[] { FlowIn, new Port("if", RulePortType.Bool, false, "если") },
        _ => new[] { FlowIn },
    };

    /// <summary>The outputs of a kind of node.</summary>
    public static IReadOnlyList<Port> Outputs(RuleNodeKind k) => k switch
    {
        RuleNodeKind.Condition or RuleNodeKind.And or RuleNodeKind.Or or RuleNodeKind.Not => new[] { new Port("value", RulePortType.Bool, true, "значение") },
        RuleNodeKind.Branch => new[] { new Port("yes", RulePortType.Flow, true, "да"), new Port("no", RulePortType.Flow, true, "нет") },
        _ => new[] { FlowOut },
    };

    public static bool IsSource(RuleNodeKind k) => k is RuleNodeKind.Event or RuleNodeKind.Timer or RuleNodeKind.Signal;
    public static bool IsLogic(RuleNodeKind k) => k is RuleNodeKind.Condition or RuleNodeKind.And or RuleNodeKind.Or or RuleNodeKind.Not;

    /// <summary>
    /// What the editor offers but the runtime has no event or command for (ТЗ 7.2: marked as unavailable, never faked).
    /// The way to the same result is told.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Why)> Unavailable = new[]
    {
        ("Здоровье базы изменилось", "отдельного события нет: условие «Здоровье ниже» с событием «Выполнение условия»"),
        ("Применить модификатор миссии", "модификаторы применяются до боя (варианты сценария); в бою — действие «Наложить состояние» или операции"),
        ("Изменить состояние цели", "цели меняются своими условиями; действие «Завершить сценарий» — для исхода"),
    };

    /// <summary>The text of a node: its label or its kind and parameters.</summary>
    public static string Title(RuleNodeDef n) => n.Label != "" ? n.Label : n.Kind switch
    {
        RuleNodeKind.Event => "Событие: " + EdUtil.EnumLabel(n.Event) + (n.EventObject != "" ? $" «{n.EventObject}»" : "") + (n.EventUnit != "" ? $" [{n.EventUnit}]" : ""),
        RuleNodeKind.Timer => $"Таймер {n.Seconds:0.##} с" + (n.Repeat ? " (повтор)" : ""),
        RuleNodeKind.Signal => $"Сигнал «{n.Name}»",
        RuleNodeKind.Condition => n.Condition?.Describe() ?? "Условие (не задано)",
        RuleNodeKind.Delay => $"Задержка {n.Seconds:0.##} с",
        RuleNodeKind.Cooldown => $"Не чаще {n.Seconds:0.##} с",
        RuleNodeKind.LimitedRepeat => $"Повтор ×{n.Count}, через {n.Seconds:0.##} с",
        RuleNodeKind.Gate => $"Ворота «{n.Name}»",
        RuleNodeKind.GateOpen => $"Открыть ворота «{n.Name}»",
        RuleNodeKind.GateClose => $"Закрыть ворота «{n.Name}»",
        RuleNodeKind.Action => n.Action?.Describe() ?? "Действие (не задано)",
        RuleNodeKind.EmitSignal => $"Послать сигнал «{n.Name}»",
        _ => EdUtil.EnumLabel(n.Kind),
    };

    /// <summary>Why a link may not be made (an unknown node or port, types that do not match, a single input taken); null — it may.</summary>
    public static string? CanLink(RuleGraphDef g, RuleLinkDef l, bool adding = false)
    {
        var a = g.Node(l.From);
        var b = g.Node(l.To);
        if (a == null || b == null) return "узла нет";
        if (a == b) return "узел не соединяется сам с собой";
        var outp = Outputs(a.Kind).FirstOrDefault(p => p.Name == l.FromPort);
        var inp = Inputs(b.Kind).FirstOrDefault(p => p.Name == l.ToPort);
        if (outp == null) return $"у «{Title(a)}» нет выхода «{l.FromPort}»";
        if (inp == null) return $"у «{Title(b)}» нет входа «{l.ToPort}»";
        if (outp.Type != inp.Type)
            return $"типы не совпадают: {(outp.Type == RulePortType.Flow ? "поток" : "условие")} → {(inp.Type == RulePortType.Flow ? "поток" : "условие")}";
        if (!inp.Many && g.Links.Count(x => x.To == l.To && x.ToPort == l.ToPort && x != l) >= 1) return $"вход «{inp.Caption}» уже занят";
        if (adding && g.Links.Any(x => x.From == l.From && x.FromPort == l.FromPort && x.To == l.To && x.ToPort == l.ToPort)) return "такая связь уже есть";
        return null;
    }

    private static IEnumerable<RuleLinkDef> From(RuleGraphDef g, string node, string port) => g.Links.Where(l => l.From == node && l.FromPort == port);
    private static IEnumerable<RuleLinkDef> Into(RuleGraphDef g, string node, string port) => g.Links.Where(l => l.To == node && l.ToPort == port);

    /// <summary>
    /// The check of a graph (ТЗ 7.3, 7.5, T22): IDs, links and their types, the parameters of the nodes, signals without a
    /// receiver, nodes nothing reaches; a ring of the flow without «задержка» or «ограниченный повтор» is an error (it would run
    /// in one tick for ever), a ring through a plain delay with no limit — a warning.
    /// </summary>
    public static List<GraphProblem> Check(ScenarioDef sc, RuleGraphDef g)
    {
        var list = new List<GraphProblem>();
        foreach (var dup in g.Nodes.GroupBy(n => n.Id).Where(x => x.Key == "" || x.Count() > 1))
            list.Add(new(true, dup.Key, dup.Key == "" ? "узел без ID" : $"ID узла «{dup.Key}» повторяется"));
        foreach (var n in g.Nodes.Where(n => BadId(n.Id)))
            list.Add(new(true, n.Id, $"ID узла «{n.Id}»: нельзя «:» и «/»"));
        foreach (var ring in LogicRings(g))
            list.Add(new(true, ring[0], "кольцо условий: " + string.Join(" → ", ring.Append(ring[0]).Select(id => $"«{Title(g.Node(id)!)}»")) + " — значение не вычислить"));
        foreach (var l in g.Links)
            if (CanLink(g, l) is { } why) list.Add(new(true, l.From, $"связь {l}: {why}"));
        foreach (var n in g.Nodes)
        {
            string t = Title(n);
            switch (n.Kind)
            {
                case RuleNodeKind.Event when n.Event is ScenarioEventType.None or ScenarioEventType.Timer:
                    list.Add(new(true, n.Id, $"«{t}»: для времени — узел «Таймер»"));
                    break;
                case RuleNodeKind.Event when n.Event == ScenarioEventType.ConditionMet && n.Condition == null:
                    list.Add(new(true, n.Id, $"«{t}»: у события «Выполнение условия» нет условия"));
                    break;
                case RuleNodeKind.Condition when n.Condition == null:
                    list.Add(new(true, n.Id, "условие не задано"));
                    break;
                case RuleNodeKind.Action when n.Action == null:
                    list.Add(new(true, n.Id, "действие не задано"));
                    break;
                case RuleNodeKind.Branch when !Into(g, n.Id, "if").Any():
                    list.Add(new(true, n.Id, "у ветвления нет условия (вход «если»)"));
                    break;
                case RuleNodeKind.Not when !Into(g, n.Id, "in").Any():
                case RuleNodeKind.And or RuleNodeKind.Or when !Into(g, n.Id, "in").Any():
                    list.Add(new(true, n.Id, $"«{t}»: нет входящих условий"));
                    break;
                case RuleNodeKind.Signal or RuleNodeKind.EmitSignal or RuleNodeKind.Gate or RuleNodeKind.GateOpen or RuleNodeKind.GateClose when n.Name == "":
                    list.Add(new(true, n.Id, $"«{EdUtil.EnumLabel(n.Kind)}»: не задано имя"));
                    break;
                case RuleNodeKind.LimitedRepeat when n.Count < 1:
                    list.Add(new(true, n.Id, "повтор: меньше одного раза"));
                    break;
            }
            if (n.Kind == RuleNodeKind.EmitSignal && n.Name != "" && !sc.Graphs.SelectMany(x => x.Nodes).Any(x => x.Kind == RuleNodeKind.Signal && x.Name == n.Name))
                list.Add(new(false, n.Id, $"сигнал «{n.Name}» никто не принимает"));
            if (n.Kind is RuleNodeKind.GateOpen or RuleNodeKind.GateClose && n.Name != "" && !g.Nodes.Any(x => x.Kind == RuleNodeKind.Gate && x.Name == n.Name))
                list.Add(new(false, n.Id, $"ворот «{n.Name}» в графе нет"));
            if (n.Kind == RuleNodeKind.Action && n.Action is { } a)
                foreach (var p in ScenarioObjectsCheck.Interactive(new ContentDatabase(), new ScenarioDef { Objects = sc.Objects, Triggers = { new TriggerDef { Actions = { a } } } })
                             .Where(p => p.Severity == IssueSeverity.Error && p.Path == "Triggers"))
                    list.Add(new(true, n.Id, p.Message));
            if (n.Kind == RuleNodeKind.Action && n.Action is { Type: TriggerActionType.SpawnWave } w && (w.Wave < 0 || w.Wave >= sc.Waves.Count))
                list.Add(new(true, n.Id, $"волны {w.Wave + 1} в сценарии нет"));
        }
        // Nodes no source reaches (logic is reached through the node it feeds).
        var reached = new HashSet<string>();
        var stack = new Stack<string>(g.Nodes.Where(n => IsSource(n.Kind)).Select(n => n.Id));
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!reached.Add(id)) continue;
            foreach (var l in g.Links.Where(l => l.From == id)) stack.Push(l.To);
        }
        foreach (var n in g.Nodes.Where(n => !reached.Contains(n.Id) && !IsLogic(n.Kind)))
            list.Add(new(false, n.Id, $"«{Title(n)}»: до узла не доходит ни одно событие"));
        foreach (var n in g.Nodes.Where(n => IsLogic(n.Kind) && !g.Links.Any(l => l.From == n.Id)))
            list.Add(new(false, n.Id, $"«{Title(n)}»: значение никуда не идёт"));
        // Rings of the flow.
        foreach (var ring in FlowRings(g))
        {
            var kinds = ring.Select(id => g.Node(id)!.Kind).ToList();
            string text = string.Join(" → ", ring.Append(ring[0]).Select(id => $"«{Title(g.Node(id)!)}»"));
            if (!kinds.Any(k => k is RuleNodeKind.Delay or RuleNodeKind.LimitedRepeat))
                list.Add(new(true, ring[0], "мгновенный бесконечный цикл: " + text + " — нужен узел «Задержка» или «Ограниченный повтор»"));
            else if (!kinds.Any(k => k is RuleNodeKind.LimitedRepeat or RuleNodeKind.Once or RuleNodeKind.Gate or RuleNodeKind.Branch))
                list.Add(new(false, ring[0], "бесконечный цикл с задержкой: " + text + " — без предела и условия выхода"));
        }
        // A chain of nodes of control deeper than the triggers allow.
        foreach (var src in g.Nodes.Where(n => IsSource(n.Kind)))
            if (ControlDepth(g, src.Id, new HashSet<string>()) > 7)
                list.Add(new(false, src.Id, "цепочка из более 7 узлов управления подряд: в бою она оборвётся (предел вложенности триггеров 8)"));
        return list;
    }

    private static int ControlDepth(RuleGraphDef g, string id, HashSet<string> path)
    {
        if (!path.Add(id)) return 0;
        var n = g.Node(id);
        int self = n != null && n.Kind is RuleNodeKind.Branch or RuleNodeKind.Once or RuleNodeKind.Cooldown or RuleNodeKind.Gate or RuleNodeKind.LimitedRepeat ? 1 : 0;
        if (n?.Kind is RuleNodeKind.Delay) self = 0; // a delay starts a new chain in a later tick
        int best = 0;
        if (n?.Kind != RuleNodeKind.Delay)
            foreach (var l in g.Links.Where(l => l.From == id && g.Node(l.To) is { } t && !IsLogic(t.Kind)))
                best = Math.Max(best, ControlDepth(g, l.To, path));
        path.Remove(id);
        return self + best;
    }

    /// <summary>The simple rings of the flow (each once, from its smallest ID).</summary>
    public static List<List<string>> FlowRings(RuleGraphDef g) => Rings(g, false);

    /// <summary>The rings of the logic: И → ИЛИ → И… (the value would be computed for ever).</summary>
    public static List<List<string>> LogicRings(RuleGraphDef g) => Rings(g, true);

    private static List<List<string>> Rings(RuleGraphDef g, bool logic)
    {
        var edges = g.Nodes.GroupBy(n => n.Id).ToDictionary(x => x.Key, _ => new List<string>());
        foreach (var l in g.Links)
            if (g.Node(l.From) is { } a && g.Node(l.To) is { } b && IsLogic(a.Kind) == logic && IsLogic(b.Kind) == logic && edges.ContainsKey(a.Id))
                edges[a.Id].Add(b.Id);
        var rings = new List<List<string>>();
        var seen = new HashSet<string>();
        foreach (var start in edges.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var path = new List<string>();
            void Walk(string id)
            {
                if (id == start && path.Count > 0)
                {
                    string key = string.Join(">", path.OrderBy(x => x, StringComparer.Ordinal));
                    if (seen.Add(key)) rings.Add(path.ToList());
                    return;
                }
                if (path.Contains(id) || string.CompareOrdinal(id, start) < 0 || path.Count > 64) return;
                path.Add(id);
                foreach (var next in edges[id]) Walk(next);
                path.RemoveAt(path.Count - 1);
            }
            Walk(start);
        }
        return rings;
    }

    // ------------------------------------------------------------------ compiling

    public static string TriggerName(RuleGraphDef g, string node, string suffix = "") => $"⚙ {g.Id}:{node}{suffix}";
    private static string GateFlag(RuleGraphDef g, string name) => $"ворота:{g.Id}:{name}";

    /// <summary>The scenario with its graphs compiled into triggers (a copy; the graphs with errors are left out). The same scenario without graphs.</summary>
    public static ScenarioDef WithCompiled(ScenarioDef sc)
    {
        if (sc.Graphs.Count == 0 || sc.Graphs.All(g => !g.Enabled)) return sc;
        var copy = ContentJson.Clone(sc);
        copy.Triggers.AddRange(Compile(copy));
        return copy;
    }

    /// <summary>The triggers of every enabled graph of the scenario without errors, in a fixed order.</summary>
    public static List<TriggerDef> Compile(ScenarioDef sc)
    {
        var result = new List<TriggerDef>();
        var good = sc.Graphs.Where(g => g.Enabled && !Check(sc, g).Any(p => p.Error)).ToList();
        foreach (var g in good) result.AddRange(new Compiler(sc, g, good).Run());
        return result;
    }

    private sealed class Compiler
    {
        private readonly ScenarioDef _sc;
        private readonly RuleGraphDef _g;
        private readonly List<RuleGraphDef> _all;
        private readonly List<TriggerDef> _out = new();
        private readonly HashSet<string> _made = new();

        public Compiler(ScenarioDef sc, RuleGraphDef g, List<RuleGraphDef> all)
        {
            _sc = sc;
            _g = g;
            _all = all;
        }

        private string Mark(string node) => $"{_g.Id}/{node}";

        public List<TriggerDef> Run()
        {
            // Gates open at the start.
            var open = _g.Nodes.Where(n => n.Kind == RuleNodeKind.Gate && n.Open).Select(n => n.Name).Distinct().ToList();
            if (open.Count > 0)
                _out.Add(new TriggerDef
                {
                    Name = TriggerName(_g, "ворота"), Event = ScenarioEventType.ScenarioStart, Condition = new ConditionDef { Type = ConditionType.Always }, Once = true,
                    Actions = open.Select(n => new TriggerActionDef { Type = TriggerActionType.SetFlag, Flag = GateFlag(_g, n) }).ToList(), GraphNode = _g.Id + "/",
                });
            foreach (var n in _g.Nodes.Where(n => IsSource(n.Kind)).OrderBy(n => n.Id, StringComparer.Ordinal))
            {
                var t = new TriggerDef { Name = TriggerName(_g, n.Id), Condition = new ConditionDef { Type = ConditionType.Always }, Once = false, GraphNode = Mark(n.Id) };
                switch (n.Kind)
                {
                    case RuleNodeKind.Event:
                        t.Event = n.Event;
                        t.EventObject = n.EventObject;
                        t.EventUnit = n.EventUnit;
                        t.EventWave = n.EventWave;
                        if (n.Event == ScenarioEventType.ConditionMet && n.Condition != null) t.Condition = ContentJson.Clone(n.Condition);
                        break;
                    case RuleNodeKind.Timer:
                        t.Event = ScenarioEventType.Timer;
                        t.Interval = Math.Max(0.1, n.Seconds);
                        t.Once = !n.Repeat;
                        break;
                    case RuleNodeKind.Signal:
                        t.ActivationOnly = true;
                        break;
                }
                t.Actions = Flow(n.Id, "out");
                _out.Add(t);
            }
            return _out;
        }

        /// <summary>The actions run when the flow leaves a port: the targets in the order of the links.</summary>
        private List<TriggerActionDef> Flow(string node, string port)
        {
            var list = new List<TriggerActionDef>();
            foreach (var l in From(_g, node, port))
                if (_g.Node(l.To) is { } t && !IsLogic(t.Kind))
                    list.AddRange(Enter(t));
            return list;
        }

        private TriggerActionDef Activate(string name) => new() { Type = TriggerActionType.ActivateTrigger, Text = name };

        /// <summary>A node of control as a trigger of its own (started only by another), made once.</summary>
        private string Own(RuleNodeDef n, Func<TriggerDef> make)
        {
            string name = TriggerName(_g, n.Id);
            if (_made.Add(name))
            {
                var t = make();
                t.Name = name;
                t.ActivationOnly = true;
                t.GraphNode = Mark(n.Id);
                _out.Add(t);
            }
            return name;
        }

        private List<TriggerActionDef> Enter(RuleNodeDef n)
        {
            switch (n.Kind)
            {
                case RuleNodeKind.Action:
                {
                    var a = ContentJson.Clone(n.Action!);
                    a.Node = Mark(n.Id);
                    var list = new List<TriggerActionDef> { a };
                    list.AddRange(Flow(n.Id, "out"));
                    return list;
                }
                case RuleNodeKind.Sequence:
                    return Flow(n.Id, "out");
                case RuleNodeKind.EmitSignal:
                {
                    var list = _all.SelectMany(g => g.Nodes.Where(x => x.Kind == RuleNodeKind.Signal && x.Name == n.Name).Select(x => Activate(TriggerName(g, x.Id))))
                        .Select(a => { a.Node = Mark(n.Id); return a; }).ToList();
                    list.AddRange(Flow(n.Id, "out"));
                    return list;
                }
                case RuleNodeKind.GateOpen:
                case RuleNodeKind.GateClose:
                {
                    var list = new List<TriggerActionDef>
                    {
                        new() { Type = n.Kind == RuleNodeKind.GateOpen ? TriggerActionType.SetFlag : TriggerActionType.ClearFlag, Flag = GateFlag(_g, n.Name), Node = Mark(n.Id) },
                    };
                    list.AddRange(Flow(n.Id, "out"));
                    return list;
                }
                case RuleNodeKind.Branch:
                {
                    string yes = Own(new RuleNodeDef { Id = n.Id + ":да" }, () => new TriggerDef { Once = false, Condition = Always(), Actions = Flow(n.Id, "yes") });
                    string no = Own(new RuleNodeDef { Id = n.Id + ":нет" }, () => new TriggerDef { Once = false, Condition = Always(), Actions = Flow(n.Id, "no") });
                    string self = Own(n, () => new TriggerDef
                    {
                        Once = false, Condition = Always(),
                        Actions = { new TriggerActionDef { Type = TriggerActionType.IfElse, If = Bool(n.Id, "if"), Text = yes, ElseTrigger = no } },
                    });
                    return new List<TriggerActionDef> { Activate(self) };
                }
                case RuleNodeKind.Delay:
                {
                    string after = Own(n, () => new TriggerDef { Once = false, Condition = Always(), Actions = Flow(n.Id, "out") });
                    return new List<TriggerActionDef> { new() { Type = TriggerActionType.DelayedTrigger, Text = after, Delay = n.Seconds } };
                }
                case RuleNodeKind.Once:
                    return new List<TriggerActionDef> { Activate(Own(n, () => new TriggerDef { Once = true, Condition = Always(), Actions = Flow(n.Id, "out") })) };
                case RuleNodeKind.Cooldown:
                    return new List<TriggerActionDef> { Activate(Own(n, () => new TriggerDef { Once = false, Cooldown = n.Seconds, Condition = Always(), Actions = Flow(n.Id, "out") })) };
                case RuleNodeKind.LimitedRepeat:
                {
                    string name = TriggerName(_g, n.Id);
                    Own(n, () =>
                    {
                        var t = new TriggerDef { Once = false, MaxFires = Math.Max(1, n.Count), Condition = Always(), Actions = Flow(n.Id, "out") };
                        t.Actions.Add(new TriggerActionDef { Type = TriggerActionType.DelayedTrigger, Text = name, Delay = n.Seconds });
                        return t;
                    });
                    return new List<TriggerActionDef> { Activate(name) };
                }
                case RuleNodeKind.Gate:
                    return new List<TriggerActionDef>
                    {
                        Activate(Own(n, () => new TriggerDef
                        {
                            Once = false, CheckOnActivate = true, Condition = new ConditionDef { Type = ConditionType.FlagSet, Flag = GateFlag(_g, n.Name) },
                            Actions = Flow(n.Id, "out"),
                        })),
                    };
            }
            return new List<TriggerActionDef>();
        }

        private static ConditionDef Always() => new() { Type = ConditionType.Always };

        /// <summary>The condition coming into a port of logic: one link — its value; И/ИЛИ/НЕ — the tree.</summary>
        private ConditionDef Bool(string node, string port)
        {
            var src = Into(_g, node, port).Select(l => _g.Node(l.From)).Where(n => n != null).Select(n => n!).ToList();
            if (src.Count == 0) return Always();
            if (src.Count == 1) return Value(src[0]);
            return new ConditionDef { Type = ConditionType.All, Children = src.Select(Value).ToList() };
        }

        private ConditionDef Value(RuleNodeDef n) => n.Kind switch
        {
            RuleNodeKind.Condition => ContentJson.Clone(n.Condition ?? Always()),
            RuleNodeKind.And => new ConditionDef { Type = ConditionType.All, Children = Into(_g, n.Id, "in").Select(l => _g.Node(l.From)!).Select(Value).ToList() },
            RuleNodeKind.Or => new ConditionDef { Type = ConditionType.Any, Children = Into(_g, n.Id, "in").Select(l => _g.Node(l.From)!).Select(Value).ToList() },
            RuleNodeKind.Not => new ConditionDef { Type = ConditionType.Not, Children = Into(_g, n.Id, "in").Select(l => _g.Node(l.From)!).Select(Value).ToList() },
            _ => Always(),
        };
    }
}
