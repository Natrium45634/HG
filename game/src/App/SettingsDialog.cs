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
        var box = Ui.VBox(6);
        // Part 6 (ТЗ 11.7): the keys of the battle.
        var keys = Ui.Button("Клавиши…", () => OpenHotkeys(), "Переназначить клавиши боя");
        keys.Name = "OpenHotkeys";
        box.AddChild(keys);
        box.AddChild(grid);
        scroll.AddChild(box.Expand());
        AddChild(scroll);
        Confirmed += () =>
        {
            settings.Save();
            Main.Instance.ApplyWindowSettings();
            QueueFree();
        };
        Canceled += QueueFree;
    }

    /// <summary>The dialog of the keys over this one (a child window: one exclusive window at a time).</summary>
    public HotkeysDialog OpenHotkeys()
    {
        var d = new HotkeysDialog();
        AddChild(d);
        d.PopupCentered(new Vector2I(640, 640));
        return d;
    }
}
