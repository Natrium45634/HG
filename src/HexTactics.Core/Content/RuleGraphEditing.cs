using System;
using System.Collections.Generic;
using System.Linq;
using HexTactics.Core.Sim;

namespace HexTactics.Core.Content;

/// <summary>An item of the palette of the graph editor; without <see cref="Create"/> it is shown as unavailable with the reason.</summary>
public sealed record GraphPaletteItem(string Group, string Name, string Description, Func<RuleNodeDef>? Create, string Why = "")
{
    public bool Available => Create != null;
}

/// <summary>What a node did in a battle, from the trace (the debugger of the graph, ТЗ ч. 6, 7.6).</summary>
public sealed class GraphNodeRun
{
    public int Count { get; set; }
    public long LastTick { get; set; }
    public string LastText { get; set; } = "";
    /// <summary>The last entry stopped the flow («условие ложно», «не чаще…», «ветка «нет»» is not a stop).</summary>
    public bool Blocked { get; set; }
}

public static partial class RuleGraphs
{
    public const string GroupSources = "Источники";
    public const string GroupConditions = "Условия";
    public const string GroupLogic = "Логика";
    public const string GroupControl = "Управление";
    public const string GroupActions = "Действия";
    public const string GroupUnavailable = "Недоступно";

    /// <summary>The actions the graph has its own nodes for (offered as nodes, not as actions).</summary>
    private static readonly TriggerActionType[] GraphOwnActions = { TriggerActionType.DelayedTrigger, TriggerActionType.IfElse };

    /// <summary>
    /// The palette (ТЗ 7.2): the sources (the events of the scenario, the timer, the signal), the conditions of the catalog, the
    /// logic, the nodes of control, the actions of the catalog and «послать сигнал»; what the runtime cannot do — unavailable, with why.
    /// </summary>
    public static List<GraphPaletteItem> Palette()
    {
        var list = new List<GraphPaletteItem>();
        foreach (var e in Enum.GetValues<ScenarioEventType>().Where(e => e is not ScenarioEventType.None and not ScenarioEventType.Timer))
        {
            var ev = e;
            list.Add(new(GroupSources, "Событие: " + EdUtil.EnumLabel(ev), EdUtil.EnumDescription(ev),
                () => new RuleNodeDef { Kind = RuleNodeKind.Event, Event = ev, Condition = ev == ScenarioEventType.ConditionMet ? new ConditionDef { Type = ConditionType.Always } : null }));
        }
        list.Add(new(GroupSources, "Таймер", "Через N с боя; «повторяемый» — каждые N с", () => new RuleNodeDef { Kind = RuleNodeKind.Timer, Seconds = 10 }));
        list.Add(new(GroupSources, "Сигнал (приём)", "Запускается узлом «Послать сигнал» с тем же именем (из любого графа сценария)",
            () => new RuleNodeDef { Kind = RuleNodeKind.Signal, Name = "сигнал" }));
        foreach (var c in OperationCatalog.Conditions.Where(c => c.Create() is ConditionDef { Type: not (ConditionType.All or ConditionType.Any or ConditionType.Not) }))
        {
            var cc = c;
            list.Add(new(GroupConditions, c.Name, c.Description, () => new RuleNodeDef { Kind = RuleNodeKind.Condition, Condition = (ConditionDef)cc.Create() }));
        }
        foreach (var k in new[] { RuleNodeKind.And, RuleNodeKind.Or, RuleNodeKind.Not })
        {
            var kk = k;
            list.Add(new(GroupLogic, EdUtil.EnumLabel(k), EdUtil.EnumDescription(k), () => new RuleNodeDef { Kind = kk }));
        }
        foreach (var k in new[]
                 {
                     RuleNodeKind.Branch, RuleNodeKind.Sequence, RuleNodeKind.Delay, RuleNodeKind.Once, RuleNodeKind.Cooldown, RuleNodeKind.LimitedRepeat,
                     RuleNodeKind.Gate, RuleNodeKind.GateOpen, RuleNodeKind.GateClose,
                 })
        {
            var kk = k;
            list.Add(new(GroupControl, EdUtil.EnumLabel(k), EdUtil.EnumDescription(k), () => new RuleNodeDef
            {
                Kind = kk, Seconds = kk == RuleNodeKind.Cooldown ? 10 : kk == RuleNodeKind.LimitedRepeat ? 5 : 3, Name = kk is RuleNodeKind.Gate or RuleNodeKind.GateOpen or RuleNodeKind.GateClose ? "ворота" : "",
            }));
        }
        foreach (var a in OperationCatalog.Actions.Where(a => a.Create() is TriggerActionDef t && !GraphOwnActions.Contains(t.Type)))
        {
            var aa = a;
            list.Add(new(GroupActions, a.Name, a.Description, () => new RuleNodeDef { Kind = RuleNodeKind.Action, Action = (TriggerActionDef)aa.Create() }));
        }
        list.Add(new(GroupActions, "Послать сигнал", EdUtil.EnumDescription(RuleNodeKind.EmitSignal), () => new RuleNodeDef { Kind = RuleNodeKind.EmitSignal, Name = "сигнал" }));
        foreach (var (name, why) in Unavailable) list.Add(new(GroupUnavailable, name, "", null, why));
        return list;
    }

    /// <summary>The palette filtered by text (name, group, description; any case).</summary>
    public static List<GraphPaletteItem> Palette(string filter)
    {
        var all = Palette();
        filter = filter.Trim();
        if (filter == "") return all;
        return all.Where(p => p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || p.Group.Contains(filter, StringComparison.OrdinalIgnoreCase)
                              || p.Description.Contains(filter, StringComparison.OrdinalIgnoreCase) || p.Why.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static string Prefix(RuleNodeKind k) => k switch
    {
        RuleNodeKind.Event => "event", RuleNodeKind.Timer => "timer", RuleNodeKind.Signal => "signal", RuleNodeKind.Condition => "cond",
        RuleNodeKind.And => "and", RuleNodeKind.Or => "or", RuleNodeKind.Not => "not", RuleNodeKind.Branch => "branch", RuleNodeKind.Sequence => "seq",
        RuleNodeKind.Delay => "delay", RuleNodeKind.Once => "once", RuleNodeKind.Cooldown => "cooldown", RuleNodeKind.LimitedRepeat => "repeat",
        RuleNodeKind.Gate => "gate", RuleNodeKind.GateOpen => "gate_open", RuleNodeKind.GateClose => "gate_close", RuleNodeKind.Action => "action",
        _ => "emit",
    };

    /// <summary>A free ID of a node of a kind: «branch1», «branch2»…</summary>
    public static string NewNodeId(RuleGraphDef g, RuleNodeKind k)
    {
        string p = Prefix(k);
        int n = 1;
        while (g.Node(p + n) != null) n++;
        return p + n;
    }

    /// <summary>Adds a node to a graph at a place on the canvas with a free ID.</summary>
    public static RuleNodeDef AddNode(RuleGraphDef g, RuleNodeDef n, float x, float y)
    {
        n.Id = n.Id == "" || g.Node(n.Id) != null ? NewNodeId(g, n.Kind) : n.Id;
        n.X = x;
        n.Y = y;
        g.Nodes.Add(n);
        return n;
    }

    /// <summary>Removes nodes and every link of theirs.</summary>
    public static int RemoveNodes(RuleGraphDef g, ICollection<string> ids)
    {
        g.Links.RemoveAll(l => ids.Contains(l.From) || ids.Contains(l.To));
        return g.Nodes.RemoveAll(n => ids.Contains(n.Id));
    }

    /// <summary>Copies of nodes with free IDs, shifted, with the links between them.</summary>
    public static List<RuleNodeDef> Duplicate(RuleGraphDef g, ICollection<string> ids, float dx = 40, float dy = 40)
    {
        var map = new Dictionary<string, string>();
        var made = new List<RuleNodeDef>();
        foreach (var n in g.Nodes.Where(n => ids.Contains(n.Id)).ToList())
        {
            var c = ContentJson.Clone(n);
            c.Id = "";
            AddNode(g, c, n.X + dx, n.Y + dy);
            map[n.Id] = c.Id;
            made.Add(c);
        }
        foreach (var l in g.Links.Where(l => map.ContainsKey(l.From) && map.ContainsKey(l.To)).ToList())
            g.Links.Add(new RuleLinkDef { From = map[l.From], FromPort = l.FromPort, To = map[l.To], ToPort = l.ToPort });
        return made;
    }

    /// <summary>Renames a node with its links (false — the ID is taken, empty or not allowed).</summary>
    public static bool RenameNode(RuleGraphDef g, string from, string to)
    {
        if (to == "" || to == from || g.Node(to) != null || BadId(to) || g.Node(from) is not { } n) return false;
        n.Id = to;
        foreach (var l in g.Links)
        {
            if (l.From == from) l.From = to;
            if (l.To == from) l.To = to;
        }
        return true;
    }

    /// <summary>An ID of a node may not have «:» and «/» (they separate the graph, the node and its branch in the trace).</summary>
    public static bool BadId(string id) => id.IndexOfAny(new[] { ':', '/' }) >= 0;

    /// <summary>
    /// Where a graph uses an entity (ТЗ 7.3 «ссылки на сущности»): the nodes whose ID, label, name, filters, condition or action
    /// mention the text (an ID of an object, a wave, a unit, a flag, a signal…).
    /// </summary>
    public static List<RuleNodeDef> Uses(RuleGraphDef g, string text)
    {
        text = text.Trim();
        if (text == "") return new();
        bool Has(string s) => s.Contains(text, StringComparison.OrdinalIgnoreCase);
        return g.Nodes.Where(n => Has(n.Id) || Has(n.Label) || Has(n.Name) || Has(n.EventObject) || Has(n.EventUnit) || Has(Title(n))
                                  || n.Condition != null && Texts(ContentJson.ToNode(n.Condition)).Any(Has)
                                  || n.Action != null && Texts(ContentJson.ToNode(n.Action)).Any(Has)).ToList();
    }

    /// <summary>The text values of a JSON tree (not the names of the fields: «elite» must not find every action with «maxEliteShare»).</summary>
    private static IEnumerable<string> Texts(System.Text.Json.Nodes.JsonNode? node)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject o:
                foreach (var kv in o)
                    foreach (var t in Texts(kv.Value))
                        yield return t;
                break;
            case System.Text.Json.Nodes.JsonArray a:
                foreach (var x in a)
                    foreach (var t in Texts(x))
                        yield return t;
                break;
            case System.Text.Json.Nodes.JsonValue v when v.TryGetValue<string>(out var str):
                yield return str;
                break;
        }
    }

    /// <summary>Where every graph of a scenario uses an entity: (graph, node).</summary>
    public static List<(RuleGraphDef Graph, RuleNodeDef Node)> UsesIn(ScenarioDef sc, string text) =>
        sc.Graphs.SelectMany(g => Uses(g, text).Select(n => (g, n))).ToList();

    /// <summary>
    /// «Пробная компиляция»: the triggers one graph becomes (even switched off), or none when it has errors — what the battle
    /// would run, shown before a test.
    /// </summary>
    public static List<TriggerDef> CompileOne(ScenarioDef sc, RuleGraphDef g)
    {
        if (Check(sc, g).Any(p => p.Error)) return new();
        var good = sc.Graphs.Where(x => x == g || x.Enabled && !Check(sc, x).Any(p => p.Error)).ToList();
        return new Compiler(sc, g, good).Run();
    }

    /// <summary>
    /// The example of ТЗ 7.7 for a scenario: «юнит погиб (метка первого врага)» → «если не прошло 120 с» → да: первая волна,
    /// нет: вторая (или первая). A free ID of a graph, nodes laid out left to right.
    /// </summary>
    public static RuleGraphDef Example(ScenarioDef sc)
    {
        int k = 1;
        while (sc.Graphs.Any(g => g.Id == "boss" + (k == 1 ? "" : k.ToString()))) k++;
        string tag = sc.Units.FirstOrDefault(u => u.Faction == "enemy" && u.Tag != "")?.Tag ?? "";
        int elite = 0, normal = sc.Waves.Count > 1 ? 1 : 0;
        var g = new RuleGraphDef
        {
            Id = "boss" + (k == 1 ? "" : k.ToString()), Name = "Смерть босса до 120 с",
            Nodes =
            {
                new RuleNodeDef { Id = "died", Kind = RuleNodeKind.Event, Event = ScenarioEventType.UnitDied, EventUnit = tag, X = 0, Y = 0 },
                new RuleNodeDef { Id = "t120", Kind = RuleNodeKind.Condition, Condition = new ConditionDef { Type = ConditionType.TimeElapsed, Seconds = 120 }, X = 0, Y = 200 },
                new RuleNodeDef { Id = "early", Kind = RuleNodeKind.Not, X = 260, Y = 200 },
                new RuleNodeDef { Id = "branch", Kind = RuleNodeKind.Branch, X = 500, Y = 40 },
                new RuleNodeDef { Id = "elite", Kind = RuleNodeKind.Action, Label = "Элитный резерв", Action = new TriggerActionDef { Type = TriggerActionType.SpawnWave, Wave = elite }, X = 780, Y = -40 },
                new RuleNodeDef { Id = "normal", Kind = RuleNodeKind.Action, Label = "Обычный резерв", Action = new TriggerActionDef { Type = TriggerActionType.SpawnWave, Wave = normal }, X = 780, Y = 140 },
            },
            Links =
            {
                new RuleLinkDef { From = "died", To = "branch" },
                new RuleLinkDef { From = "t120", FromPort = "value", To = "early" },
                new RuleLinkDef { From = "early", FromPort = "value", To = "branch", ToPort = "if" },
                new RuleLinkDef { From = "branch", FromPort = "yes", To = "elite" },
                new RuleLinkDef { From = "branch", FromPort = "no", To = "normal" },
            },
        };
        return g;
    }

    /// <summary>The node of an entry of the trace: «branch:да» → «branch».</summary>
    public static string NodeOf(GraphTraceEntry e)
    {
        int c = e.Node.IndexOf(':');
        return c > 0 ? e.Node[..c] : e.Node;
    }

    /// <summary>What every node of a graph did in a battle (count, last tick, last text, stopped or not).</summary>
    public static Dictionary<string, GraphNodeRun> Runs(IEnumerable<GraphTraceEntry> trace, string graph)
    {
        var map = new Dictionary<string, GraphNodeRun>();
        foreach (var e in trace.Where(e => e.Graph == graph && e.Node != ""))
        {
            // The triggers of the branches of «ветвление» («branch:да») only repeat its «ветка «да»» — they are not runs of their own.
            if (e.Node.Contains(':')) continue;
            string id = NodeOf(e);
            if (!map.TryGetValue(id, out var r)) map[id] = r = new GraphNodeRun();
            bool blocked = e.Text.StartsWith("условие ложно", StringComparison.Ordinal) || e.Text.StartsWith("не чаще", StringComparison.Ordinal);
            // «ветка «да» / «нет»» follows the «запущен» of the same run.
            if (!blocked && !e.Text.StartsWith("ветка", StringComparison.Ordinal)) r.Count++;
            r.LastTick = e.Tick;
            r.LastText = e.Text;
            r.Blocked = blocked;
        }
        return map;
    }
}
