using System;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Hex;
using HexTactics.Core.Sim;
using HexTactics.Game.Render;

namespace HexTactics.Game.Editor;

/// <summary>3D board inside a SubViewport for editors (map / scenario). Forwards mouse input as hex events.</summary>
public partial class BoardViewport : SubViewportContainer
{
    private readonly SubViewport _vp = new() { HandleInputLocally = false, Msaa3D = Viewport.Msaa.Msaa2X, OwnWorld3D = true };
    public BoardView Board { get; } = new() { Embedded = true };
    public LabelPool Labels { get; } = new();

    public event Action<HexCoord?, Cell?, InputEventMouseButton>? Clicked;
    public event Action<HexCoord?, Cell?>? Hovered;
    public event Action<HexCoord?, Cell?, MouseButtonMask>? Dragged;

    public HexCoord? HoverHex { get; private set; }
    public Cell? HoverCell { get; private set; }
    private bool _drag;

    public BoardViewport()
    {
        Stretch = true;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.Click;
        AddChild(_vp);
        _vp.AddChild(Board);
        Board.AddChild(Labels);
    }

    public override void _Ready()
    {
        Board.ApplySettings(App.Main.Instance.Settings);
        Board.Rig.EdgeScroll = false;
    }

    public void Bind(BattleMap map, ContentDatabase db, bool frame)
    {
        Board.Bind(map, db);
        if (!frame) return;
        Board.FrameCamera();
    }

    public override void _GuiInput(InputEvent e)
    {
        Board.Rig.KeyboardPan = HasFocus() || GetGlobalRect().HasPoint(GetGlobalMousePosition());
        if (Board.Rig.HandleInput(e))
        {
            AcceptEvent();
            return;
        }
        switch (e)
        {
            case InputEventMouseMotion mm:
            {
                HoverCell = Board.PickCell(mm.Position);
                HoverHex = HoverCell?.Coord ?? Board.PickHex(mm.Position);
                Hovered?.Invoke(HoverHex, HoverCell);
                if (_drag && (mm.ButtonMask & (MouseButtonMask.Left | MouseButtonMask.Right)) != 0) Dragged?.Invoke(HoverHex, HoverCell, mm.ButtonMask);
                break;
            }
            case InputEventMouseButton mb when mb.ButtonIndex is MouseButton.Left or MouseButton.Right:
                GrabFocus();
                if (mb.Pressed)
                {
                    HoverCell = Board.PickCell(mb.Position);
                    HoverHex = HoverCell?.Coord ?? Board.PickHex(mb.Position);
                    _drag = true;
                    Clicked?.Invoke(HoverHex, HoverCell, mb);
                }
                else
                {
                    _drag = false;
                    Clicked?.Invoke(HoverHex, HoverCell, mb);
                }
                AcceptEvent();
                break;
        }
    }
}
