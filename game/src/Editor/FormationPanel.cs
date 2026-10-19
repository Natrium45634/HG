using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// «Формации» (docs/DECISIONS_3.md §44): own formations by relative positions. The grid shows the order's
/// point in the centre and the slots numbered in filling order (movement to the east); a click adds or
/// removes a slot, the arrow shows how the slots turn with the direction of movement.
/// </summary>
public partial class FormationPanel : DefListPanel
{
    private HexCanvas? _canvas;
    private PropertyGrid? _grid;

    public FormationPanel(EditorContext ctx) : base(ctx, DefCategory.Formation, "Формации групп", null,
        () => new FormationDef { Name = "Новая формация", Slots = { new FormationSlotDef() } })
    {
    }

    protected override Control BuildDetail(Def d)
    {
        var f = (FormationDef)d;
        var root = Ui.HBox(10).Expand(true, true);
        _grid = new PropertyGrid();
        _grid.SetTarget(f, Ctx.Db);
        _grid.Changed += () =>
        {
            OnEdited(f);
            Paint(f);
        };
        root.AddChild(_grid.Expand(true));
        var side = Ui.VBox(6).MinSize(380);
        side.AddChild(Ui.Label("Слоты: клик — добавить/убрать; номер — порядок заполнения. Центр — точка приказа, движение — на восток (→); стрелки по краю — повернуть превью.", 12, Ui.TextDim, wrap: true));
        _canvas = new HexCanvas { Radius = 4, CustomMinimumSize = new Vector2(370, 330), ShowCasterGhost = true };
        _canvas.CellClicked += (c, button) =>
        {
            if (f.Kind != FormationKind.Slots) return;
            var off = Unrotate(c, _canvas.Orientation);
            int i = f.Slots.FindIndex(s => s.Offset == off);
            if (i >= 0) f.Slots.RemoveAt(i);
            else f.Slots.Add(new FormationSlotDef { Offset = off });
            OnEdited(f);
            _grid.SetTarget(f, Ctx.Db);
            Paint(f);
        };
        _canvas.DirectionClicked += dir =>
        {
            _canvas.Orientation = dir;
            Paint(f);
        };
        side.AddChild(_canvas);
        root.AddChild(side);
        Paint(f);
        return root;
    }

    private static HexCoord Unrotate(HexCoord c, HexDirection heading) => c.Rotate(-(int)heading);

    private void Paint(FormationDef f)
    {
        if (_canvas == null) return;
        _canvas.Cells.Clear();
        _canvas.Labels.Clear();
        _canvas.Marked.Clear();
        var heading = f.RotateWithDirection ? _canvas.Orientation : HexDirection.E;
        switch (f.Kind)
        {
            case FormationKind.Slots:
                for (int i = 0; i < f.Slots.Count; i++)
                {
                    var c = Core.Sim.GroupPlanner.Rotate(f.Slots[i].Offset, heading);
                    _canvas.Cells[c] = f.Slots[i].Role == UnitRole.Auto ? Ui.Accent : new Color(0.5f, 0.8f, 1f);
                    _canvas.Labels[c] = (i + 1).ToString();
                }
                break;
            case FormationKind.Surround:
            case FormationKind.Perimeter:
                foreach (var c in HexCoord.Ring(HexCoord.Zero, f.Radius)) _canvas.Cells[c] = Ui.Accent;
                break;
            default:
                _canvas.Cells[HexCoord.Zero] = Ui.Accent;
                foreach (var c in HexCoord.Ring(HexCoord.Zero, 1)) _canvas.Cells[c] = new Color(Ui.Accent, 0.5f);
                break;
        }
        _canvas.QueueRedraw();
    }
}
