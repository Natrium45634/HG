using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using HexTactics.Game.Render;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

public enum MapTool
{
    Paint,
    Raise,
    Lower,
    SetHeight,
    Fill,
    Line,
    AddCell,
    RemoveCell,
    Select,
    Marker,
}

/// <summary>
/// Map editor (ТЗ 16.1): new maps of any shape, add/remove cells, terrain brush, heights, fill,
/// lines, per-cell overrides with mass editing, markers (starts, spawns, objectives, triggers),
/// map properties, static checks (unreachable starts/objectives, locked areas, broken refs) and a
/// preview in game mode.
/// </summary>
public partial class MapEditorPanel : DefListPanel
{
    private MapDef? _map;
    private BattleMap? _battleMap;
    private BoardViewport _view = null!;
    private MapTool _tool = MapTool.Paint;
    private readonly List<Button> _toolButtons = new();
    private OptionButton _terrain = null!;
    private SpinBox _brush = null!;
    private SpinBox _heightValue = null!;
    private OptionButton _markerType = null!;
    private Label _status = null!;
    private TabContainer _tabs = null!;
    private VBoxContainer _cellTab = null!;
    private VBoxContainer _markerTab = null!;
    private VBoxContainer _propsTab = null!;
    private VBoxContainer _checkTab = null!;
    private readonly HashSet<HexCoord> _selection = new();
    private readonly HashSet<HexCoord> _problemCells = new();
    private HexCoord? _lineStart;
    private MapMarkerDef? _selectedMarker;
    private bool _rebuildPending;
    private bool _structureChanged;
    private float _rebuildTimer;
    private readonly HashSet<HexCoord> _strokeCells = new();
    private readonly Action<MapDef> _preview;

    public MapEditorPanel(EditorContext ctx, Action<MapDef> preview) : base(ctx, DefCategory.Map, "Карты", null, null)
    {
        _preview = preview;
    }

    protected override bool UseScroll => false;

    /// <summary>«Создать» — the first template (standard 10 × 8); other templates: «Новая карта…».</summary>
    protected override Def CreateNew()
    {
        var t = MapTemplates.Ordered(Ctx.Db).FirstOrDefault() ?? MapTemplates.CreateBuiltIn(MapTemplates.StandardId, Ctx.Db)!;
        var m = MapTemplates.CreateMap(t, "", Ctx.Db, "Новая карта");
        return m;
    }

    protected override Control BuildDetail(Def d)
    {
        _map = (MapDef)d;
        _selection.Clear();
        _problemCells.Clear();
        _selectedMarker = null;
        _lineStart = null;

        var root = Ui.HBox(8).Expand(true, true);
        var left = Ui.VBox(6).Expand(true, true);

        // Toolbar.
        var bar = new HFlowContainer();
        bar.AddThemeConstantOverride("h_separation", 4);
        (MapTool, string, string)[] tools =
        {
            (MapTool.Paint, "Кисть", "Рисовать местность (ЛКМ, можно вести)"),
            (MapTool.Raise, "Выше", "Поднять высоту"),
            (MapTool.Lower, "Ниже", "Опустить высоту"),
            (MapTool.SetHeight, "Высота =", "Задать высоту"),
            (MapTool.Fill, "Заливка", "Залить связную область той же местности"),
            (MapTool.Line, "Линия", "Два клика — линия выбранной местностью"),
            (MapTool.AddCell, "+ клетка", "Добавить клетки (карта неправильной формы)"),
            (MapTool.RemoveCell, "− клетка", "Удалить клетки"),
            (MapTool.Select, "Выделение", "ЛКМ — выделить (Shift — добавить), правка свойств у всех выделенных"),
            (MapTool.Marker, "Метки", "ЛКМ — поставить метку, ПКМ — удалить"),
        };
        foreach (var (t, label, tip) in tools)
        {
            var tt = t;
            var b = Ui.Button(label, () => SetTool(tt), tip);
            b.ToggleMode = true;
            b.ButtonPressed = t == _tool;
            _toolButtons.Add(b);
            bar.AddChild(b);
        }
        left.AddChild(bar);

        var opts = new HFlowContainer();
        opts.AddThemeConstantOverride("h_separation", 6);
        opts.AddChild(Ui.Label("Местность:", 13, Ui.TextDim));
        _terrain = new OptionButton { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(180, 0) };
        foreach (var t in Ctx.Db.All<TerrainDef>()) _terrain.AddItem($"{t.DisplayName} [{t.Id}]");
        opts.AddChild(_terrain);
        opts.AddChild(Ui.Label("Кисть:", 13, Ui.TextDim));
        _brush = new SpinBox { MinValue = 0, MaxValue = 4, Value = 0, CustomMinimumSize = new Vector2(64, 0), TooltipText = "Радиус кисти" };
        opts.AddChild(_brush);
        opts.AddChild(Ui.Label("Высота:", 13, Ui.TextDim));
        _heightValue = new SpinBox { MinValue = -5, MaxValue = 20, Value = 1, CustomMinimumSize = new Vector2(64, 0) };
        opts.AddChild(_heightValue);
        opts.AddChild(Ui.Label("Метка:", 13, Ui.TextDim));
        _markerType = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (var n in Enum.GetNames(typeof(MarkerType))) _markerType.AddItem(Names.Of(typeof(MarkerType), n));
        opts.AddChild(_markerType);
        opts.AddChild(Ui.Spacer(8));
        opts.AddChild(Ui.Button("Новая карта…", NewMapDialog, "Из шаблона (стандарт 10 × 8 — первый) или произвольного размера"));
        opts.AddChild(Ui.Button("Сохранить как шаблон…", SaveAsTemplateDialog, "Сохранить эту карту как шаблон с выбором частей"));
        opts.AddChild(Ui.Button("⤢", () => _view.Board.FitCamera(), "Вписать карту в окно", 36));
        opts.AddChild(Ui.Button("1:1", () => _view.Board.Rig.ResetStandardZoom(), "Стандартный масштаб (карта 10 × 8)", 40));
        opts.AddChild(Ui.Button("Проверить", RunCheck));
        opts.AddChild(Ui.Button("▶ Просмотр в игре", () =>
        {
            Ctx.Validate();
            _preview(_map!);
        }, "Песочница на этой карте (черновик, без сохранения)"));
        left.AddChild(opts);

        _view = new BoardViewport();
        _view.Clicked += OnClick;
        _view.Dragged += (hex, cell, mask) => ApplyTool(hex, cell, (mask & MouseButtonMask.Right) != 0, true);
        _view.Hovered += (hex, cell) => UpdateStatus(hex, cell);
        left.AddChild(_view);
        _status = Ui.Label("", 13, Ui.TextDim);
        _status.ClipText = true;
        _status.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _status.CustomMinimumSize = new Vector2(10, 0);
        left.AddChild(_status);
        root.AddChild(left);

        _tabs = new TabContainer { CustomMinimumSize = new Vector2(370, 0), SizeFlagsVertical = SizeFlags.ExpandFill };
        _cellTab = Tab("Клетки");
        _markerTab = Tab("Метки");
        _propsTab = Tab("Свойства карты");
        _checkTab = Tab("Проверка");
        root.AddChild(_tabs);

        Callable.From(() =>
        {
            RebuildMap(true);
            RefreshCellTab();
            RefreshMarkerTab();
            RefreshProps();
            RunCheck();
        }).CallDeferred();
        return root;
    }

    private VBoxContainer Tab(string name)
    {
        var scroll = new ScrollContainer { Name = name, HorizontalScrollMode = ScrollContainer.ScrollMode.Auto };
        var v = Ui.VBox(6).Expand();
        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_right", 12);
        pad.AddChild(v);
        scroll.AddChild(pad.Expand());
        _tabs.AddChild(scroll);
        return v;
    }

    private void SetTool(MapTool t)
    {
        _tool = t;
        _lineStart = null;
        var all = Enum.GetValues<MapTool>();
        for (int i = 0; i < _toolButtons.Count; i++) _toolButtons[i].ButtonPressed = all[i] == t;
        if (t == MapTool.Marker) _tabs.CurrentTab = 1;
        if (t == MapTool.Select) _tabs.CurrentTab = 0;
    }

    private string SelectedTerrain()
    {
        var ids = Ctx.Db.Ids(DefCategory.Terrain).ToList();
        return _terrain.Selected >= 0 && _terrain.Selected < ids.Count ? ids[_terrain.Selected] : ids.FirstOrDefault() ?? "grass";
    }

    private MapCellDef? CellDef(HexCoord c) => _map?.Cells.FirstOrDefault(x => x.C == c);

    private void RebuildMap(bool frame)
    {
        if (_map == null || !IsInstanceValid(_view)) return;
        var issues = new List<ContentIssue>();
        _battleMap = BattleMap.Build(_map, Ctx.Db, issues);
        _view.Bind(_battleMap, Ctx.Db, frame);
        _rebuildPending = false;
        _structureChanged = false;
    }

    private void MarkChanged(bool structure = false)
    {
        _rebuildPending = true;
        _structureChanged |= structure;
        if (_map != null) OnEdited(_map);
    }

    public override void _Process(double delta)
    {
        if (_map == null || !IsInstanceValid(_view) || _battleMap == null) return;
        _rebuildTimer -= (float)delta;
        if (_rebuildPending && _rebuildTimer <= 0)
        {
            _rebuildTimer = 0.12f;
            RebuildMap(false);
        }
        DrawOverlay();
    }

    private void DrawOverlay()
    {
        var o = _view.Board.Overlay;
        o.Clear();
        _view.Labels.Begin();
        foreach (var c in _selection) o.RingHex(c, new Color(0.4f, 0.8f, 1f), 0.09f);
        foreach (var c in _problemCells) o.HatchHex(c, new Color(1f, 0.3f, 0.25f, 0.6f));
        if (_map != null)
        {
            foreach (var m in _map.Markers)
            {
                var col = m.Type switch
                {
                    MarkerType.PlayerStart => new Color(0.35f, 0.65f, 1f),
                    MarkerType.EnemySpawn => new Color(1f, 0.4f, 0.35f),
                    MarkerType.NeutralSpawn => new Color(0.95f, 0.8f, 0.35f),
                    MarkerType.Objective => new Color(1f, 0.85f, 0.2f),
                    MarkerType.Exit => new Color(0.4f, 1f, 0.5f),
                    _ => new Color(0.85f, 0.5f, 1f),
                };
                foreach (var c in HexCoord.Spiral(m.C, m.Radius)) o.FillHex(c, new Color(col.R, col.G, col.B, 0.22f), 0.85f);
                o.DashedRing(m.C, col);
                if (m == _selectedMarker) o.RingHex(m.C, Colors.White, 0.1f, 1f);
                var cell = _battleMap?.Get(m.C);
                _view.Labels.Add(Render.Board.Center(m.C, (cell == null ? 0 : Render.Board.SurfaceY(cell)) + 0.6f),
                    (string.IsNullOrEmpty(m.Label) ? Names.Of(m.Type) : m.Label), col, 26);
            }
        }
        if (_view.HoverHex is HexCoord h)
        {
            int r = _tool is MapTool.Paint or MapTool.Raise or MapTool.Lower or MapTool.SetHeight or MapTool.AddCell or MapTool.RemoveCell ? (int)_brush.Value : 0;
            foreach (var c in HexCoord.Spiral(h, r))
            {
                if (_battleMap?.Get(c) != null) o.RingHex(c, new Color(1, 1, 1, 0.8f), 0.06f);
                else if (_tool == MapTool.AddCell) o.FillHex(c, new Color(0.4f, 1f, 0.5f, 0.35f), 0.9f, 0.02f);
            }
            if (_lineStart is HexCoord ls)
                foreach (var c in HexCoord.Line(ls, h))
                    o.FillHex(c, new Color(1f, 0.85f, 0.35f, 0.35f));
        }
        // Empty slots are invisible for picking; show a ghost grid around the map for "+ клетка".
        if (_tool == MapTool.AddCell && _battleMap != null)
        {
            var ring = new HashSet<HexCoord>();
            foreach (var c in _battleMap.Cells)
                foreach (var n in c.Coord.Neighbors())
                    if (!_battleMap.Contains(n))
                        ring.Add(n);
            foreach (var c in ring) o.RingHex(c, new Color(0.4f, 1f, 0.5f, 0.35f), 0.04f, 0.95f, 0.0f);
        }
        o.Commit();
        _view.Labels.End();
    }

    private void UpdateStatus(HexCoord? hex, Cell? cell)
    {
        if (_map == null) return;
        string s = $"Клеток: {_map.Cells.Count} · меток: {_map.Markers.Count} · выделено: {_selection.Count}";
        if (cell != null) s += $" · {cell.Coord}: {cell.Terrain.DisplayName}, высота {cell.Height}" + (cell.Effects.Count > 0 ? $", эффекты: {string.Join(", ", cell.Effects.Select(e => e.Def.DisplayName))}" : "");
        else if (hex != null) s += $" · {hex}: пусто";
        s += "   |   ПКМ/колесо/СКМ — камера; Q/E — поворот";
        _status.Text = s;
    }

    private void OnClick(HexCoord? hex, Cell? cell, InputEventMouseButton mb)
    {
        if (!mb.Pressed)
        {
            _strokeCells.Clear();
            if (_rebuildPending) RebuildMap(false);
            return;
        }
        _strokeCells.Clear();
        if (_tool == MapTool.Select)
        {
            if (hex == null || cell == null)
            {
                if (!mb.ShiftPressed) _selection.Clear();
            }
            else
            {
                if (!mb.ShiftPressed && !mb.CtrlPressed) _selection.Clear();
                if (_selection.Contains(hex.Value) && (mb.ShiftPressed || mb.CtrlPressed)) _selection.Remove(hex.Value);
                else _selection.Add(hex.Value);
            }
            RefreshCellTab();
            return;
        }
        if (_tool == MapTool.Marker)
        {
            if (hex == null || _map == null) return;
            var existing = _map.Markers.FirstOrDefault(m => m.C == hex.Value);
            if (mb.ButtonIndex == MouseButton.Right)
            {
                if (existing != null) _map.Markers.Remove(existing);
                _selectedMarker = null;
            }
            else if (existing != null)
            {
                _selectedMarker = existing;
            }
            else if (cell != null)
            {
                var type = (MarkerType)_markerType.Selected;
                var m = new MapMarkerDef { Type = type, C = hex.Value, Id = UniqueMarkerId(type), Label = Names.Of(type) };
                _map.Markers.Add(m);
                _selectedMarker = m;
            }
            MarkChanged();
            RefreshMarkerTab();
            return;
        }
        if (_tool == MapTool.Line)
        {
            if (hex == null) return;
            if (_lineStart == null)
            {
                _lineStart = hex;
                return;
            }
            foreach (var c in HexCoord.Line(_lineStart.Value, hex.Value))
            {
                var cd = CellDef(c);
                if (cd == null) continue;
                cd.T = SelectedTerrain();
                cd.H = Ctx.Db.Terrain(cd.T)?.DefaultHeight ?? cd.H;
            }
            _lineStart = null;
            MarkChanged();
            return;
        }
        ApplyTool(hex, cell, mb.ButtonIndex == MouseButton.Right, false);
    }

    private string UniqueMarkerId(MarkerType t)
    {
        string b = t switch { MarkerType.PlayerStart => "start", MarkerType.EnemySpawn => "spawn", MarkerType.NeutralSpawn => "neutral", MarkerType.Objective => "goal", MarkerType.Exit => "exit", _ => "trigger" };
        for (int i = 1; ; i++)
            if (_map!.FindMarker(b + i) == null)
                return b + i;
    }

    private void ApplyTool(HexCoord? hex, Cell? cell, bool alt, bool dragging)
    {
        if (_map == null || hex == null) return;
        if (_tool is MapTool.Select or MapTool.Marker or MapTool.Line) return;
        if (dragging && _tool == MapTool.Fill) return;
        int r = (int)_brush.Value;
        var targets = _tool == MapTool.Fill ? FloodSame(hex.Value) : HexCoord.Spiral(hex.Value, r);
        bool structure = false;
        foreach (var c in targets)
        {
            // While dragging, apply height changes only once per stroke and cell.
            if (dragging && _tool is MapTool.Raise or MapTool.Lower && !_strokeCells.Add(c)) continue;
            if (!dragging) _strokeCells.Add(c);
            var cd = CellDef(c);
            switch (_tool)
            {
                case MapTool.Paint:
                case MapTool.Fill:
                    if (cd == null) continue;
                    if (alt)
                    {
                        _terrain.Selected = Math.Max(0, Ctx.Db.Ids(DefCategory.Terrain).ToList().IndexOf(cd.T));
                        return;
                    }
                    var tid = SelectedTerrain();
                    if (cd.T != tid)
                    {
                        cd.T = tid;
                        var td = Ctx.Db.Terrain(tid);
                        if (td != null && td.DefaultHeight != 0) cd.H = td.DefaultHeight;
                    }
                    break;
                case MapTool.Raise:
                    if (cd != null) cd.H = Math.Min(20, cd.H + (alt ? -1 : 1));
                    break;
                case MapTool.Lower:
                    if (cd != null) cd.H = Math.Max(-5, cd.H - (alt ? -1 : 1));
                    break;
                case MapTool.SetHeight:
                    if (cd != null) cd.H = (int)_heightValue.Value;
                    break;
                case MapTool.AddCell:
                    if (alt)
                    {
                        if (cd != null) _map.Cells.Remove(cd);
                    }
                    else if (cd == null)
                    {
                        _map.Cells.Add(new MapCellDef { C = c, T = SelectedTerrain() });
                    }
                    structure = true;
                    break;
                case MapTool.RemoveCell:
                    if (cd != null) _map.Cells.Remove(cd);
                    _selection.Remove(c);
                    structure = true;
                    break;
            }
        }
        MarkChanged(structure);
    }

    private IEnumerable<HexCoord> FloodSame(HexCoord start)
    {
        var cells = _map!.Cells.ToDictionary(c => c.C);
        if (!cells.TryGetValue(start, out var s0)) yield break;
        var seen = new HashSet<HexCoord> { start };
        var q = new Queue<HexCoord>();
        q.Enqueue(start);
        while (q.Count > 0 && seen.Count < 5000)
        {
            var c = q.Dequeue();
            yield return c;
            foreach (var n in c.Neighbors())
                if (!seen.Contains(n) && cells.TryGetValue(n, out var nc) && nc.T == s0.T && nc.H == s0.H)
                {
                    seen.Add(n);
                    q.Enqueue(n);
                }
        }
    }

    // ------------------------------------------------------------------ side tabs

    private void RefreshCellTab()
    {
        if (!IsInstanceValid(_cellTab) || _map == null) return;
        Ui.ClearChildren(_cellTab);
        if (_selection.Count == 0)
        {
            _cellTab.AddChild(Ui.Label("Инструмент «Выделение»: ЛКМ по клетке (Shift — добавить к выделению). Свойства применяются ко всем выделенным клеткам.", 13, Ui.TextDim, wrap: true));
            return;
        }
        var first = CellDef(_selection.First());
        if (first == null)
        {
            _selection.Clear();
            return;
        }
        _cellTab.AddChild(Ui.Label(_selection.Count == 1 ? $"Клетка {first.C}" : $"Выделено клеток: {_selection.Count} (показаны значения первой)", 15, Ui.Accent));
        var proxy = first.Clone();
        var grid = new PropertyGrid { LabelWidth = 170 };
        grid.SetTarget(proxy, Ctx.Db);
        grid.PropertyChanged += (owner, propName) =>
        {
            if (owner != proxy) return;
            var prop = typeof(MapCellDef).GetProperty(propName);
            if (prop == null || propName == nameof(MapCellDef.C)) return;
            foreach (var c in _selection)
            {
                var cd = CellDef(c);
                if (cd == null) continue;
                var v = prop.GetValue(proxy);
                prop.SetValue(cd, v is List<string> l ? new List<string>(l) : v);
            }
            MarkChanged();
        };
        _cellTab.AddChild(grid);
        _cellTab.AddChild(Ui.Button("Снять выделение", () =>
        {
            _selection.Clear();
            RefreshCellTab();
        }));
    }

    private void RefreshMarkerTab()
    {
        if (!IsInstanceValid(_markerTab) || _map == null) return;
        Ui.ClearChildren(_markerTab);
        _markerTab.AddChild(Ui.Label("Инструмент «Метки»: тип выбирается на панели; ЛКМ — поставить/выбрать, ПКМ — удалить.", 13, Ui.TextDim, wrap: true));
        foreach (var m in _map.Markers)
        {
            var mm = m;
            var b = Ui.Button($"{Names.Of(m.Type)} «{m.Label}» [{m.Id}] {m.C}", () =>
            {
                _selectedMarker = mm;
                RefreshMarkerTab();
            });
            b.Alignment = HorizontalAlignment.Left;
            if (m == _selectedMarker) b.AddThemeColorOverride("font_color", Ui.Accent);
            _markerTab.AddChild(b);
        }
        if (_selectedMarker != null)
        {
            _markerTab.AddChild(Ui.Sep());
            var grid = new PropertyGrid { LabelWidth = 150 };
            grid.SetTarget(_selectedMarker, Ctx.Db);
            grid.Changed += () => MarkChanged();
            _markerTab.AddChild(grid);
            _markerTab.AddChild(Ui.Button("Удалить метку", () =>
            {
                _map.Markers.Remove(_selectedMarker);
                _selectedMarker = null;
                MarkChanged();
                RefreshMarkerTab();
            }));
        }
    }

    private void RefreshProps()
    {
        if (!IsInstanceValid(_propsTab) || _map == null) return;
        Ui.ClearChildren(_propsTab);
        var grid = new PropertyGrid { LabelWidth = 150 };
        grid.SetTarget(_map, Ctx.Db);
        grid.Changed += () => MarkChanged();
        _propsTab.AddChild(grid);
        _propsTab.AddChild(Ui.Label("Условия победы/поражения задаются в сценарии, использующем эту карту (раздел «Сценарии»).", 12, Ui.TextDim, wrap: true));
    }

    private void RunCheck()
    {
        if (!IsInstanceValid(_checkTab) || _map == null) return;
        Ui.ClearChildren(_checkTab);
        _problemCells.Clear();
        var issues = new List<ContentIssue>();
        var seen = new HashSet<HexCoord>();
        foreach (var c in _map.Cells)
        {
            if (!seen.Add(c.C)) issues.Add(new ContentIssue(IssueSeverity.Error, "", c.C.ToString(), "клетка указана дважды"));
            if (Ctx.Db.Terrain(c.T) == null)
            {
                issues.Add(new ContentIssue(IssueSeverity.Error, "", c.C.ToString(), $"неизвестная местность «{c.T}» (сломанная ссылка)"));
                _problemCells.Add(c.C);
            }
        }
        issues.AddRange(MapAnalysis.Analyze(Ctx.Db, _map));
        foreach (var i in Ctx.IssuesOf(_map)) if (!issues.Any(x => x.Message == i.Message)) issues.Add(i);
        foreach (var i in issues)
        {
            var col = i.Severity == IssueSeverity.Error ? Ui.Bad : Ui.Warn;
            _checkTab.AddChild(Ui.Label($"{(i.Severity == IssueSeverity.Error ? "✗" : "⚠")} {i.Path} {i.Message}", 13, col, wrap: true));
            foreach (var token in i.Message.Split(' ', '(', ')', ','))
                ParseCoord(token);
        }
        if (issues.Count == 0) _checkTab.AddChild(Ui.Label("✓ Проблем не найдено: стартовые точки и цели достижимы, запертых областей нет.", 13, Ui.Good, wrap: true));
        _checkTab.AddChild(Ui.Button("Проверить снова", RunCheck));
    }

    private void ParseCoord(string token)
    {
        // Messages contain coordinates like "(3,-2)" — highlight them on the board.
        var t = token.Trim('(', ')', ',', ' ');
        var parts = t.Split(',');
        if (parts.Length == 2 && int.TryParse(parts[0], out var q) && int.TryParse(parts[1], out var r)) _problemCells.Add(new HexCoord(q, r));
    }

    private void NewMapDialog()
    {
        var templates = MapTemplates.Ordered(Ctx.Db);
        var v = Ui.VBox(6);
        v.AddChild(Ui.Label("Шаблон новой карты. Карта — независимая копия: изменения шаблона её не меняют. Клетки потом можно добавлять и удалять.", 13, Ui.TextDim, wrap: true).MinSize(520));
        var list = new ItemList { CustomMinimumSize = new Vector2(520, 220) };
        foreach (var t in templates)
        {
            var (c, r) = t.Size();
            list.AddItem($"{t.DisplayName}   ({(t.Cells.Count == 0 ? "пусто" : $"{c} × {r}")}){(t.BuiltIn ? "" : "  · " + (string.IsNullOrEmpty(t.Group) ? MapTemplates.UserGroup : t.Group))}");
        }
        int customIndex = list.AddItem("Произвольный размер…");
        list.Select(0);
        v.AddChild(list);
        var info = Ui.Label("", 12, Ui.TextDim, wrap: true).MinSize(520);
        v.AddChild(info);
        var custom = Ui.HBox(6);
        var shape = new OptionButton();
        foreach (var n in Enum.GetNames(typeof(MapShape))) shape.AddItem(Names.Of(typeof(MapShape), n));
        var w = new SpinBox { MinValue = 1, MaxValue = 128, Value = 10, Prefix = "ширина/радиус" };
        var h = new SpinBox { MinValue = 1, MaxValue = 128, Value = 8, Prefix = "высота" };
        var terrain = new OptionButton();
        var ids = Ctx.Db.Ids(DefCategory.Terrain).ToList();
        foreach (var id in ids) terrain.AddItem(id);
        int fi = ids.IndexOf(MapTemplates.DefaultFloor);
        if (fi >= 0) terrain.Selected = fi;
        custom.AddChild(shape);
        custom.AddChild(w);
        custom.AddChild(h);
        custom.AddChild(terrain);
        custom.Visible = false;
        v.AddChild(custom);
        var name = new LineEdit { PlaceholderText = "Название карты", Text = "Новая карта" };
        v.AddChild(name);
        void Info(long idx)
        {
            custom.Visible = idx == customIndex;
            info.Text = idx >= 0 && idx < templates.Count ? $"{templates[(int)idx].Description} {templates[(int)idx].Purpose}" : "Форма и размер задаются вручную.";
        }
        list.ItemSelected += Info;
        Info(0);
        Dialogs.Custom(this, "Новая карта", v, "Создать", () =>
        {
            var sel = list.GetSelectedItems();
            int idx = sel.Length > 0 ? sel[0] : 0;
            var id = ContentIO.UniqueId(Ctx.Db, DefCategory.Map, "map");
            MapDef m;
            if (idx == customIndex)
            {
                string t = ids.Count > 0 ? ids[Math.Max(0, terrain.Selected)] : MapTemplates.DefaultFloor;
                m = MapTemplates.Generate(id, (MapShape)shape.Selected, (int)w.Value, (int)h.Value, t);
            }
            else
            {
                m = MapTemplates.CreateMap(templates[idx], id, Ctx.Db);
            }
            if (m.Cells.Count > Ctx.Db.Rules.Limits.MaxMapCells)
            {
                Dialogs.Info(this, "Слишком большая карта", $"Клеток {m.Cells.Count} > лимита {Ctx.Db.Rules.Limits.MaxMapCells}.");
                return false;
            }
            m.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : "Новая карта";
            Ctx.Db.Add(m);
            Ctx.MarkDirty();
            RefreshList();
            Select(m);
            return true;
        }, new Vector2I(560, 0));
    }

    private void SaveAsTemplateDialog()
    {
        if (_map == null) return;
        var map = _map;
        var v = Ui.VBox(6);
        v.AddChild(Ui.Label($"Шаблон из карты «{map.DisplayName}». Форма и размер сохраняются всегда; выберите остальные части:", 13, Ui.TextDim, wrap: true).MinSize(480));
        var parts = new TemplatePartsDef();
        var grid = new PropertyGrid { LabelWidth = 220 };
        grid.SetTarget(parts, Ctx.Db);
        v.AddChild(grid);
        var name = new LineEdit { Text = map.DisplayName, PlaceholderText = "Название шаблона" };
        var group = new LineEdit { Text = MapTemplates.UserGroup, PlaceholderText = "Категория" };
        var desc = new LineEdit { Text = map.Description, PlaceholderText = "Описание" };
        v.AddChild(Ui.Label("Название, категория, описание:", 13, Ui.TextDim));
        v.AddChild(name);
        v.AddChild(group);
        v.AddChild(desc);
        Dialogs.Custom(this, "Сохранить как шаблон", v, "Сохранить", () =>
        {
            var id = ContentIO.UniqueId(Ctx.Db, DefCategory.MapTemplate, "tpl_" + map.Id);
            var t = MapTemplates.FromMap(map, id, parts, Ctx.Db, name.Text.Trim().Length > 0 ? name.Text.Trim() : map.DisplayName);
            t.Group = group.Text.Trim();
            t.Description = desc.Text.Trim();
            Ctx.Db.Add(t);
            Ctx.MarkDirty();
            Dialogs.Info(this, "Шаблон сохранён", $"Шаблон «{t.DisplayName}» [{t.Id}] добавлен в черновик (раздел «Шаблоны карт»). Он попадёт в библиотеку при сохранении.");
            return true;
        }, new Vector2I(540, 0));
    }
}
