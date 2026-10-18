using Godot;
namespace HexTactics.Game;
public partial class Main : Node
{
    public override void _Ready() { GD.Print("HexTactics ok, format=" + HexTactics.Core.Version.Format); GetTree().Quit(); }
}
