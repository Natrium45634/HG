using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Sim;
using HexTactics.Game.Editor;
using HexTactics.Game.UI;

namespace HexTactics.Game.Battle;

/// <summary>
/// Breakpoints on the nodes of the graphs of events, set in the editor (docs/DECISIONS_6.md §101): a state of the session of the
/// editor, not of the content and not of the battle. Only a test from the editor (the simulator) stops on them.
/// </summary>
public static class GraphBreakpoints
{
    private static readonly HashSet<string> Set = new();

    private static string Key(string scenario, string graph, string node) => $"{scenario}/{graph}/{node}";

    public static bool Has(string scenario, string graph, string node) => Set.Contains(Key(scenario, graph, node));

    /// <summary>Sets or removes a breakpoint; true — it is set now.</summary>
    public static bool Toggle(string scenario, string graph, string node)
    {
        var k = Key(scenario, graph, node);
        if (Set.Remove(k)) return false;
        Set.Add(k);
        return true;
    }

    /// <summary>«граф/узел» of every breakpoint of a scenario.</summary>
    public static IEnumerable<string> In(string scenario) => Set.Where(k => k.StartsWith(scenario + "/", StringComparison.Ordinal)).Select(k => k[(scenario.Length + 1)..]);

    public static int CountIn(string scenario) => In(scenario).Count();

    public static void ClearIn(string scenario) => Set.RemoveWhere(k => k.StartsWith(scenario + "/", StringComparison.Ordinal));
}

/// <summary>
/// «Граф событий» of the test battle (ТЗ ч. 6, 7.6; §101): the graph with what its nodes did (✓ and how many times, ✗ and why),
/// the trace in the order of the ticks with the values of the conditions, a click — the node on the canvas, and the breakpoint
/// on a node (only a test from the editor). Reading the trace changes nothing in the battle.
/// </summary>
public partial class GraphDebugPanel : PanelContainer
{
    private readonly BattleScreen _bs;
    private readonly OptionButton _graph;
    private readonly RuleGraphCanvas _canvas;
    private readonly ItemList _trace;
    private readonly Label _node;
    private readonly Button _break;
    private readonly List<GraphTraceEntry> _shown = new();
    private int _seen = -1;
    private long _seenTick = -1;
    private double _wait;

    public GraphDebugPanel(BattleScreen bs)
    {
        _bs = bs;
        Name = "GraphDebugPanel";
        Visible = false;
        AddThemeStyleboxOverride("panel", Ui.Box(new Color(0.05f, 0.06f, 0.09f, 0.97f), new Color(0.45f, 0.85f, 0.45f, 0.9f), 1, 6, 10));
        var v = Ui.VBox(6);
        var head = Ui.HBox(6);
        head.AddChild(Ui.Label("Граф событий — отладка", 17, Ui.Good).Expand());
        _graph = new OptionButton { Name = "GraphDebugChoice", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(220, 0) };
        _graph.ItemSelected += _ => Refresh(true);
        head.AddChild(_graph);
        head.AddChild(Ui.Button("✕", () => Visible = false, "Закрыть"));
        v.AddChild(head);
        v.AddChild(Ui.Label("✓ — узел сработал (сколько раз, последний тик и что сделал), ✗ — остановил поток (почему), бледный — не запускался.", 12, Ui.TextDim, wrap: true));
        _canvas = new RuleGraphCanvas { Name = "GraphDebugCanvas", ReadOnly = true, CustomMinimumSize = new Vector2(820, 330), MinimapSize = new Vector2(150, 90) };
        _canvas.SizeFlagsVertical = SizeFlags.Fill;
        _canvas.NodeChosen += ShowNode;
        v.AddChild(_canvas);
        var row = Ui.HBox(6);
        _node = Ui.Label("Выберите узел или строку трассы.", 12, Ui.Text, wrap: true).Expand();
        // A wrapped label measured at no width would make the window as tall as its letters.
        _node.CustomMinimumSize = new Vector2(560, 0);
        _node.Name = "GraphDebugNode";
        row.AddChild(_node);
        _break = Ui.Button("● Точка останова", ToggleBreak, "Бой встанет на паузу, когда узел сработает (только тест из редактора)");
        _break.Name = "GraphDebugBreak";
        _break.Visible = bs.Admin;
        row.AddChild(_break);
        v.AddChild(row);
        _trace = new ItemList { Name = "GraphDebugTrace", CustomMinimumSize = new Vector2(820, 150), SameColumnWidth = true, SizeFlagsVertical = SizeFlags.Fill };
        _trace.ItemSelected += i =>
        {
            if (i >= 0 && i < _shown.Count) _canvas.Focus(RuleGraphs.NodeOf(_shown[(int)i]));
        };
        v.AddChild(_trace);
        AddChild(v);
    }

    private ScenarioDef? Def => _bs.Sim.Scenario.Def;
    private RuleGraphDef? Graph => Def is { } d && _graph.Selected >= 0 && _graph.Selected < d.Graphs.Count ? d.Graphs[_graph.Selected] : null;
    public RuleGraphCanvas Canvas => _canvas;
    public int TraceRows => _trace.ItemCount;
    public string NodeText => _node.Text;

    private Vector2 _fitted;

    /// <summary>Fits the window into the screen: the canvas takes what the trace and the header leave.</summary>
    public void Fit(Vector2 screen)
    {
        if (screen == _fitted) return;
        _fitted = screen;
        float w = Math.Clamp(screen.X - 40, 600, 1100);
        float h = Math.Clamp(screen.Y - 64 - 330, 200, 520);
        _canvas.CustomMinimumSize = new Vector2(w, h);
        _trace.CustomMinimumSize = new Vector2(w, Math.Clamp(screen.Y * 0.16f, 100, 220));
        Size = Vector2.Zero; // shrink to the content
    }

    public void Open()
    {
        _graph.Clear();
        foreach (var g in Def?.Graphs ?? new())
            _graph.AddItem((g.Enabled ? "" : "⏸ ") + $"{g.DisplayName} [{g.Id}]");
        if (_graph.ItemCount > 0) _graph.Select(0);
        Visible = true;
        Refresh(true);
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _wait -= delta;
        if (_wait > 0) return;
        _wait = 0.25;
        Refresh(false);
    }

    /// <summary>Redraws the marks and the trace when the trace grew (or always).</summary>
    public void Refresh(bool always)
    {
        var g = Graph;
        var trace = _bs.Sim.Scenario.GraphTrace;
        long last = trace.Count > 0 ? trace[^1].Tick : -1;
        if (!always && trace.Count == _seen && last == _seenTick) return;
        _seen = trace.Count;
        _seenTick = last;
        string sc = Def?.Id ?? "";
        _canvas.IsBreakpoint = id => g != null && _bs.Admin && GraphBreakpoints.Has(sc, g.Id, id);
        _canvas.Runs = g == null ? null : RuleGraphs.Runs(trace, g.Id);
        if (g != null && _canvas.Graph != g) _canvas.Show(g);
        else _canvas.Rebuild();
        _trace.Clear();
        _shown.Clear();
        if (g == null)
        {
            _trace.AddItem(Def?.Graphs.Count > 0 ? "Граф не выбран" : "В сценарии нет графов событий");
            return;
        }
        foreach (var e in trace.Where(e => e.Graph == g.Id).Reverse().Take(60))
        {
            _shown.Add(e);
            int i = _trace.AddItem($"тик {e.Tick} ({e.Tick * _bs.Sim.Dt:0.0} с) · {e.Node}: {e.Text}");
            bool stop = e.Text.StartsWith("условие ложно", StringComparison.Ordinal) || e.Text.StartsWith("не чаще", StringComparison.Ordinal);
            _trace.SetItemCustomFgColor(i, stop ? Ui.Bad : e.Text.StartsWith("ветка «нет»", StringComparison.Ordinal) ? Ui.Warn : Ui.Text);
            _trace.SetItemTooltip(i, e.Text);
        }
        if (_shown.Count == 0) _trace.AddItem("Узлы графа ещё не срабатывали");
    }

    private string _chosen = "";

    private void ShowNode(string id)
    {
        _chosen = id;
        var g = Graph;
        if (g?.Node(id) is not { } n)
        {
            _node.Text = "Выберите узел или строку трассы.";
            return;
        }
        var runs = RuleGraphs.Runs(_bs.Sim.Scenario.GraphTrace, g.Id);
        string what = runs.TryGetValue(id, out var r)
            ? (r.Blocked ? "✗ остановил поток" : $"✓ сработал {r.Count} раз") + $", последний тик {r.LastTick} ({r.LastTick * _bs.Sim.Dt:0.0} с): {r.LastText}"
            : "не запускался";
        _node.Text = $"{n.Id} · {EdUtil.EnumLabel(n.Kind)}: {RuleGraphs.Title(n)}\n{what}";
        bool bp = GraphBreakpoints.Has(Def?.Id ?? "", g.Id, id);
        _break.Text = bp ? "○ Снять точку останова" : "● Точка останова";
    }

    private void ToggleBreak()
    {
        var g = Graph;
        if (g == null || _chosen == "" || !_bs.Admin) return;
        bool on = GraphBreakpoints.Toggle(Def?.Id ?? "", g.Id, _chosen);
        string key = g.Id + "/" + _chosen;
        if (on) _bs.Breaks.GraphNodes.Add(key);
        else _bs.Breaks.GraphNodes.Remove(key);
        ShowNode(_chosen);
        Refresh(true);
    }
}
