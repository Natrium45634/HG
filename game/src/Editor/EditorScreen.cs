using Godot;
using HexTactics.Game.App;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>Administrative mode (editors). Filled in during stage 5.</summary>
public partial class EditorScreen : Control
{
    public override void _Ready()
    {
        Theme = Main.Instance.Theme;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(Ui.VBox(8, Ui.Label("Редактор"), Ui.Button("Назад", () => Main.Instance.ShowMainMenu())));
    }
}
