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
/// Scenario editor (ТЗ 16.6): map, sides, units placed on the map with facing, waves, objectives,
/// victory/defeat conditions, triggers and messages, plus a test battle on the draft content.
/// </summary>
public partial class ScenarioEditorPanel : DefListPanel
{
    private ScenarioDef? _sc;
    private BoardViewport _view = null!;
    private OptionButton _unit = null!;
    private OptionButton _faction = null!;
    private OptionButton _facing = null!;
    private int _tool; // 0 place, 1 remove, 2 rotate, 3 select
    private readonly List<Button> _toolButtons = new();
    private VBoxContainer _unitsTab = null!;
    private VBoxContainer _propsTab = null!;
    private Label _status = null!;
    private ScenarioUnitDef? _selected;
    private Simulation? _preview;
    private string _mapId = "";
    private readonly Action<ScenarioDef> _test;

    public ScenarioEditorPanel(EditorContext ctx, Action<ScenarioDef> test) : base(ctx, DefCategory.Scenario, "Сценарии", null, null)
    {
        _test = test;
    }

    protected override bool UseScroll => false;

    protected override Def CreateNew() => new ScenarioDef
    {
        Name = "Новый сценарий",
        Map = Ctx.Db.Ids(DefCategory.Map).FirstOrDefault() ?? "",
    };

    protected override Control BuildDetail(Def d)
    {
        _sc = (ScenarioDef)d;
        _selected = null;
        _mapId = "";
        var root = Ui.HBox(8).Expand(true, true);
        var left = Ui.VBox(6).Expand(true, true);
        var bar = new HFlowContainer();
        bar.AddThemeConstantOverride("h_separation", 4);
        _toolButtons.Clear();
        string[] tools = { "Поставить", "Убрать", "Повернуть", "Выбрать", "Клетки цели" };
        for (int i = 0; i < tools.Length; i++)
        {
            int ti = i;
            var b = Ui.Button(tools[i], () => SetTool(ti));
            b.ToggleMode = true;
            b.ButtonPressed = i == _tool;
            _toolButtons.Add(b);
            bar.AddChild(b);
        }
        bar.AddChild(Ui.Label("Юнит:", 13, Ui.TextDim));
        _unit = new OptionButton { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(170, 0) };
        foreach (var u in Ctx.Db.All<UnitDef>()) _unit.AddItem($"{u.DisplayName} [{u.Faction}]");
        bar.AddChild(_unit);
        bar.AddChild(Ui.Label("Фракция:", 13, Ui.TextDim));
        _faction = new OptionButton { FocusMode = FocusModeEnum.None };
        _faction.AddItem("из шаблона");
        foreach (var f in Ctx.Db.Ids(DefCategory.Faction)) _faction.AddItem(f);
        bar.AddChild(_faction);
        bar.AddChild(Ui.Label("Цель:", 13, Ui.TextDim));
        _objective = new OptionButton { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(150, 0), TooltipText = "Инструмент «Клетки цели»: ЛКМ по гексу добавляет/убирает его в гексах выбранной цели" };
        foreach (var o in _sc.Objectives) _objective.AddItem($"{o.Name} [{o.Id}]");
        if (_sc.Objectives.Count == 0) _objective.AddItem("(целей нет)");
        bar.AddChild(_objective);
        bar.AddChild(Ui.Label("Взгляд:", 13, Ui.TextDim));
        _facing = new OptionButton { FocusMode = FocusModeEnum.None };
        for (int i = 0; i < 6; i++) _facing.AddItem(((HexDirection)i).RuShortName());
        bar.AddChild(_facing);
        bar.AddChild(Ui.Button("▶ Тест сценария", () =>
        {
            Ctx.Validate();
            _test(_sc!);
        }, "Запустить бой на черновике (опубликованный контент не меняется)"));
        left.AddChild(bar);
        _view = new BoardViewport();
        _view.Clicked += OnClick;
        left.AddChild(_view);
        _status = Ui.Label("Выберите юнита и инструмент «Поставить», затем ЛКМ по клетке. ПКМ/колесо — камера.", 13, Ui.TextDim);
        _status.ClipText = true;
        _status.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _status.CustomMinimumSize = new Vector2(10, 0);
        left.AddChild(_status);
        root.AddChild(left);

        var tabs = new TabContainer { CustomMinimumSize = new Vector2(380, 0), SizeFlagsVertical = SizeFlags.ExpandFill };
        _propsTab = TabIn(tabs, "Сценарий");
        _unitsTab = TabIn(tabs, "Юниты");
        root.AddChild(tabs);
        AfterLayout(_view, () =>
        {
            RebuildPreview(true);
            RefreshProps();
            RefreshUnits();
        });
        return root;
    }

    private static VBoxContainer TabIn(TabContainer tabs, string name)
    {
        var scroll = new ScrollContainer { Name = name, HorizontalScrollMode = ScrollContainer.ScrollMode.Auto };
        var v = Ui.VBox(6).Expand();
        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_right", 12);
        pad.AddChild(v);
        scroll.AddChild(pad.Expand());
        tabs.AddChild(scroll);
        return v;
    }

    private OptionButton _objective = null!;

    private ObjectiveDef? ChosenObjective => _sc != null && _objective.Selected >= 0 && _objective.Selected < _sc.Objectives.Count ? _sc.Objectives[_objective.Selected] : null;

    private void SetTool(int t)
    {
        _tool = t;
        if (t == 4 && _sc != null)
        {
            // Objectives may have been added on the «Сценарий» tab meanwhile.
            int keep = _objective.Selected;
            _objective.Clear();
            foreach (var o in _sc.Objectives) _objective.AddItem($"{o.Name} [{o.Id}]");
            if (_sc.Objectives.Count == 0) _objective.AddItem("(целей нет)");
            _objective.Selected = Math.Clamp(keep, 0, Math.Max(0, _objective.ItemCount - 1));
        }
        for (int i = 0; i < _toolButtons.Count; i++) _toolButtons[i].ButtonPressed = i == t;
    }

    private void RebuildPreview(bool frame)
    {
        if (_sc == null || !IsInstanceValid(_view)) return;
        var map = Ctx.Db.Get<MapDef>(_sc.Map);
        if (map == null)
        {
            _status.Text = $"Карта «{_sc.Map}» не найдена — выберите карту на вкладке «Сценарий».";
            return;
        }
        bool newMap = _mapId != map.Id;
        _mapId = map.Id;
        _view.Board.ClearUnits();
        _preview = new Simulation(Ctx.Db, map, new SimOptions { Seed = 1, FogOfWar = false, AdminMode = true, Stacking = _sc.Limits.Stacking });
        foreach (var su in _sc.Units)
        {
            var ud = Ctx.Db.Unit(su.Unit);
            if (ud == null || !_preview.Map.Contains(su.C)) continue;
            try
            {
                _preview.Spawn(ud, string.IsNullOrEmpty(su.Faction) ? null : su.Faction, su.C, su.Facing, string.IsNullOrEmpty(su.Name) ? null : su.Name, su.Tag);
            }
            catch (InvalidOperationException)
            {
                break;
            }
        }
        _view.Bind(_preview.Map, Ctx.Db, frame || newMap);
    }

    public override void _Process(double delta)
    {
        if (_preview == null || !IsInstanceValid(_view)) return;
        _view.Board.SyncUnits(_preview, 0, _ => true, _ => false, (float)delta);
        foreach (var v in _view.Board.Views.Values) v.Selected = _selected != null && v.Unit.Pos == _selected.C;
        var o = _view.Board.Overlay;
        o.Clear();
        _view.Labels.Begin();
        var map = Ctx.Db.Get<MapDef>(_sc?.Map);
        if (map != null)
            foreach (var m in map.Markers)
            {
                var col = m.Type == MarkerType.PlayerStart ? new Color(0.35f, 0.65f, 1f) : m.Type == MarkerType.EnemySpawn ? new Color(1f, 0.4f, 0.35f) : new Color(1f, 0.85f, 0.3f);
                o.DashedRing(m.C, col);
                var cell = _preview.Map.Get(m.C);
                _view.Labels.Add(Render.Board.Center(m.C, (cell == null ? 0 : Render.Board.SurfaceY(cell)) + 0.5f), m.Label, col, 22);
            }
        if (_view.HoverHex is HexCoord h) o.RingHex(h, new Color(1, 1, 1, 0.8f), 0.06f);
        // Hexes of the objectives (the chosen one brighter).
        if (_sc != null)
            foreach (var obj in _sc.Objectives)
            {
                bool chosen = obj == ChosenObjective && _tool == 4;
                foreach (var c in obj.Cells) o.RingHex(c, chosen ? new Color(0.4f, 1f, 0.55f, 0.95f) : new Color(0.4f, 0.9f, 0.5f, 0.45f), chosen ? 0.1f : 0.05f);
            }
        if (_sc != null)
            foreach (var w in _sc.Waves)
            {
                var mk = map?.FindMarker(w.SpawnMarker);
                if (mk == null) continue;
                var cell = _preview.Map.Get(mk.C);
                _view.Labels.Add(Render.Board.Center(mk.C, (cell == null ? 0 : Render.Board.SurfaceY(cell)) + 0.95f), $"{w.Name} @ {w.Time:0} с", new Color(1f, 0.6f, 0.5f), 20);
            }
        o.Commit();
        _view.Labels.End();
    }

    private void OnClick(HexCoord? hex, Cell? cell, InputEventMouseButton mb)
    {
        if (!mb.Pressed || _sc == null || hex == null || cell == null) return;
        var existing = _sc.Units.FirstOrDefault(u => u.C == hex.Value);
        int tool = mb.ButtonIndex == MouseButton.Right ? 1 : _tool;
        switch (tool)
        {
            case 0:
            {
                if (existing != null && _sc.Limits.Stacking != StackingMode.AllowAll)
                {
                    _selected = existing;
                    break;
                }
                var ids = Ctx.Db.All<UnitDef>().ToList();
                if (ids.Count == 0) return;
                var ud = ids[Math.Max(0, _unit.Selected)];
                if (!cell.Terrain.IsPassableFor(ud.MoveCategory) || !cell.AllowsPlacement)
                {
                    _status.Text = $"Нельзя поставить «{ud.DisplayName}» на «{cell.Terrain.DisplayName}»";
                    return;
                }
                var facs = Ctx.Db.Ids(DefCategory.Faction).ToList();
                var su = new ScenarioUnitDef
                {
                    Unit = ud.Id,
                    C = hex.Value,
                    Facing = (HexDirection)Math.Max(0, _facing.Selected),
                    Faction = _faction.Selected <= 0 ? "" : facs[_faction.Selected - 1],
                };
                _sc.Units.Add(su);
                _selected = su;
                break;
            }
            case 1:
                if (existing != null) _sc.Units.Remove(existing);
                if (_selected == existing) _selected = null;
                break;
            case 2:
                if (existing != null) existing.Facing = existing.Facing.Rotate(mb.ShiftPressed ? -1 : 1);
                _selected = existing;
                break;
            case 3:
                _selected = existing;
                break;
            case 4:
            {
                var obj = ChosenObjective;
                if (obj == null)
                {
                    _status.Text = "Сначала добавьте цель на вкладке «Сценарий» → «Цели» и выберите её в списке «Цель».";
                    return;
                }
                if (!obj.Cells.Remove(hex.Value)) obj.Cells.Add(hex.Value);
                _status.Text = $"Цель «{obj.Name}»: гексов {obj.Cells.Count}";
                OnEdited(_sc);
                RefreshProps();
                return;
            }
        }
        OnEdited(_sc);
        RebuildPreview(false);
        RefreshUnits();
    }

    private void RefreshProps()
    {
        if (!IsInstanceValid(_propsTab) || _sc == null) return;
        Ui.ClearChildren(_propsTab);
        var grid = new PropertyGrid { LabelWidth = 170 };
        grid.SetTarget(_sc, Ctx.Db);
        grid.Changed += () => OnEdited(_sc);
        grid.PropertyChanged += (o, p) =>
        {
            if (p == nameof(ScenarioDef.Map) || p == nameof(ScenarioLimitsDef.Stacking)) RebuildPreview(true);
        };
        _propsTab.AddChild(grid);
    }

    private void RefreshUnits()
    {
        if (!IsInstanceValid(_unitsTab) || _sc == null) return;
        Ui.ClearChildren(_unitsTab);
        _unitsTab.AddChild(Ui.Label($"Юнитов: {_sc.Units.Count}. Метка-ID нужна для условий «юнит погиб/жив» и «дойти до метки».", 13, Ui.TextDim, wrap: true));
        foreach (var su in _sc.Units)
        {
            var s = su;
            var ud = Ctx.Db.Unit(su.Unit);
            var b = Ui.Button($"{ud?.DisplayName ?? su.Unit} {su.C} {su.Facing.RuShortName()}{(string.IsNullOrEmpty(su.Faction) ? "" : " · " + su.Faction)}{(string.IsNullOrEmpty(su.Tag) ? "" : " #" + su.Tag)}", () =>
            {
                _selected = s;
                RefreshUnits();
            });
            b.Alignment = HorizontalAlignment.Left;
            if (su == _selected) b.AddThemeColorOverride("font_color", Ui.Accent);
            _unitsTab.AddChild(b);
        }
        if (_selected != null)
        {
            _unitsTab.AddChild(Ui.Sep());
            var grid = new PropertyGrid { LabelWidth = 140 };
            grid.SetTarget(_selected, Ctx.Db);
            grid.Changed += () =>
            {
                OnEdited(_sc);
                RebuildPreview(false);
            };
            _unitsTab.AddChild(grid);
        }
    }
}
