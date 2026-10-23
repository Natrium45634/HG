using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using HexTactics.Game.Render;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// Рабочее место «TD-сцена» (docs/DECISIONS_4.md §69): the map in the centre, the library of the mode on the left,
/// the properties of the selection on the right, the problems of the scene at the bottom, the modes on top. Each mode
/// shows only its tools: «Объекты» places, moves and shapes bases; «Маршруты» draws a route by dragging the left
/// button over hexes; «Волны» binds groups to spawn bases and routes. A right click on an object opens its menu.
/// </summary>
public partial class TdScenePanel : DefListPanel
{
    public enum TdMode { Objects, Routes, Waves }

    private static readonly ScenarioObjectType[] PlaceTypes =
    {
        ScenarioObjectType.PlayerBase, ScenarioObjectType.SpawnBase, ScenarioObjectType.Checkpoint,
        ScenarioObjectType.Defense, ScenarioObjectType.Goal, ScenarioObjectType.RoutePoint,
    };

    private ScenarioDef? _sc;
    private MapDef? _map;
    private BoardViewport _view = null!;
    private VBoxContainer _library = null!;
    private VBoxContainer _props = null!;
    private VBoxContainer _problemsBox = null!;
    private Label _status = null!;
    private PopupMenu _menu = null!;
    private readonly List<Button> _modeButtons = new();
    private readonly Action<ScenarioDef> _test;
    private Simulation? _preview;
    private string _mapId = "";
    private List<ScenarioProblem> _problems = new();
    private string _menuObject = "";

    // Route drawing: the line being drawn and the route it goes to (docs/DECISIONS_4.md §57).
    private RouteDrawing? _drawing;
    private RouteDef? _drawRoute;
    private List<ScenarioProblem> _liveProblems = new();

    // Moving an object by dragging it.
    private ScenarioObjectDef? _moving;
    private HexCoord _moveFrom;

    public TdMode Mode { get; private set; }
    /// <summary>The object type the click places (null — select and move).</summary>
    public ScenarioObjectType? PlaceType { get; private set; }
    public string SelectedObject { get; private set; } = "";
    public string SelectedRoute { get; private set; } = "";
    public WaveDef? SelectedWave { get; private set; }
    public SpawnGroupDef? SelectedGroup { get; private set; }
    public ScenarioDef? Scene => _sc;
    public IReadOnlyList<ScenarioProblem> Problems => _problems;
    public BoardViewport View => _view;
    public bool Drawing => _drawing != null;
    public string StatusText => IsInstanceValid(_status) ? _status.Text : "";

    public TdScenePanel(EditorContext ctx, Action<ScenarioDef> test) : base(ctx, DefCategory.Scenario, "TD-сцена", null, null)
    {
        _test = test;
    }

    protected override bool UseScroll => false;

    protected override Def CreateNew() => new ScenarioDef
    {
        Name = "Новая TD-сцена",
        Map = Ctx.Db.Ids(DefCategory.Map).FirstOrDefault() ?? "",
        Victory = new() { new ConditionDef { Type = ConditionType.AllEnemiesDead } },
        Defeat = new(),
    };

    // ------------------------------------------------------------------ layout

    protected override Control BuildDetail(Def d)
    {
        _sc = (ScenarioDef)d;
        _mapId = "";
        SelectedObject = SelectedRoute = "";
        SelectedWave = null;
        SelectedGroup = null;
        _drawing = null;
        _moving = null;
        var root = Ui.VBox(6).Expand(true, true);

        var bar = new HFlowContainer();
        bar.AddThemeConstantOverride("h_separation", 4);
        bar.AddChild(Ui.Label("Режим:", 13, Ui.TextDim));
        _modeButtons.Clear();
        foreach (var (m, title, tip) in new[]
                 {
                     (TdMode.Objects, "Объекты и базы", "Поставить, передвинуть, повернуть базы и объекты; Shift+ЛКМ — добавить/убрать гекс базы"),
                     (TdMode.Routes, "Маршруты", "Протяжка ЛКМ от базы спавна до цели рисует маршрут"),
                     (TdMode.Waves, "Волны", "Группы волн: шаблон, количество, база спавна, маршрут"),
                 })
        {
            var mode = m;
            var b = Ui.Button(title, () => SetMode(mode), tip);
            b.ToggleMode = true;
            b.Name = "TdMode_" + m;
            _modeButtons.Add(b);
            bar.AddChild(b);
        }
        bar.AddChild(Ui.Spacer(16));
        var check = Ui.Button("✓ Проверить", () => RunCheck(true), "Проверить базы, маршруты и группы сцены");
        check.Name = "TdCheck";
        bar.AddChild(check);
        var run = Ui.Button("▶ Запустить бой", RunTest, "Бой на черновике (опубликованный контент не меняется)");
        run.Name = "TdRun";
        bar.AddChild(run);
        root.AddChild(bar);

        var main = Ui.HBox(8).Expand(true, true);
        // Side panels keep their width (content scrolls instead): a resized map view would refit the camera mid-gesture.
        var libScroll = new ScrollContainer { CustomMinimumSize = new Vector2(230, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Auto };
        _library = Ui.VBox(4).Expand();
        libScroll.AddChild(_library);
        main.AddChild(libScroll);

        var center = Ui.VBox(4).Expand(true, true);
        _view = new BoardViewport();
        _view.Clicked += OnClick;
        _view.Dragged += OnDrag;
        _view.GuiInput += OnViewKey;
        center.AddChild(_view);
        _status = Ui.Label("", 13, Ui.TextDim);
        _status.ClipText = true;
        _status.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _status.CustomMinimumSize = new Vector2(10, 0);
        center.AddChild(_status);
        main.AddChild(center);

        var propScroll = new ScrollContainer { CustomMinimumSize = new Vector2(370, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Auto };
        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_right", 12);
        _props = Ui.VBox(6).Expand();
        pad.AddChild(_props);
        propScroll.AddChild(pad.Expand());
        main.AddChild(propScroll);
        root.AddChild(main);

        var probScroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 120), HorizontalScrollMode = ScrollContainer.ScrollMode.Auto };
        _problemsBox = Ui.VBox(2).Expand();
        probScroll.AddChild(_problemsBox);
        root.AddChild(probScroll);

        _menu = new PopupMenu { Name = "TdObjectMenu" };
        _menu.IdPressed += OnMenu;
        root.AddChild(_menu);

        AfterLayout(_view, () =>
        {
            RebuildPreview(true);
            SetMode(Mode);
            RunCheck(false);
        });
        return root;
    }

    public void SetMode(TdMode m)
    {
        Mode = m;
        _drawing = null;
        _moving = null;
        for (int i = 0; i < _modeButtons.Count; i++) _modeButtons[i].ButtonPressed = i == (int)m;
        if (m != TdMode.Objects) PlaceType = null;
        RefreshLibrary();
        RefreshProps();
        Hint();
    }

    public void SetPlace(ScenarioObjectType? t)
    {
        PlaceType = t;
        RefreshLibrary();
        Hint();
    }

    private void Hint()
    {
        if (!IsInstanceValid(_status)) return;
        _status.Text = Mode switch
        {
            TdMode.Objects when PlaceType is { } t => $"ЛКМ по гексу — поставить «{ScenarioObjects.Caption(t)}». ПКМ по объекту — меню. Esc — выбор.",
            TdMode.Objects => "ЛКМ — выбрать объект, протяжка — передвинуть; Shift+ЛКМ по соседнему гексу — добавить/убрать гекс базы; R — повернуть; Delete — удалить.",
            TdMode.Routes when Route(SelectedRoute) is { } r => $"Маршрут «{r.DisplayName}»: протяните ЛКМ от базы спавна до цели (начало у конца линии — продолжить её).",
            TdMode.Routes => "Выберите базу спавна (ЛКМ) и нажмите «Создать маршрут» — или сразу протяните ЛКМ от базы спавна.",
            _ => "Выберите волну и группу; «Добавить группу» привязывает её к выбранной базе спавна и её маршруту.",
        };
    }

    // ------------------------------------------------------------------ helpers

    private ScenarioObjectDef? Obj(string id) => _sc?.Object(id);
    public RouteDef? Route(string id) => _sc?.Route(id);
    private ScenarioObjectDef? ObjectAt(HexCoord c) => _sc?.Objects.LastOrDefault(o => o.Occupies(c));

    private ScenarioObjectDef? MainBase() =>
        _sc?.Objects.FirstOrDefault(o => o.Type == ScenarioObjectType.PlayerBase && o.Health?.Main == true)
        ?? _sc?.Objects.FirstOrDefault(o => o.Type == ScenarioObjectType.PlayerBase);

    private IEnumerable<(string, string)> ObjectChoices(Func<ScenarioObjectDef, bool> filter) =>
        (_sc?.Objects ?? new()).Where(filter).Select(o => (o.Id, $"{o.DisplayName} [{o.Id}]"));

    private IEnumerable<(string, string)> RouteChoices() => (_sc?.Routes ?? new()).Select(r => (r.Id, $"{r.DisplayName} [{r.Id}]"));

    private void Edited(bool preview = false)
    {
        if (_sc == null) return;
        OnEdited(_sc);
        if (preview) RebuildPreview(false);
        RunCheck(false);
    }

    private void SelectObject(string obj = "")
    {
        SelectedObject = obj;
        RefreshLibrary();
        RefreshProps();
        Hint();
    }

    // ------------------------------------------------------------------ preview

    private void RebuildPreview(bool frame)
    {
        if (_sc == null || !IsInstanceValid(_view)) return;
        _map = Ctx.Db.Get<MapDef>(_sc.Map);
        if (_map == null)
        {
            _status.Text = $"Карта «{_sc.Map}» не найдена — выберите карту в свойствах сцены.";
            return;
        }
        bool newMap = _mapId != _map.Id;
        _mapId = _map.Id;
        _view.Board.ClearUnits();
        _preview = new Simulation(Ctx.Db, _map, new SimOptions { Seed = 1, FogOfWar = false, AdminMode = true, Stacking = _sc.Limits.Stacking });
        try
        {
            _preview.Structures.Load(_sc);
            // The units of the scene for context (placed on the «Сценарии» section).
            foreach (var su in _sc.Units)
                if (Ctx.Db.Unit(su.Unit) is { } ud && _preview.Map.Contains(su.C))
                    _preview.Spawn(ud, string.IsNullOrEmpty(su.Faction) ? null : su.Faction, su.C, su.Facing, string.IsNullOrEmpty(su.Name) ? null : su.Name, su.Tag);
        }
        catch (InvalidOperationException e)
        {
            _status.Text = "Предпросмотр сооружений: " + e.Message;
        }
        _view.Bind(_preview.Map, Ctx.Db, frame || newMap);
    }

    public override void _Process(double delta)
    {
        if (_preview == null || _sc == null || !IsInstanceValid(_view)) return;
        _view.Board.SyncUnits(_preview, 0, _ => true, _ => false, (float)delta);
        foreach (var v in _view.Board.Views.Values) v.Selected = v.Unit.ObjectId != "" && v.Unit.ObjectId == SelectedObject;
        _view.Board.Objects.Sync(_sc, _preview.Map, Ctx.Db);
        var o = _view.Board.Overlay;
        o.Clear();
        _view.Labels.Begin();
        float pulse = o.Pulse(4);

        // Objects: footprint outline, spawn area, name.
        foreach (var obj in _sc.Objects)
        {
            bool sel = obj.Id == SelectedObject;
            var col = obj.Type == ScenarioObjectType.SpawnBase ? new Color(1f, 0.45f, 0.35f) : obj.Health != null ? new Color(0.4f, 0.7f, 1f) : new Color(1f, 0.85f, 0.35f);
            foreach (var c in obj.Cells()) o.RingHex(c, sel ? Ui.Accent : new Color(col.R, col.G, col.B, 0.55f), sel ? 0.1f : 0.05f);
            if (obj.Spawn is { AreaRadius: > 0 })
                foreach (var c in ScenarioObjectsCheck.SpawnArea(obj).Where(c => !obj.Occupies(c))) o.DashedRing(c, new Color(1f, 0.45f, 0.35f, 0.6f), 0.05f);
            var cell = _preview.Map.Get(obj.Anchor);
            _view.Labels.Add(Render.Board.Center(obj.Anchor, (cell == null ? 0 : Render.Board.SurfaceY(cell)) + (obj.Health != null ? 2.3f : 1.4f)),
                obj.DisplayName, sel ? Ui.Accent : col, sel ? 24 : 20);
        }

        // Routes; the one being drawn on top with its live problems.
        ScenarioObjectsView.DrawRoutes(o, _sc, Mode == TdMode.Routes ? SelectedRoute : "", Mode == TdMode.Routes ? 0.9f : 0.5f);
        if (_drawing != null)
        {
            var others = new HashSet<HexCoord>(_sc.Routes.Where(r => r != _drawRoute).SelectMany(r => r.MainPath()));
            foreach (var c in _drawing.Cells)
            {
                if (!_drawing.IsWalkable(c)) o.FillHex(c, new Color(1f, 0.25f, 0.2f, 0.55f), 0.85f);
                else if (_drawing.Filled.Contains(c)) o.HatchHex(c, new Color(1f, 1f, 1f, 0.35f));
                if (others.Contains(c)) o.RingHex(c, new Color(1f, 0.9f, 0.2f, 0.9f), 0.07f, 0.8f);
            }
            ScenarioObjectsView.DrawLine(o, _drawing.Cells, new Color(1f, 1f, 1f, 0.95f), 0.12f);
            if (_drawing.Cells.Count > 0) o.RingHex(_drawing.Cells[^1], new Color(1f, 1f, 1f, 0.6f + 0.4f * pulse), 0.1f);
        }
        // Hexes named by problems.
        foreach (var p in (_drawing != null ? _liveProblems : _problems).Where(p => p.At.HasValue && p.Severity == IssueSeverity.Error).Take(40))
            o.DashedRing(p.At!.Value, new Color(1f, 0.3f, 0.25f, 0.6f + 0.4f * pulse), 0.07f);
        if (_moving != null)
            foreach (var c in _moving.Cells()) o.FillHex(c, new Color(1f, 1f, 1f, 0.25f));
        if (_view.HoverHex is HexCoord h) o.RingHex(h, new Color(1, 1, 1, 0.8f), 0.06f);
        o.Commit();
        _view.Labels.End();
    }

    // ------------------------------------------------------------------ input

    private void OnClick(HexCoord? hex, Cell? cell, InputEventMouseButton mb)
    {
        if (_sc == null) return;
        if (mb.ButtonIndex == MouseButton.Right)
        {
            if (mb.Pressed && hex is { } rh && ObjectAt(rh) is { } ro) OpenMenu(ro, mb.GlobalPosition);
            return;
        }
        if (!mb.Pressed)
        {
            if (_drawing != null)
            {
                if (hex is { } end) _drawing.To(end);
                FinishDrawing();
            }
            if (_moving != null) FinishMove();
            return;
        }
        if (hex == null) return;
        var at = ObjectAt(hex.Value);
        switch (Mode)
        {
            case TdMode.Objects:
            {
                var sel = Obj(SelectedObject);
                if (mb.ShiftPressed && sel != null)
                {
                    ToggleFootprint(sel, hex.Value);
                    return;
                }
                if (at != null)
                {
                    SelectObject(at.Id);
                    _moving = at;
                    _moveFrom = at.Anchor;
                    return;
                }
                if (PlaceType is { } t) Place(t, hex.Value);
                else SelectObject();
                return;
            }
            case TdMode.Routes:
                StartDrawing(hex.Value, at);
                return;
            case TdMode.Waves:
                if (at != null) SelectObject(at.Id);
                RefreshLibrary();
                return;
        }
    }

    private void OnDrag(HexCoord? hex, Cell? cell, MouseButtonMask mask)
    {
        if (hex == null || (mask & MouseButtonMask.Left) == 0) return;
        if (_drawing != null)
        {
            if (_drawing.To(hex.Value)) LiveCheck();
            return;
        }
        if (_moving != null && hex.Value != _moving.Anchor && _map != null && _map.Cells.Any(c => c.C == hex.Value))
            _moving.Anchor = hex.Value;
    }

    private void OnViewKey(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } k || _sc == null) return;
        switch (k.Keycode)
        {
            case Key.Escape:
                if (_drawing != null)
                {
                    _drawing = null;
                    _status.Text = "Рисование отменено — маршрут не изменён.";
                }
                else SetPlace(null);
                _view.AcceptEvent();
                break;
            case Key.Delete when Obj(SelectedObject) is { } o && Mode == TdMode.Objects:
                Delete(o);
                _view.AcceptEvent();
                break;
            case Key.R when Obj(SelectedObject) is { } o && Mode == TdMode.Objects:
                Rotate(o, k.ShiftPressed ? -1 : 1);
                _view.AcceptEvent();
                break;
        }
    }

    // ------------------------------------------------------------------ objects

    public ScenarioObjectDef? Place(ScenarioObjectType t, HexCoord at)
    {
        if (_sc == null || _map == null) return null;
        if (!_map.Cells.Any(c => c.C == at))
        {
            _status.Text = $"{at} вне карты";
            return null;
        }
        if (ObjectAt(at) is { } busy)
        {
            _status.Text = $"Гекс {at} занят объектом «{busy.DisplayName}»";
            return null;
        }
        var o = ScenarioObjects.Create(_sc, t, at);
        _sc.Objects.Add(o);
        _status.Text = $"Поставлен объект «{o.DisplayName}» [{o.Id}] в {at}";
        SelectObject(o.Id);
        Edited(true);
        return o;
    }

    private void FinishMove()
    {
        var o = _moving!;
        _moving = null;
        if (o.Anchor == _moveFrom) return;
        var clash = _sc!.Objects.FirstOrDefault(x => x != o && x.Cells().Intersect(o.Cells()).Any());
        _status.Text = clash != null
            ? $"«{o.DisplayName}» перекрывает «{clash.DisplayName}» — передвиньте, проверка покажет ошибку"
            : $"«{o.DisplayName}» перемещён в {o.Anchor}";
        Edited(true);
        RefreshProps();
    }

    private void ToggleFootprint(ScenarioObjectDef o, HexCoord c)
    {
        if (c == o.Anchor)
        {
            _status.Text = "Положение объекта нельзя убрать из его гексов — передвиньте объект";
            return;
        }
        int steps = (int)o.Facing - (int)HexDirection.E;
        var offset = (c - o.Anchor).Rotate(-steps);
        if (o.Footprint.Remove(offset))
            _status.Text = $"«{o.DisplayName}»: гекс {c} убран (гексов {o.Cells().Count})";
        else if (o.Cells().Any(x => HexCoord.Distance(x, c) == 1))
        {
            o.Footprint.Add(offset);
            _status.Text = $"«{o.DisplayName}»: гекс {c} добавлен (гексов {o.Cells().Count})";
        }
        else
        {
            _status.Text = "Новый гекс должен касаться объекта";
            return;
        }
        Edited(true);
    }

    private void Rotate(ScenarioObjectDef o, int steps)
    {
        o.Facing = o.Facing.Rotate(steps);
        _status.Text = $"«{o.DisplayName}»: ориентация {o.Facing.RuShortName()}";
        Edited(true);
        RefreshProps();
    }

    public ScenarioObjectDef? Duplicate(ScenarioObjectDef src)
    {
        if (_sc == null || _map == null) return null;
        var mapCells = new HashSet<HexCoord>(_map.Cells.Select(c => c.C));
        for (int r = 1; r <= 6; r++)
            foreach (var at in HexCoord.Ring(src.Anchor, r))
            {
                var copy = ScenarioObjects.Duplicate(_sc, src, at);
                if (!copy.Cells().All(mapCells.Contains) || copy.Cells().Any(c => ObjectAt(c) != null)) continue;
                _sc.Objects.Add(copy);
                _status.Text = $"Копия «{copy.DisplayName}» [{copy.Id}] в {at}: новый ID, ссылки на шаблоны и ресурсы сохранены";
                SelectObject(copy.Id);
                Edited(true);
                return copy;
            }
        _status.Text = "Рядом нет места для копии";
        return null;
    }

    private void Delete(ScenarioObjectDef o)
    {
        if (_sc == null) return;
        int routes = _sc.Routes.Count(r => r.Spawn == o.Id || r.Target == o.Id);
        int groups = _sc.Waves.Sum(w => w.Groups.Count(g => g.SpawnBase == o.Id || g.Target == o.Id));
        void Do()
        {
            _sc!.Objects.Remove(o);
            if (SelectedObject == o.Id) SelectedObject = "";
            _status.Text = $"«{o.DisplayName}» удалён" + (routes + groups > 0 ? " — ссылки на него показаны в проблемах" : "");
            SelectObject();
            Edited(true);
        }
        if (routes + groups == 0) Do();
        else Dialogs.Confirm(this, "Удалить объект", $"На «{o.DisplayName}» ссылаются маршрутов: {routes}, групп: {groups}. Ссылки останутся и будут отмечены как ошибки. Удалить?", Do, "Удалить");
    }

    private void OpenMenu(ScenarioObjectDef o, Vector2 at)
    {
        _menuObject = o.Id;
        SelectObject(o.Id);
        _menu.Clear();
        _menu.AddItem("Настройки", 1);
        if (o.Spawn != null)
        {
            _menu.AddItem("Добавить группу", 2);
            _menu.AddItem("Создать маршрут", 3);
            _menu.AddItem("Проверить маршруты", 5);
        }
        _menu.AddItem("Повернуть", 7);
        _menu.AddItem("Дублировать", 4);
        _menu.AddSeparator();
        _menu.AddItem("Удалить", 6);
        _menu.Position = (Vector2I)at;
        _menu.Popup();
    }

    /// <summary>A command of the object menu (by its ID; also used by the tests).</summary>
    public void MenuCommand(string objectId, long id)
    {
        _menuObject = objectId;
        OnMenu(id);
    }

    private void OnMenu(long id)
    {
        var o = Obj(_menuObject);
        if (o == null) return;
        switch (id)
        {
            case 1:
                SetMode(TdMode.Objects);
                SelectObject(o.Id);
                break;
            case 2:
                SetMode(TdMode.Waves);
                SelectObject(o.Id);
                AddGroup(o);
                break;
            case 3:
                SetMode(TdMode.Routes);
                SelectObject(o.Id);
                CreateRoute(o);
                break;
            case 4:
                Duplicate(o);
                break;
            case 5:
            {
                RunCheck(true);
                var mine = _sc!.Routes.Where(r => r.Spawn == o.Id).ToList();
                int errs = _problems.Count(p => p.Severity == IssueSeverity.Error && mine.Any(r => r.Id == p.Object));
                _status.Text = mine.Count == 0 ? $"У «{o.DisplayName}» нет маршрутов" : $"Маршрутов у «{o.DisplayName}»: {mine.Count}, ошибок: {errs}";
                break;
            }
            case 6:
                Delete(o);
                break;
            case 7:
                Rotate(o, 1);
                break;
        }
    }

    // ------------------------------------------------------------------ routes

    public RouteDef? CreateRoute(ScenarioObjectDef? spawn = null)
    {
        if (_sc == null) return null;
        spawn ??= Obj(SelectedObject) is { Spawn: not null } s ? s : _sc.Objects.FirstOrDefault(x => x.Spawn != null);
        if (spawn == null)
        {
            _status.Text = "Сначала поставьте базу спавна (режим «Объекты и базы»)";
            return null;
        }
        var target = MainBase();
        var colors = new[] { "#ff9a40", "#ff5fa2", "#58d0ff", "#b98cff", "#7ee06a", "#ffd84a" };
        var r = new RouteDef
        {
            Id = RouteGraph.NewId(_sc), Name = $"Маршрут {_sc.Routes.Count + 1}", Spawn = spawn.Id, Target = target?.Id ?? "",
            Color = colors[_sc.Routes.Count % colors.Length],
        };
        _sc.Routes.Add(r);
        SelectedRoute = r.Id;
        SelectedObject = spawn.Id;
        Edited();
        RefreshLibrary();
        RefreshProps();
        _status.Text = $"Маршрут «{r.DisplayName}» от «{spawn.DisplayName}» к «{target?.DisplayName ?? "(цель не назначена)"}»: протяните ЛКМ от базы спавна.";
        return r;
    }

    private void StartDrawing(HexCoord at, ScenarioObjectDef? onObject)
    {
        if (_sc == null || _map == null) return;
        var r = Route(SelectedRoute);
        // Starting on (or next to) a spawn base without a chosen route of it: a new route of that base.
        var spawnHere = onObject is { Spawn: not null } ? onObject : _sc.Objects.FirstOrDefault(o => o.Spawn != null && ScenarioObjectsCheck.SpawnArea(o).Any(c => HexCoord.Distance(c, at) <= 1));
        if (r == null || (spawnHere != null && r.Spawn != spawnHere.Id && r.MainPath().Count > 0))
        {
            if (spawnHere == null)
            {
                _status.Text = "Начните линию у базы спавна или выберите маршрут в списке";
                return;
            }
            r = _sc.Routes.FirstOrDefault(x => x.Spawn == spawnHere.Id && x.MainPath().Count == 0) ?? CreateRoute(spawnHere);
            if (r == null) return;
            SelectedRoute = r.Id;
        }
        var blocked = _sc.Objects.Where(o => o.Health != null && o.Id != r.Target).SelectMany(o => o.Cells());
        _drawing = new RouteDrawing(Ctx.Db, _map, ScenarioObjectsCheck.CategoriesOf(Ctx.Db, _sc, r), blocked);
        _drawRoute = r;
        var old = r.MainPath();
        if (old.Count > 0 && old[^1] == at)
        {
            // Continue from the end of the line.
            _drawing.Cells.AddRange(old);
        }
        else
        {
            // A line started on the spawn base itself begins at its exit hex.
            var start = onObject != null && onObject.Spawn != null ? onObject.Anchor : at;
            _drawing.To(start);
            if (start != at) _drawing.To(at);
        }
        LiveCheck();
    }

    private RouteDef DraftOf(RouteDef r, IReadOnlyList<HexCoord> cells)
    {
        var d = RouteGraph.FromCells(r.Id, cells, r.Spawn, r.Target, r.Name);
        d.Color = r.Color;
        d.End = r.End;
        d.AllowIncomplete = r.AllowIncomplete;
        return d;
    }

    private void LiveCheck()
    {
        if (_drawing == null || _drawRoute == null || _sc == null || _map == null) return;
        _liveProblems = ScenarioObjectsCheck.Route(Ctx.Db, _sc, _map, DraftOf(_drawRoute, _drawing.Cells))
            .Where(p => p.Severity != IssueSeverity.Info).ToList();
        var first = _liveProblems.FirstOrDefault(p => p.Severity == IssueSeverity.Error && !p.Message.Contains("не доходит") && !p.Message.Contains("не завершён"));
        _status.Text = $"Гексов: {_drawing.Cells.Count}" + (first != null ? $" · ⛔ {first.Message}" : " · линия проходима");
    }

    private void FinishDrawing()
    {
        var d = _drawing!;
        var r = _drawRoute!;
        _drawing = null;
        _drawRoute = null;
        if (_sc == null) return;
        var committed = DraftOf(r, d.Cells);
        // The route keeps its identity and settings; only the line changes. Nothing is cut or redirected.
        r.Nodes = committed.Nodes;
        r.Edges = committed.Edges;
        Edited();
        var mine = _problems.Where(p => p.Object == r.Id && p.Severity != IssueSeverity.Info).ToList();
        var err = mine.FirstOrDefault(p => p.Severity == IssueSeverity.Error);
        _status.Text = err == null
            ? $"Маршрут «{r.DisplayName}»: {d.Cells.Count} гексов, проверка пройдена" + (mine.Count > 0 ? $" (предупреждений: {mine.Count})" : "")
            : $"Маршрут «{r.DisplayName}» сохранён с ошибкой: {err.Message}" + (err.Message.Contains("не доходит") ? " — «Продлить до цели» в списке слева" : "");
        RefreshLibrary();
        RefreshProps();
    }

    /// <summary>«Продлить до цели»: adds the shortest passable way from the end of the line to the target.</summary>
    public bool ExtendToTarget(RouteDef r)
    {
        if (_sc == null || _map == null) return false;
        var target = Obj(r.Target);
        if (target == null)
        {
            _status.Text = $"У маршрута «{r.DisplayName}» не назначена цель";
            return false;
        }
        var blocked = _sc.Objects.Where(o => o.Health != null && o.Id != r.Target).SelectMany(o => o.Cells());
        var d = new RouteDrawing(Ctx.Db, _map, ScenarioObjectsCheck.CategoriesOf(Ctx.Db, _sc, r), blocked);
        d.Cells.AddRange(r.MainPath());
        var rest = d.WayTo(target);
        if (rest == null)
        {
            _status.Text = $"От конца маршрута «{r.DisplayName}» нет проходимого пути до «{target.DisplayName}»";
            return false;
        }
        d.Cells.AddRange(rest);
        var c = DraftOf(r, d.Cells);
        r.Nodes = c.Nodes;
        r.Edges = c.Edges;
        _status.Text = $"Маршрут «{r.DisplayName}» продлён на {rest.Count} гекс(ов) до «{target.DisplayName}»";
        Edited();
        RefreshLibrary();
        return true;
    }

    /// <summary>Draws a route through the given hexes as the drag gesture would (for the tests and scripted use).</summary>
    public void DrawThrough(IReadOnlyList<HexCoord> hexes)
    {
        if (hexes.Count == 0) return;
        StartDrawing(hexes[0], ObjectAt(hexes[0]));
        if (_drawing == null) return;
        foreach (var h in hexes.Skip(1)) _drawing.To(h);
        LiveCheck();
        FinishDrawing();
    }

    // ------------------------------------------------------------------ waves

    public SpawnGroupDef? AddGroup(ScenarioObjectDef? spawn = null)
    {
        if (_sc == null) return null;
        spawn ??= Obj(SelectedObject) is { Spawn: not null } s ? s : _sc.Objects.FirstOrDefault(x => x.Spawn != null);
        if (spawn == null)
        {
            _status.Text = "Нет базы спавна — поставьте её в режиме «Объекты и базы»";
            return null;
        }
        var wave = SelectedWave ?? _sc.Waves.LastOrDefault();
        if (wave == null)
        {
            wave = new WaveDef { Name = "Волна 1", Start = WaveStart.Time, Time = 3 };
            _sc.Waves.Add(wave);
        }
        int n = _sc.Waves.Sum(w => w.Groups.Count) + 1;
        string gid;
        do gid = $"group_{n++}"; while (_sc.Waves.Any(w => w.Groups.Any(g => g.Id == gid)));
        var unit = Ctx.Db.All<UnitDef>().FirstOrDefault(u => u.Faction == spawn.Faction) ?? Ctx.Db.All<UnitDef>().FirstOrDefault(u => u.Faction == "enemy");
        var g = new SpawnGroupDef
        {
            Id = gid, Unit = unit?.Id ?? "", Count = 3, Interval = 1.5, SpawnBase = spawn.Id,
            Route = _sc.Routes.FirstOrDefault(r => r.Spawn == spawn.Id)?.Id ?? "",
        };
        wave.Groups.Add(g);
        SelectedWave = wave;
        SelectedGroup = g;
        _status.Text = $"Группа «{g}» в волне «{wave.Name}»: база «{spawn.DisplayName}»" + (g.Route == "" ? ", маршрута у базы нет — нарисуйте его" : $", маршрут [{g.Route}]");
        Edited();
        RefreshLibrary();
        RefreshProps();
        return g;
    }

    // ------------------------------------------------------------------ panels

    private void RefreshLibrary()
    {
        if (!IsInstanceValid(_library) || _sc == null) return;
        Ui.ClearChildren(_library);
        Button Btn(string text, Action a, string tip = "", string name = "")
        {
            var b = Ui.Button(text, a, tip);
            b.Alignment = HorizontalAlignment.Left;
            b.ClipText = true;
            if (name != "") b.Name = name;
            _library.AddChild(b);
            return b;
        }
        switch (Mode)
        {
            case TdMode.Objects:
            {
                _library.AddChild(Ui.Label("Поставить:", 13, Ui.TextDim));
                var sel = Btn("☐ Выбрать / двигать", () => SetPlace(null), "ЛКМ выбирает и передвигает объекты", "TdPlace_None");
                sel.ToggleMode = true;
                sel.ButtonPressed = PlaceType == null;
                foreach (var t in PlaceTypes)
                {
                    var tt = t;
                    var b = Btn(ScenarioObjects.Caption(t), () => SetPlace(tt), "", "TdPlace_" + t);
                    b.ToggleMode = true;
                    b.ButtonPressed = PlaceType == t;
                }
                _library.AddChild(Ui.Sep());
                _library.AddChild(Ui.Label($"Объекты сцены ({_sc.Objects.Count}):", 13, Ui.TextDim));
                foreach (var o in _sc.Objects)
                {
                    var oo = o;
                    var b = Btn($"{Icon(o)} {o.DisplayName}", () => SelectObject(oo.Id), $"{o.Id} · {ScenarioObjects.Caption(o.Type)} · {o.Anchor}", "TdObj_" + o.Id);
                    if (o.Id == SelectedObject) b.AddThemeColorOverride("font_color", Ui.Accent);
                }
                if (Obj(SelectedObject) is { } so)
                {
                    _library.AddChild(Ui.Sep());
                    Btn("⟳ Повернуть (R)", () => Rotate(so, 1));
                    Btn("⧉ Дублировать", () => Duplicate(so));
                    Btn("✗ Удалить (Delete)", () => Delete(so));
                }
                break;
            }
            case TdMode.Routes:
            {
                Btn("+ Создать маршрут", () => CreateRoute(), "От выбранной базы спавна (или первой) к главной базе", "TdNewRoute");
                _library.AddChild(Ui.Label($"Маршруты ({_sc.Routes.Count}):", 13, Ui.TextDim));
                foreach (var r in _sc.Routes)
                {
                    var rr = r;
                    int errs = _problems.Count(p => p.Object == r.Id && p.Severity == IssueSeverity.Error);
                    var b = Btn($"{(errs > 0 ? "⛔" : "✓")} {r.DisplayName} · {Math.Max(0, r.MainPath().Count - 1)} гекс.", () =>
                    {
                        SelectedRoute = rr.Id;
                        RefreshLibrary();
                        RefreshProps();
                        Hint();
                    }, $"{r.Id}: {Obj(r.Spawn)?.DisplayName ?? r.Spawn} → {Obj(r.Target)?.DisplayName ?? r.Target}", "TdRoute_" + r.Id);
                    b.AddThemeColorOverride("font_color", r.Id == SelectedRoute ? Ui.Accent : Ui.Parse(r.Color, Ui.Text));
                }
                if (Route(SelectedRoute) is { } sr)
                {
                    _library.AddChild(Ui.Sep());
                    Btn("→ Продлить до цели", () => ExtendToTarget(sr), "Достроить кратчайший проходимый путь от конца линии до цели", "TdExtend");
                    Btn("✎ Перерисовать", () =>
                    {
                        sr.Nodes.Clear();
                        sr.Edges.Clear();
                        Edited();
                        RefreshLibrary();
                        _status.Text = $"Линия маршрута «{sr.DisplayName}» очищена — протяните ЛКМ от базы спавна";
                    });
                    Btn("⇢ Назначить группам базы", () =>
                    {
                        int n = 0;
                        foreach (var g in _sc.Waves.SelectMany(w => w.Groups).Where(g => g.SpawnBase == sr.Spawn && g.Route != sr.Id))
                        {
                            g.Route = sr.Id;
                            n++;
                        }
                        _status.Text = $"Маршрут «{sr.DisplayName}» назначен групп: {n}";
                        Edited();
                    }, "Всем группам базы спавна этого маршрута");
                    Btn("✗ Удалить маршрут", () =>
                    {
                        _sc.Routes.Remove(sr);
                        SelectedRoute = "";
                        Edited();
                        RefreshLibrary();
                        RefreshProps();
                    });
                }
                break;
            }
            case TdMode.Waves:
            {
                Btn("+ Добавить волну", () =>
                {
                    var w = new WaveDef { Name = $"Волна {_sc.Waves.Count + 1}", Start = WaveStart.Time, Time = 3 + 20 * _sc.Waves.Count };
                    _sc.Waves.Add(w);
                    SelectedWave = w;
                    SelectedGroup = null;
                    Edited();
                    RefreshLibrary();
                    RefreshProps();
                });
                Btn("+ Добавить группу", () => AddGroup(), "В выбранную волну, от выбранной базы спавна", "TdAddGroup");
                foreach (var w in _sc.Waves)
                {
                    var ww = w;
                    var wb = Btn($"▾ {w.Name} · {(w.Start == WaveStart.Time ? $"{w.Time:0} с" : w.Start.ToString())}", () =>
                    {
                        SelectedWave = ww;
                        SelectedGroup = null;
                        RefreshLibrary();
                        RefreshProps();
                    });
                    if (w == SelectedWave && SelectedGroup == null) wb.AddThemeColorOverride("font_color", Ui.Accent);
                    foreach (var g in w.Groups)
                    {
                        var gg = g;
                        bool bad = _problems.Any(p => p.Severity == IssueSeverity.Error && p.Path.Contains(g.Id));
                        var gb = Btn($"    {(bad ? "⛔" : "•")} {g} · {Obj(g.SpawnBase)?.DisplayName ?? "?"}", () =>
                        {
                            SelectedWave = ww;
                            SelectedGroup = gg;
                            RefreshLibrary();
                            RefreshProps();
                        }, $"{g.Id}: маршрут {(g.Route == "" ? "—" : g.Route)}", "TdGroup_" + g.Id);
                        if (g == SelectedGroup) gb.AddThemeColorOverride("font_color", Ui.Accent);
                    }
                }
                break;
            }
        }
    }

    private static string Icon(ScenarioObjectDef o) => o.Type switch
    {
        ScenarioObjectType.PlayerBase => "■",
        ScenarioObjectType.SpawnBase => "◎",
        ScenarioObjectType.Checkpoint => "⚑",
        ScenarioObjectType.Defense => "▲",
        ScenarioObjectType.Goal => "◆",
        _ => "•",
    };

    private PropertyGrid Grid(object target, Action? changed = null, Action<string>? prop = null)
    {
        var grid = new PropertyGrid { LabelWidth = 170 };
        grid.Choices[nameof(SpawnGroupDef.SpawnBase)] = () => ObjectChoices(o => o.Spawn != null);
        grid.Choices[nameof(SpawnGroupDef.Route)] = RouteChoices;
        grid.Choices[nameof(SpawnGroupDef.Target)] = () => ObjectChoices(_ => true);
        grid.Choices[nameof(RouteDef.Spawn)] = () => ObjectChoices(o => o.Spawn != null);
        grid.Choices[nameof(BaseDefeatDef.MainBase)] = () => ObjectChoices(o => o.Type == ScenarioObjectType.PlayerBase);
        grid.SetTarget(target, Ctx.Db);
        grid.Changed += () =>
        {
            Edited(true);
            changed?.Invoke();
        };
        if (prop != null) grid.PropertyChanged += (_, p) => prop(p);
        return grid;
    }

    private void RefreshProps()
    {
        if (!IsInstanceValid(_props) || _sc == null) return;
        Ui.ClearChildren(_props);
        switch (Mode)
        {
            case TdMode.Objects when Obj(SelectedObject) is { } o:
                _props.AddChild(Ui.Label($"{ScenarioObjects.Caption(o.Type)} [{o.Id}] · {o.Anchor} · гексов {o.Cells().Count}", 15, Ui.Accent, wrap: true));
                _props.AddChild(Grid(o, RefreshLibrary));
                return;
            case TdMode.Routes when Route(SelectedRoute) is { } r:
                _props.AddChild(Ui.Label($"Маршрут [{r.Id}] · гексов {Math.Max(0, r.MainPath().Count - 1)}", 15, Ui.Accent, wrap: true));
                _props.AddChild(Grid(r, RefreshLibrary));
                return;
            case TdMode.Waves when SelectedGroup is { } g:
                _props.AddChild(Ui.Label($"Группа [{g.Id}] волны «{SelectedWave?.Name}»", 15, Ui.Accent, wrap: true));
                _props.AddChild(Grid(g, RefreshLibrary));
                return;
            case TdMode.Waves when SelectedWave is { } w:
                _props.AddChild(Ui.Label($"Волна «{w.Name}»", 15, Ui.Accent, wrap: true));
                _props.AddChild(Grid(w, RefreshLibrary));
                return;
        }
        _props.AddChild(Ui.Label("Сцена", 15, Ui.Accent));
        _props.AddChild(Grid(_sc, null, p =>
        {
            if (p == nameof(ScenarioDef.Map)) RebuildPreview(true);
        }));
    }

    // ------------------------------------------------------------------ check & test

    public void RunCheck(bool report)
    {
        if (_sc == null || !IsInstanceValid(_problemsBox)) return;
        _map = Ctx.Db.Get<MapDef>(_sc.Map);
        _problems = _map == null ? new() : ScenarioObjectsCheck.All(Ctx.Db, _sc, _map);
        Ui.ClearChildren(_problemsBox);
        int errors = _problems.Count(p => p.Severity == IssueSeverity.Error), warns = _problems.Count(p => p.Severity == IssueSeverity.Warning);
        var head = Ui.Label(_problems.Count == 0 ? "Проблем нет" : $"Ошибок: {errors} · предупреждений: {warns} · сведений: {_problems.Count - errors - warns}", 13,
            errors > 0 ? Ui.Bad : warns > 0 ? Ui.Warn : Ui.Good);
        head.Name = "TdProblemsHead";
        _problemsBox.AddChild(head);
        foreach (var p in _problems.OrderBy(p => p.Severity == IssueSeverity.Error ? 0 : p.Severity == IssueSeverity.Warning ? 1 : 2).Take(60))
        {
            var pp = p;
            var col = p.Severity == IssueSeverity.Error ? Ui.Bad : p.Severity == IssueSeverity.Warning ? Ui.Warn : Ui.TextDim;
            var b = Ui.Button($"{(p.Severity == IssueSeverity.Error ? "⛔" : p.Severity == IssueSeverity.Warning ? "⚠" : "ℹ")} {p.Message}", () => GoTo(pp));
            b.Alignment = HorizontalAlignment.Left;
            b.Flat = true;
            b.ClipText = true;
            b.AddThemeColorOverride("font_color", col);
            _problemsBox.AddChild(b);
        }
        if (report) _status.Text = errors == 0 ? $"Проверка: критических ошибок нет (предупреждений {warns})" : $"Проверка: ошибок {errors} — сцена не запустится, пока они есть";
        if (Mode != TdMode.Objects) RefreshLibrary();
    }

    private void GoTo(ScenarioProblem p)
    {
        if (Route(p.Object) != null)
        {
            SetMode(TdMode.Routes);
            SelectedRoute = p.Object;
            RefreshLibrary();
            RefreshProps();
        }
        else if (Obj(p.Object) != null)
        {
            SetMode(TdMode.Objects);
            SelectObject(p.Object);
        }
        if (p.At is { } at && _preview?.Map.Get(at) is { } cell) _view.Board.Rig.FocusOn(Render.Board.CellTop(cell));
        _status.Text = p.Message;
    }

    private void RunTest()
    {
        if (_sc == null) return;
        Ctx.Validate();
        RunCheck(false);
        var errors = _problems.Where(p => p.Severity == IssueSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            Dialogs.Info(this, "Сцена не запущена", "Критические ошибки:\n• " + string.Join("\n• ", errors.Take(8).Select(p => p.Message)) + (errors.Count > 8 ? $"\n… и ещё {errors.Count - 8}" : ""));
            return;
        }
        _test(_sc);
    }
}
