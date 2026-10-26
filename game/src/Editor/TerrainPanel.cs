using System;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// Terrain type editor: all parameters (kind, movement, line of sight, pit rules, look: material,
/// height, bevel, transparency, glow, shadow, outline, pattern, symbol, depth, animation) with a
/// live 3D preview — the edited tile in the centre of a small floor patch.
/// </summary>
public partial class TerrainPanel : DefListPanel
{
    private BoardViewport? _view;
    private TerrainDef? _terrain;
    private float _rebuild = -1;
    private static PreviewShape _shape = PreviewShape.Single;

    /// <summary>The patch shown last (UI tests).</summary>
    public MapDef? PreviewMap { get; private set; }

    public TerrainPanel(EditorContext ctx) : base(ctx, DefCategory.Terrain, "Типы местности", null, () => new TerrainDef
    {
        Name = "Новая местность", Color = "#5a616a", Look = new TerrainLookDef { Material = TerrainMaterial.Metal, OutlineColor = "#8d9aa8" },
    })
    {
    }

    protected override Control BuildDetail(Def d)
    {
        _terrain = (TerrainDef)d;
        var root = Ui.HBox(10).Expand(true, true);
        var grid = new PropertyGrid();
        grid.SetTarget(d, Ctx.Db);
        grid.Changed += () =>
        {
            OnEdited(d);
            _rebuild = 0.25f;
        };
        root.AddChild(grid.Expand());
        var side = Ui.VBox(6);
        _view = new BoardViewport { CustomMinimumSize = new Vector2(340, 300), SizeFlagsVertical = SizeFlags.ShrinkBegin };
        side.AddChild(_view);
        // ТЗ ч. 5, 7.4: one hex, its neighbours, steps of different heights (§89).
        var shape = new OptionButton { Name = "TerrainPreviewShape" };
        foreach (var v in Enum.GetValues<PreviewShape>()) shape.AddItem(Names.Of(v), (int)v);
        shape.Select((int)_shape);
        shape.ItemSelected += i =>
        {
            _shape = (PreviewShape)(int)i;
            Preview();
        };
        side.AddChild(Ui.HBox(6, Ui.Label("Показать", 13, Ui.TextDim), shape.Expand()));
        side.AddChild(Ui.Label("Предпросмотр: плитка, её соседи или ступени по кругу (высоты 0…6), вокруг — пол. Колесо — зум, Q/E — поворот.", 12, Ui.TextDim, wrap: true).MinSize(320));
        root.AddChild(side);
        AfterLayout(root, Preview);
        return root;
    }

    public override void _Process(double delta)
    {
        if (_rebuild < 0) return;
        _rebuild -= (float)delta;
        if (_rebuild < 0) Preview();
    }

    private void Preview()
    {
        if (_view == null || !IsInstanceValid(_view) || _terrain == null) return;
        try
        {
            var floor = Ctx.Db.Terrain(MapTemplates.DefaultFloor) != null ? MapTemplates.DefaultFloor : _terrain.Id;
            var map = MaterialPreview.Map(floor, _terrain.Id, _shape, _terrain.DefaultHeight);
            PreviewMap = map;
            var sim = new Simulation(Ctx.Db, map, new SimOptions { FogOfWar = false });
            _view.Bind(sim.Map, Ctx.Db, true);
        }
        catch (Exception e)
        {
            GD.PushWarning("Предпросмотр местности: " + e.Message);
        }
    }
}
