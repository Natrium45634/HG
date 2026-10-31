using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Game.UI;

namespace HexTactics.Game.App;

/// <summary>
/// «Клавиши» (ТЗ ч. 6, 11.7): every reassignable action of the battle by groups with its keys; a click on the keys waits for a new
/// key (Esc — cancel), a key taken from another action is named; «↺» returns the default, «Все по умолчанию» — every one.
/// </summary>
public partial class HotkeysDialog : AcceptDialog
{
    private VBoxContainer _rows = null!;
    private Label _note = null!;
    /// <summary>The action waiting for a key ("" — none).</summary>
    public string Capturing { get; private set; } = "";

    public override void _Ready()
    {
        Title = "Клавиши";
        OkButtonText = "Готово";
        Name = "HotkeysDialog";
        var box = Ui.VBox(6);
        box.AddChild(Ui.Label("Нажмите на клавиши действия и затем новую клавишу (Esc — отмена). Esc в бою и клавиши камеры не переназначаются.", 12, Ui.TextDim, wrap: true));
        _note = Ui.Label("", 12, Ui.Warn, wrap: true);
        _note.Name = "HotkeysNote";
        box.AddChild(_note);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(560, 460), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _rows = Ui.VBox(3);
        scroll.AddChild(_rows.Expand());
        box.AddChild(scroll);
        box.AddChild(Ui.Button("Все по умолчанию", () =>
        {
            Hotkeys.ResetAll();
            _note.Text = "Все клавиши — по умолчанию.";
            Rebuild();
        }));
        AddChild(box);
        Rebuild();
        Confirmed += () =>
        {
            Main.Instance.Settings.Save();
            QueueFree();
        };
        Canceled += () =>
        {
            Main.Instance.Settings.Save();
            QueueFree();
        };
    }

    private void Rebuild()
    {
        Ui.ClearChildren(_rows);
        foreach (var g in Hotkeys.All.GroupBy(a => a.Group))
        {
            _rows.AddChild(Ui.Label(g.Key, 14, Ui.Accent));
            foreach (var a in g)
            {
                var row = Ui.HBox(6);
                row.AddChild(Ui.Label(a.Label, 13, Ui.Text).MinSize(300));
                bool own = Main.Instance.Settings.KeyBindings.ContainsKey(a.Id);
                string keys = Hotkeys.Label(a.Id);
                var b = Ui.Button(Capturing == a.Id ? "Нажмите клавишу…" : keys == "" ? "— нет —" : keys, () => StartCapture(a.Id), "Нажмите и затем новую клавишу");
                b.Name = "Hotkey_" + a.Id;
                b.CustomMinimumSize = new Vector2(150, 0);
                if (own) b.AddThemeColorOverride("font_color", Ui.Accent);
                row.AddChild(b);
                var reset = Ui.Button("↺", () =>
                {
                    Hotkeys.Reset(a.Id);
                    _note.Text = $"«{a.Label}» — по умолчанию: {Hotkeys.Label(a.Id)}";
                    Rebuild();
                }, "По умолчанию: " + string.Join("/", a.Defaults.Select(Hotkeys.Show)));
                reset.Disabled = !own;
                row.AddChild(reset);
                _rows.AddChild(row);
            }
        }
    }

    public void StartCapture(string id)
    {
        Capturing = id;
        _note.Text = $"«{Hotkeys.Find(id)?.Label}»: нажмите новую клавишу (Esc — отмена)";
        Rebuild();
    }

    public override void _Input(InputEvent e)
    {
        if (Capturing == "" || e is not InputEventKey { Pressed: true, Echo: false } k) return;
        GetViewport().SetInputAsHandled();
        Take(k);
    }

    /// <summary>The captured key (also the UI tests' way in).</summary>
    public void Take(InputEventKey k)
    {
        if (Capturing == "") return;
        var id = Capturing;
        if (k.Keycode == Key.Escape)
        {
            Capturing = "";
            _note.Text = "Отменено.";
            Rebuild();
            return;
        }
        if (!Hotkeys.Bindable(k)) return;
        var binding = Hotkeys.FromEvent(k);
        var lost = Hotkeys.Assign(id, binding);
        Capturing = "";
        _note.Text = $"«{Hotkeys.Find(id)?.Label}» — {Hotkeys.Show(binding)}" + (lost.Count > 0 ? $"; клавиша снята с: {string.Join(", ", lost.Select(l => $"«{l}»"))}" : "");
        Rebuild();
    }
}
