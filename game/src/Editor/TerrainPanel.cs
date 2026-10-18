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
        side.AddChild(Ui.Label("Предпросмотр: плитка в центре, вокруг — пол. Колесо — зум, Q/E — поворот.", 12, Ui.TextDim, wrap: true).MinSize(320));
        root.AddChild(side);
        Callable.From(Preview).CallDeferred();
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
            var map = MapTemplates.Generate("terrain_preview", MapShape.Hexagon, 2, 0, floor);
            foreach (var c in map.Cells)
                if (c.C == HexCoord.Zero || c.C == new HexCoord(1, 0))
                {
                    c.T = _terrain.Id;
                    c.H = _terrain.DefaultHeight;
                }
            var sim = new Simulation(Ctx.Db, map, new SimOptions { FogOfWar = false });
            _view.Bind(sim.Map, Ctx.Db, true);
        }
        catch (Exception e)
        {
            GD.PushWarning("Предпросмотр местности: " + e.Message);
        }
    }
}
