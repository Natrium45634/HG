using Godot;
using HexTactics.Game.UI;

namespace HexTactics.Game.App;

/// <summary>User settings dialog (camera tilt, interface, graphics).</summary>
public partial class SettingsDialog : AcceptDialog
{
    public override void _Ready()
    {
        Title = "Настройки";
        OkButtonText = "Сохранить";
        var settings = Main.Instance.Settings;
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(520, 520), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var grid = new PropertyGrid { LabelWidth = 260 };
        grid.SetTarget(settings, null);
        scroll.AddChild(grid);
        AddChild(scroll);
        Confirmed += () =>
        {
            settings.Save();
            Main.Instance.ApplyWindowSettings();
            QueueFree();
        };
        Canceled += QueueFree;
    }
}
