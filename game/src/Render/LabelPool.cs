using System.Collections.Generic;
using Godot;

namespace HexTactics.Game.Render;

/// <summary>Pool of billboard 3D labels for overlays (queue numbers, costs, warnings). Reset every frame.</summary>
public partial class LabelPool : Node3D
{
    private readonly List<Label3D> _labels = new();
    private int _used;

    public void Begin() => _used = 0;

    public Label3D Add(Vector3 pos, string text, Color color, int fontSize = 34, Color? outline = null)
    {
        Label3D l;
        if (_used < _labels.Count)
        {
            l = _labels[_used];
        }
        else
        {
            l = new Label3D
            {
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                FixedSize = true,
                PixelSize = 0.0009f,
                OutlineSize = 9,
                RenderPriority = 18,
                OutlineRenderPriority = 17,
                Shaded = false,
            };
            AddChild(l);
            _labels.Add(l);
        }
        _used++;
        l.Visible = true;
        l.Position = pos;
        l.Text = text;
        l.Modulate = color;
        l.FontSize = fontSize;
        l.OutlineModulate = outline ?? new Color(0, 0, 0, 0.85f);
        return l;
    }

    public void End()
    {
        for (int i = _used; i < _labels.Count; i++) _labels[i].Visible = false;
    }
}
