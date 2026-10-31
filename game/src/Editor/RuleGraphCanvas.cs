using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// The canvas of a graph of events (ТЗ ч. 6, 7; docs/DECISIONS_6.md §101): the nodes with typed ports (a flow — light, a value
/// of truth — green), links checked before they are stored (a refused one says why), delete, duplicate (Ctrl+D), the
/// minimap. The same canvas, read-only, shows in a test battle what the nodes did (the debugger).
/// </summary>
public partial class RuleGraphCanvas : GraphEdit
{
    public static readonly Color FlowColor = new(0.85f, 0.88f, 0.95f);
    public static readonly Color BoolColor = new(0.45f, 0.85f, 0.45f);

    private readonly Dictionary<StringName, string> _ids = new();
    private readonly Dictionary<string, GraphNode> _nodes = new();

    public RuleGraphDef? Graph { get; private set; }
    public bool ReadOnly { get; set; }
    /// <summary>What the nodes did in a battle (null — the editor: no marks).</summary>
    public Dictionary<string, GraphNodeRun>? Runs { get; set; }
    /// <summary>Is a breakpoint set on a node (drawn as ● in its title).</summary>
    public Func<string, bool>? IsBreakpoint { get; set; }
    /// <summary>Nodes drawn as found (the search of uses).</summary>
    public HashSet<string> Found { get; } = new();
    /// <summary>Nodes with problems: true — an error.</summary>
    public Dictionary<string, bool> Problems { get; } = new();

    /// <summary>The graph changed (nodes, links, places): the caller stores it as one operation.</summary>
    public event Action? Changed;
    public event Action<string>? NodeChosen;
    public event Action<string>? Rejected;

    public IEnumerable<string> SelectedIds => _nodes.Where(kv => IsInstanceValid(kv.Value) && kv.Value.Selected).Select(kv => kv.Key);
    public GraphNode? NodeView(string id) => _nodes.GetValueOrDefault(id);

    public RuleGraphCanvas()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        MinimapEnabled = true;
        MinimapSize = new Vector2(200, 130);
        RightDisconnects = true;
        ShowArrangeButton = true;
        // Different types may be dragged onto each other so that the refusal can say why (a silent no-snap would not).
        AddValidConnectionType((int)RulePortType.Flow, (int)RulePortType.Bool);
        AddValidConnectionType((int)RulePortType.Bool, (int)RulePortType.Flow);
        ConnectionRequest += OnConnect;
        DisconnectionRequest += OnDisconnect;
        DeleteNodesRequest += OnDelete;
        DuplicateNodesRequest += OnDuplicate;
        EndNodeMove += OnMoved;
        NodeSelected += n => NodeChosen?.Invoke(_ids.GetValueOrDefault(n.Name, ""));
        NodeDeselected += _ => Callable.From(() =>
        {
            if (!SelectedIds.Any()) NodeChosen?.Invoke("");
        }).CallDeferred();
        var fit = new Button { Text = "⤢", TooltipText = "Вписать граф в холст", FocusMode = FocusModeEnum.None, Name = "GraphFit" };
        fit.Pressed += FitView;
        GetMenuHBox().AddChild(fit);
        // Until the user moves the view, a resized canvas fits the graph again.
        Resized += () =>
        {
            if (_autoFit) _fitIn = 2;
        };
        GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true } or InputEventMagnifyGesture or InputEventPanGesture) _autoFit = false;
        };
    }

    private bool _autoFit;

    private int _fitIn = -1;

    public override void _Process(double delta)
    {
        // A graph just opened is fitted once its nodes know their sizes (a couple of frames).
        if (_fitIn < 0 || --_fitIn > 0) return;
        _fitIn = -1;
        FitView();
    }

    /// <summary>Zooms and scrolls so that every node is seen (not closer than 1:1).</summary>
    public void FitView()
    {
        var views = _nodes.Values.Where(IsInstanceValid).ToList();
        if (views.Count == 0 || Size.X < 10 || Size.Y < 10) return;
        var box = new Rect2(views[0].PositionOffset, views[0].Size);
        foreach (var v in views.Skip(1)) box = box.Merge(new Rect2(v.PositionOffset, v.Size));
        box = box.Grow(40);
        Zoom = Math.Clamp(Math.Min(Size.X / box.Size.X, Size.Y / box.Size.Y), Math.Max(ZoomMin, 0.35f), 1f);
        ScrollOffset = box.GetCenter() * Zoom - Size / 2;
    }

    /// <summary>The place on the canvas at the middle of what is seen (a node from the palette goes there).</summary>
    public Vector2 VisibleCenter => (ScrollOffset + Size / 2) / Zoom - new Vector2(90, 40);

    public void Show(RuleGraphDef? g)
    {
        Graph = g;
        Rebuild();
        _fitIn = 2;
        _autoFit = true;
    }

    public void Rebuild()
    {
        var selected = SelectedIds.ToHashSet();
        ClearConnections();
        foreach (var gn in _nodes.Values)
            if (IsInstanceValid(gn))
            {
                RemoveChild(gn);
                gn.QueueFree();
            }
        _nodes.Clear();
        _ids.Clear();
        if (Graph == null) return;
        int i = 0;
        foreach (var n in Graph.Nodes)
        {
            if (_nodes.ContainsKey(n.Id)) continue; // a repeated ID is an error of the check; the first one is drawn
            var gn = MakeNode(n, "N" + i++);
            gn.Selected = selected.Contains(n.Id);
            AddChild(gn);
            _nodes[n.Id] = gn;
            _ids[gn.Name] = n.Id;
        }
        foreach (var l in Graph.Links) DrawLink(l);
    }

    private void DrawLink(RuleLinkDef l)
    {
        if (Graph?.Node(l.From) is not { } a || Graph.Node(l.To) is not { } b) return;
        int fi = RuleGraphs.Outputs(a.Kind).ToList().FindIndex(p => p.Name == l.FromPort);
        int ti = RuleGraphs.Inputs(b.Kind).ToList().FindIndex(p => p.Name == l.ToPort);
        if (fi < 0 || ti < 0 || !_nodes.TryGetValue(l.From, out var ga) || !_nodes.TryGetValue(l.To, out var gb)) return;
        ConnectNode(ga.Name, fi, gb.Name, ti);
    }

    private static Color PortColor(RulePortType t) => t == RulePortType.Bool ? BoolColor : FlowColor;

    private GraphNode MakeNode(RuleNodeDef n, string name)
    {
        var gn = new GraphNode
        {
            Name = name, Title = $"{EdUtil.EnumLabel(n.Kind)} · {n.Id}", PositionOffset = new Vector2(n.X, n.Y),
            CustomMinimumSize = new Vector2(190, 0), Draggable = !ReadOnly, Selectable = true,
        };
        if (IsBreakpoint?.Invoke(n.Id) == true)
            gn.GetTitlebarHBox().AddChild(Ui.Label("●", 14, Ui.Bad));
        var ins = RuleGraphs.Inputs(n.Kind);
        var outs = RuleGraphs.Outputs(n.Kind);
        int rows = Math.Max(ins.Count, outs.Count);
        for (int r = 0; r < rows; r++)
        {
            var row = Ui.HBox(8);
            var left = Ui.Label(r < ins.Count ? ins[r].Caption : "", 12, Ui.TextDim).Expand();
            var right = Ui.Label(r < outs.Count ? outs[r].Caption : "", 12, Ui.TextDim);
            right.HorizontalAlignment = HorizontalAlignment.Right;
            row.AddChild(left);
            row.AddChild(right);
            gn.AddChild(row);
            bool hasIn = r < ins.Count, hasOut = r < outs.Count;
            gn.SetSlot(r, hasIn, hasIn ? (int)ins[r].Type : 0, hasIn ? PortColor(ins[r].Type) : Colors.White,
                hasOut, hasOut ? (int)outs[r].Type : 0, hasOut ? PortColor(outs[r].Type) : Colors.White);
        }
        var body = Ui.Label(RuleGraphs.Title(n), 13, Ui.Text, wrap: true);
        body.CustomMinimumSize = new Vector2(170, 0);
        body.Name = "Body";
        gn.AddChild(body);

        Color? border = null;
        string tip = n.Label != "" ? n.Label : "";
        if (Problems.TryGetValue(n.Id, out bool error)) border = error ? Ui.Bad : Ui.Warn;
        if (Found.Contains(n.Id)) border = Ui.Accent;
        if (Runs != null)
        {
            if (Runs.TryGetValue(n.Id, out var run))
            {
                border = run.Blocked ? Ui.Bad : Ui.Good;
                var mark = Ui.Label((run.Blocked ? "✗ " : $"✓ ×{run.Count} ") + $"тик {run.LastTick}: {run.LastText}", 12, run.Blocked ? Ui.Bad : Ui.Good, wrap: true);
                mark.CustomMinimumSize = new Vector2(170, 0);
                mark.Name = "Run";
                gn.AddChild(mark);
                tip = run.LastText;
            }
            else gn.Modulate = new Color(1, 1, 1, 0.55f);
        }
        if (border is { } c)
        {
            var sb = Ui.Box(Ui.Panel, c, 6, 2);
            gn.AddThemeStyleboxOverride("panel", sb);
        }
        gn.TooltipText = tip;
        return gn;
    }

    /// <summary>Links two ports by their names (the canvas and the UI tests): null — linked, else why not.</summary>
    public string? TryLink(string from, string fromPort, string to, string toPort)
    {
        if (Graph == null || ReadOnly) return "граф не открыт";
        var link = new RuleLinkDef { From = from, FromPort = fromPort, To = to, ToPort = toPort };
        if (RuleGraphs.CanLink(Graph, link, adding: true) is { } why)
        {
            Rejected?.Invoke(why);
            return why;
        }
        Graph.Links.Add(link);
        DrawLink(link);
        Changed?.Invoke();
        return null;
    }

    private void OnConnect(StringName fromNode, long fromPort, StringName toNode, long toPort)
    {
        if (Graph == null || ReadOnly) return;
        string a = _ids.GetValueOrDefault(fromNode, ""), b = _ids.GetValueOrDefault(toNode, "");
        var outs = Graph.Node(a) is { } na ? RuleGraphs.Outputs(na.Kind) : Array.Empty<RuleGraphs.Port>();
        var ins = Graph.Node(b) is { } nb ? RuleGraphs.Inputs(nb.Kind) : Array.Empty<RuleGraphs.Port>();
        if (fromPort >= outs.Count || toPort >= ins.Count) return;
        TryLink(a, outs[(int)fromPort].Name, b, ins[(int)toPort].Name);
    }

    private void OnDisconnect(StringName fromNode, long fromPort, StringName toNode, long toPort)
    {
        if (Graph == null || ReadOnly) return;
        string a = _ids.GetValueOrDefault(fromNode, ""), b = _ids.GetValueOrDefault(toNode, "");
        if (Graph.Node(a) is not { } na || Graph.Node(b) is not { } nb) return;
        var outs = RuleGraphs.Outputs(na.Kind);
        var ins = RuleGraphs.Inputs(nb.Kind);
        if (fromPort >= outs.Count || toPort >= ins.Count) return;
        string fp = outs[(int)fromPort].Name, tp = ins[(int)toPort].Name;
        if (Graph.Links.RemoveAll(l => l.From == a && l.FromPort == fp && l.To == b && l.ToPort == tp) == 0) return;
        DisconnectNode(fromNode, (int)fromPort, toNode, (int)toPort);
        Changed?.Invoke();
    }

    /// <summary>Deletes nodes with their links (Delete on the canvas, the button of the panel).</summary>
    public void DeleteNodes(ICollection<string> ids)
    {
        if (Graph == null || ReadOnly || ids.Count == 0) return;
        RuleGraphs.RemoveNodes(Graph, ids);
        Rebuild();
        Changed?.Invoke();
        NodeChosen?.Invoke("");
    }

    private void OnDelete(Godot.Collections.Array<StringName> nodes)
    {
        var ids = nodes.Select(v => _ids.GetValueOrDefault(v, "")).Where(s => s != "").ToList();
        DeleteNodes(ids);
    }

    public void DuplicateSelected()
    {
        if (Graph == null || ReadOnly) return;
        var ids = SelectedIds.ToList();
        if (ids.Count == 0) return;
        var made = RuleGraphs.Duplicate(Graph, ids);
        foreach (var gn in _nodes.Values) gn.Selected = false;
        Rebuild();
        foreach (var n in made)
            if (_nodes.TryGetValue(n.Id, out var gn))
                gn.Selected = true;
        Changed?.Invoke();
    }

    private void OnDuplicate() => DuplicateSelected();

    private void OnMoved()
    {
        if (Graph == null || ReadOnly) return;
        bool moved = false;
        foreach (var (id, gn) in _nodes)
            if (Graph.Node(id) is { } n && (Math.Abs(n.X - gn.PositionOffset.X) > 0.5f || Math.Abs(n.Y - gn.PositionOffset.Y) > 0.5f))
            {
                n.X = MathF.Round(gn.PositionOffset.X);
                n.Y = MathF.Round(gn.PositionOffset.Y);
                moved = true;
            }
        if (moved) Changed?.Invoke();
    }

    /// <summary>Selects one node and scrolls to it.</summary>
    public void Focus(string id)
    {
        foreach (var gn in _nodes.Values) gn.Selected = false;
        if (!_nodes.TryGetValue(id, out var v)) return;
        v.Selected = true;
        ScrollOffset = v.PositionOffset * Zoom - Size / 2 + v.Size * Zoom / 2;
        NodeChosen?.Invoke(id);
    }
}
