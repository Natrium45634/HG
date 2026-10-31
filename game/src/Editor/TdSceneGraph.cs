using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.Battle;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// «Граф событий» of the scene (ТЗ ч. 6, 7; docs/DECISIONS_6.md §101): the graphs of the scenario on a canvas in place of the
/// map, the palette (search; what the runtime cannot do — marked unavailable), the properties of a node, the problems, the
/// search of uses, «пробная компиляция» (the triggers the battle will run) and breakpoints for the test battle.
/// Every edit is one operation of the history of the editor (Ctrl+Z / Ctrl+Y).
/// </summary>
public partial class TdScenePanel
{
    private RuleGraphCanvas _graphCanvas = null!;
    private ScrollContainer? _libScroll, _propScroll;
    /// <summary>«⛶»: the canvas takes the width of the palette and of the properties too.</summary>
    public bool GraphWide { get; private set; }
    /// <summary>The open graph survives the rebuild of the panel after an undo.</summary>
    private static string s_graph = "";
    private string _paletteFilter = "";
    private string _usesFilter = "";
    private VBoxContainer? _paletteBox;
    private VBoxContainer? _usesBox;

    public RuleGraphCanvas GraphCanvas => _graphCanvas;
    public RuleGraphDef? OpenGraph => _sc == null ? null : _sc.Graphs.Find(g => g.Id == s_graph) ?? _sc.Graphs.FirstOrDefault();
    public string SelectedGraphNode { get; private set; } = "";
    public List<GraphProblem> GraphProblems { get; private set; } = new();
    /// <summary>The triggers of the last «пробная компиляция» (null — not run).</summary>
    public List<TriggerDef>? GraphDryRun { get; private set; }

    private void BuildGraphCanvas(Control center)
    {
        _graphCanvas = new RuleGraphCanvas { Name = "RuleGraphCanvas", Visible = false };
        _graphCanvas.IsBreakpoint = id => _sc != null && OpenGraph is { } g && GraphBreakpoints.Has(_sc.Id, g.Id, id);
        _graphCanvas.Changed += GraphEdited;
        _graphCanvas.NodeChosen += id =>
        {
            if (id == SelectedGraphNode) return;
            SelectedGraphNode = id;
            RefreshProps();
        };
        _graphCanvas.Rejected += why => _status.Text = "Связь отклонена: " + why;
        var wide = new Button { Text = "⛶", TooltipText = "Развернуть холст (скрыть палитру и свойства) / вернуть", FocusMode = FocusModeEnum.None, Name = "GraphWide" };
        wide.Pressed += () => SetGraphWide(!GraphWide);
        _graphCanvas.GetMenuHBox().AddChild(wide);
        center.AddChild(_graphCanvas);
    }

    public void SetGraphWide(bool on)
    {
        GraphWide = on && Mode == TdMode.Graph;
        if (_libScroll != null && IsInstanceValid(_libScroll)) _libScroll.Visible = !GraphWide;
        if (_propScroll != null && IsInstanceValid(_propScroll)) _propScroll.Visible = !GraphWide;
        if (IsInstanceValid(_graphCanvas)) Callable.From(_graphCanvas.FitView).CallDeferred();
    }

    private void ShowGraphCanvas(bool on)
    {
        if (!IsInstanceValid(_graphCanvas)) return;
        _view.Visible = !on;
        _graphCanvas.Visible = on;
        if (!on && GraphWide) SetGraphWide(false);
        if (on) RefreshGraphCanvas();
    }

    private string GraphHint() => OpenGraph == null
        ? "У сцены нет графа событий: «+ Новый граф» или «Пример (ТЗ 7.7)» слева."
        : "Клик по пункту палитры — узел в центр холста; протяжка от выхода к входу — связь (поток → поток, условие → «если»/«условия»); " +
          "конец связи, утянутый от входа, — разрыв; Delete — удалить, Ctrl+D — дублировать, Ctrl+Z — отменить.";

    public void OpenGraphById(string id)
    {
        s_graph = id;
        SelectedGraphNode = "";
        GraphDryRun = null;
        RefreshLibrary();
        RefreshProps();
        RefreshGraphCanvas();
        Hint();
    }

    /// <summary>Redraws the canvas with the problems and the found nodes of the open graph.</summary>
    public void RefreshGraphCanvas()
    {
        if (!IsInstanceValid(_graphCanvas) || _sc == null) return;
        var g = OpenGraph;
        GraphProblems = g != null ? RuleGraphs.Check(_sc, g) : new();
        _graphCanvas.Problems.Clear();
        foreach (var p in GraphProblems.Where(p => p.Node != ""))
            _graphCanvas.Problems[p.Node] = p.Error || _graphCanvas.Problems.GetValueOrDefault(p.Node);
        _graphCanvas.Found.Clear();
        if (g != null && _usesFilter != "")
            foreach (var n in RuleGraphs.Uses(g, _usesFilter))
                _graphCanvas.Found.Add(n.Id);
        if (_graphCanvas.Graph != g) _graphCanvas.Show(g);
        else _graphCanvas.Rebuild();
    }

    /// <summary>The canvas changed the graph: one operation of the history; the canvas redraws after the gesture.</summary>
    private void GraphEdited()
    {
        GraphDryRun = null;
        Edited();
        Callable.From(() =>
        {
            RefreshGraphCanvas();
            if (OpenGraph?.Node(SelectedGraphNode) == null) SelectedGraphNode = "";
            RefreshProps();
        }).CallDeferred();
    }

    // ------------------------------------------------------------------ library: graphs, palette, uses

    private void BuildGraphLibrary(Func<string, Action, string, string, Button> btn)
    {
        var sc = _sc!;
        _library.AddChild(Ui.Label($"Графы сцены ({sc.Graphs.Count}):", 13, Ui.TextDim));
        var open = OpenGraph;
        foreach (var g in sc.Graphs)
        {
            var gg = g;
            var b = btn((g.Enabled ? "" : "⏸ ") + $"{g.DisplayName} [{g.Id}]", () => OpenGraphById(gg.Id), g.Enabled ? "" : "Выключен: в бой не попадает", "TdGraph_" + g.Id);
            b.ToggleMode = true;
            b.ButtonPressed = g == open;
        }
        btn("+ Новый граф", () =>
        {
            int k = sc.Graphs.Count + 1;
            while (sc.Graphs.Any(x => x.Id == "graph" + k)) k++;
            var g = new RuleGraphDef { Id = "graph" + k, Name = "Граф событий " + k };
            sc.Graphs.Add(g);
            Edited();
            OpenGraphById(g.Id);
        }, "Пустой граф событий сцены", "TdGraphNew");
        btn("+ Пример (ТЗ 7.7)", () =>
        {
            var g = RuleGraphs.Example(sc);
            sc.Graphs.Add(g);
            Edited();
            OpenGraphById(g.Id);
            _status.Text = "Пример: смерть босса до 120 с — элитная волна, иначе обычная. Проверьте метку босса и номера волн в свойствах узлов.";
        }, "Событие «юнит погиб» → «не прошло 120 с» → да: волна 1, нет: волна 2", "TdGraphExample");
        if (open != null)
            btn("✗ Удалить граф", () => Dialogs.Confirm(this, "Граф событий", $"Удалить граф «{open.DisplayName}»? Отменить можно Ctrl+Z.", () =>
            {
                sc.Graphs.Remove(open);
                Edited();
                OpenGraphById(sc.Graphs.FirstOrDefault()?.Id ?? "");
            }, "Удалить"), "", "TdGraphDelete");

        if (open == null) return;
        _library.AddChild(Ui.Sep());
        _library.AddChild(Ui.Label("Палитра:", 13, Ui.TextDim));
        var search = new LineEdit { Name = "TdGraphPaletteSearch", PlaceholderText = "поиск узла…", Text = _paletteFilter, ClearButtonEnabled = true };
        search.TextChanged += t =>
        {
            _paletteFilter = t;
            FillPalette();
        };
        _library.AddChild(search);
        _paletteBox = Ui.VBox(2);
        _paletteBox.Name = "TdGraphPalette";
        _library.AddChild(_paletteBox);
        FillPalette();

        _library.AddChild(Ui.Sep());
        _library.AddChild(Ui.Label("Найти использование:", 13, Ui.TextDim));
        var uses = new LineEdit { Name = "TdGraphUses", PlaceholderText = "ID объекта, метка, флаг, сигнал…", Text = _usesFilter, ClearButtonEnabled = true };
        uses.TextChanged += t =>
        {
            _usesFilter = t.Trim();
            FillUses();
            RefreshGraphCanvas();
        };
        _library.AddChild(uses);
        _usesBox = Ui.VBox(2);
        _usesBox.Name = "TdGraphUsesList";
        _library.AddChild(_usesBox);
        FillUses();
    }

    private void FillPalette()
    {
        if (_paletteBox == null || !IsInstanceValid(_paletteBox)) return;
        Ui.ClearChildren(_paletteBox);
        string group = "";
        foreach (var item in RuleGraphs.Palette(_paletteFilter))
        {
            if (item.Group != group)
            {
                group = item.Group;
                _paletteBox.AddChild(Ui.Label(group, 12, item.Available ? Ui.Accent : Ui.TextDim));
            }
            var it = item;
            var b = Ui.Button((it.Available ? "" : "⊘ ") + it.Name, () => AddGraphNode(it),
                it.Available ? it.Description : "Недоступно: " + it.Why);
            b.Alignment = HorizontalAlignment.Left;
            b.ClipText = true;
            b.Disabled = !it.Available;
            b.Name = "TdPalette_" + it.Group + "_" + it.Name.Replace(' ', '_').Replace(':', '_').Replace('.', '_').Replace('/', '_');
            _paletteBox.AddChild(b);
        }
    }

    private void FillUses()
    {
        if (_usesBox == null || !IsInstanceValid(_usesBox) || _sc == null) return;
        Ui.ClearChildren(_usesBox);
        if (_usesFilter == "") return;
        var found = RuleGraphs.UsesIn(_sc, _usesFilter);
        _usesBox.AddChild(Ui.Label(found.Count == 0 ? "Не используется" : $"Найдено: {found.Count}", 12, Ui.TextDim));
        foreach (var (g, n) in found.Take(40))
        {
            var gg = g;
            var nn = n;
            var b = Ui.Button($"{g.Id}: {n.Id} — {RuleGraphs.Title(n)}", () =>
            {
                if (OpenGraph != gg) OpenGraphById(gg.Id);
                _graphCanvas.Focus(nn.Id);
            });
            b.Alignment = HorizontalAlignment.Left;
            b.ClipText = true;
            _usesBox.AddChild(b);
        }
    }

    /// <summary>Adds a node of the palette at the middle of the visible canvas and selects it.</summary>
    public RuleNodeDef? AddGraphNode(GraphPaletteItem item)
    {
        if (OpenGraph is not { } g || item.Create == null) return null;
        // The middle of what is seen, moved down past the nodes already there.
        var at = _graphCanvas.VisibleCenter;
        for (int i = 0; i < 40 && g.Nodes.Any(x => Math.Abs(x.X - at.X) < 200 && Math.Abs(x.Y - at.Y) < 110); i++) at.Y += 120;
        var n = RuleGraphs.AddNode(g, item.Create(), MathF.Round(at.X), MathF.Round(at.Y));
        SelectedGraphNode = n.Id;
        GraphDryRun = null;
        Edited();
        RefreshGraphCanvas();
        _graphCanvas.Focus(n.Id);
        RefreshProps();
        _status.Text = $"Добавлен узел «{RuleGraphs.Title(n)}» [{n.Id}]";
        return n;
    }

    // ------------------------------------------------------------------ properties

    private void BuildGraphProps()
    {
        var sc = _sc!;
        var g = OpenGraph;
        if (g == null)
        {
            _props.AddChild(Ui.Label("Граф событий", 15, Ui.Accent));
            _props.AddChild(Ui.Label("Визуальные правила поверх триггеров: событие → условия → действия. Граф хранится в сценарии и при запуске боя " +
                                     "становится обычными триггерами — второй «машины сценария» нет.", 12, Ui.TextDim, wrap: true));
            return;
        }
        if (g.Node(SelectedGraphNode) is { } n)
        {
            BuildNodeProps(sc, g, n);
            return;
        }
        _props.AddChild(Ui.Label($"Граф «{g.DisplayName}» [{g.Id}] · узлов {g.Nodes.Count} · связей {g.Links.Count}", 15, Ui.Accent, wrap: true));
        _props.AddChild(Grid(g, () => RefreshLibrary(), p =>
        {
            if (p == nameof(RuleGraphDef.Id)) s_graph = g.Id;
        }, preview: false));

        int errors = GraphProblems.Count(p => p.Error), warnings = GraphProblems.Count - errors;
        var head = Ui.Label(GraphProblems.Count == 0 ? "✓ Граф без ошибок" : $"Ошибок: {errors}, предупреждений: {warnings}" + (errors > 0 ? " — граф не попадёт в бой" : ""),
            13, GraphProblems.Count == 0 ? Ui.Good : errors > 0 ? Ui.Bad : Ui.Warn, wrap: true);
        head.Name = "TdGraphCheck";
        _props.AddChild(head);
        var probs = Ui.VBox(2);
        probs.Name = "TdGraphProblems";
        foreach (var p in GraphProblems.Take(30))
        {
            var pp = p;
            var b = Ui.Button((p.Error ? "⛔ " : "⚠ ") + (p.Node != "" ? p.Node + ": " : "") + p.Message, () =>
            {
                if (pp.Node != "") _graphCanvas.Focus(pp.Node);
            }, p.Message);
            b.Alignment = HorizontalAlignment.Left;
            b.ClipText = true;
            probs.AddChild(b);
        }
        _props.AddChild(probs);

        var dry = Ui.Button("⚙ Пробная компиляция", () =>
        {
            GraphDryRun = RuleGraphs.CompileOne(sc, g);
            RefreshProps();
        }, "Показать триггеры, которыми граф станет в бою (без запуска боя)");
        dry.Name = "TdGraphDryRunButton";
        _props.AddChild(dry);
        if (GraphDryRun != null)
        {
            var list = Ui.VBox(2);
            list.Name = "TdGraphDryRun";
            list.AddChild(Ui.Label(GraphDryRun.Count == 0 ? "Граф с ошибками не компилируется — бой пойдёт без него." : $"Триггеров: {GraphDryRun.Count}", 12,
                GraphDryRun.Count == 0 ? Ui.Bad : Ui.TextDim, wrap: true));
            foreach (var t in GraphDryRun)
            {
                string when = t.ActivationOnly ? "только запуском" : EdUtil.EnumLabel(t.Event) + (t.Event == ScenarioEventType.Timer ? $" {t.Interval:0.##} с" : "");
                string limits = (t.Once ? " · однократно" : "") + (t.MaxFires > 0 ? $" · не более {t.MaxFires}" : "") + (t.Cooldown > 0 ? $" · не чаще {t.Cooldown:0.##} с" : "") +
                                (t.CheckOnActivate ? " · условие: " + t.Condition.Describe() : "");
                list.AddChild(Ui.Label($"{t.Name} — {when}{limits}", 12, Ui.Text, wrap: true));
                foreach (var a in t.Actions)
                    list.AddChild(Ui.Label("    → " + a.Describe(), 12, Ui.TextDim, wrap: true));
            }
            _props.AddChild(list);
        }

        int bps = GraphBreakpoints.CountIn(sc.Id);
        if (bps > 0)
        {
            var clear = Ui.Button($"Снять точки останова ({bps})", () =>
            {
                GraphBreakpoints.ClearIn(sc.Id);
                RefreshGraphCanvas();
                RefreshProps();
            });
            clear.Name = "TdGraphClearBreakpoints";
            _props.AddChild(clear);
        }
        _props.AddChild(Ui.Sep());
        _props.AddChild(Ui.Label(
            "Как это работает в бою. Источник — триггер на своё событие; события обрабатываются в тике симуляции в порядке возникновения, " +
            "триггеры одного события — по порядку ID узлов, связи одного выхода — по порядку создания. «Однократно», «Не чаще», " +
            "«Ограниченный повтор» — пределы запусков; «Задержка» переносит продолжение на N с. Петля без «Задержки» или «Ограниченного " +
            "повтора» — ошибка проверки. Состояние «Однократно», ворот и отложенных запусков — часть сохранения боя: загрузка не запускает " +
            "выполненное заново.", 12, Ui.TextDim, wrap: true));
    }

    private void BuildNodeProps(ScenarioDef sc, RuleGraphDef g, RuleNodeDef n)
    {
        _props.AddChild(Ui.Label($"Узел {n.Id} · {EdUtil.EnumLabel(n.Kind)}", 15, Ui.Accent, wrap: true));
        var back = Ui.Button("← К графу", () =>
        {
            SelectedGraphNode = "";
            RefreshProps();
        });
        _props.AddChild(back);
        if (EdUtil.EnumDescription(n.Kind) is { Length: > 0 } d) _props.AddChild(Ui.Label(d, 12, Ui.TextDim, wrap: true));
        string Ports(IReadOnlyList<RuleGraphs.Port> ports) => ports.Count == 0 ? "—" :
            string.Join(", ", ports.Select(p => $"{p.Caption} ({(p.Type == RulePortType.Flow ? "поток" : "условие")}{(p.Many ? "" : ", одна связь")})"));
        _props.AddChild(Ui.Label($"Входы: {Ports(RuleGraphs.Inputs(n.Kind))}\nВыходы: {Ports(RuleGraphs.Outputs(n.Kind))} · версия узла {n.NodeVersion}", 12, Ui.TextDim, wrap: true));

        var rename = Ui.HBox(4);
        var id = new LineEdit { Text = n.Id, Name = "TdGraphNodeId" };
        id.Expand();
        rename.AddChild(Ui.Label("ID:", 13, Ui.TextDim));
        rename.AddChild(id);
        var ok = Ui.Button("Переименовать", () =>
        {
            string from = n.Id, to = id.Text.Trim();
            if (!RuleGraphs.RenameNode(g, from, to))
            {
                _status.Text = to == from ? "ID не изменился" : $"Нельзя переименовать в «{to}»: занято, пусто или есть «:» / «/»";
                return;
            }
            if (GraphBreakpoints.Has(sc.Id, g.Id, from))
            {
                GraphBreakpoints.Toggle(sc.Id, g.Id, from);
                GraphBreakpoints.Toggle(sc.Id, g.Id, to);
            }
            SelectedGraphNode = to;
            GraphEdited();
        });
        rename.AddChild(ok);
        _props.AddChild(rename);

        var tools = new HFlowContainer();
        bool bp = GraphBreakpoints.Has(sc.Id, g.Id, n.Id);
        var brk = Ui.Button(bp ? "○ Снять точку останова" : "● Точка останова", () =>
        {
            GraphBreakpoints.Toggle(sc.Id, g.Id, n.Id);
            RefreshGraphCanvas();
            RefreshProps();
        }, "Тест сцены из редактора встаёт на паузу, когда узел срабатывает (в обычной игре точек останова нет)");
        brk.Name = "TdGraphBreakpoint";
        tools.AddChild(brk);
        tools.AddChild(Ui.Button("⧉ Дублировать", () =>
        {
            _graphCanvas.Focus(n.Id);
            _graphCanvas.DuplicateSelected();
        }, "Ctrl+D"));
        var del = Ui.Button("✗ Удалить", () => _graphCanvas.DeleteNodes(new[] { n.Id }), "Delete");
        del.Name = "TdGraphDeleteNode";
        tools.AddChild(del);
        _props.AddChild(tools);

        foreach (var p in GraphProblems.Where(p => p.Node == n.Id))
            _props.AddChild(Ui.Label((p.Error ? "⛔ " : "⚠ ") + p.Message, 12, p.Error ? Ui.Bad : Ui.Warn, wrap: true));
        var grid = Grid(n, () => Callable.From(RefreshGraphCanvas).CallDeferred(), null, false, nameof(RuleNodeDef.Id));
        grid.Name = "TdGraphNodeGrid";
        _props.AddChild(grid);
    }
}
