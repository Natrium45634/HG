using System;
using System.IO;
using Godot;
using HexTactics.Game.App;
using HexTactics.Game.Editor;
using HexTactics.Game.UI;

namespace HexTactics.Game.Battle;

/// <summary>The battles saved in the middle (part 6, T28; docs/DECISIONS_6.md §97): the newest first; «Загрузить» goes on from there.</summary>
public partial class SavedBattlesDialog : AcceptDialog
{
    private readonly ItemList _list = new() { Name = "SavedBattles" };
    private readonly bool _admin;
    private readonly Action? _onExit;

    private SavedBattlesDialog(bool admin, Action? onExit)
    {
        _admin = admin;
        _onExit = onExit;
        Title = "Сохранённые бои";
        OkButtonText = "Загрузить";
        Name = "SavedBattlesDialog";
        _list.CustomMinimumSize = new Vector2(520, 300);
        foreach (var path in BattleScreen.SavedBattles())
        {
            int i = _list.AddItem($"{Path.GetFileNameWithoutExtension(path)} · {File.GetLastWriteTime(path):dd.MM HH:mm}");
            _list.SetItemMetadata(i, path);
        }
        if (_list.ItemCount > 0) _list.Select(0);
        var v = Ui.VBox(6, Ui.Label(_list.ItemCount > 0 ? "Выберите бой (двойной клик — загрузить):" : "Сохранённых боёв пока нет — «Меню → Сохранить бой» во время боя.", 13, Ui.TextDim, wrap: true));
        v.AddChild(_list);
        AddChild(v);
        _list.ItemActivated += _ => LoadSelected();
        Confirmed += LoadSelected;
    }

    public static void Open(Node parent, bool admin, Action? onExit)
    {
        var dlg = new SavedBattlesDialog(admin, onExit);
        parent.AddChild(dlg);
        dlg.PopupCentered(new Vector2I(560, 400));
    }

    private void LoadSelected()
    {
        var sel = _list.GetSelectedItems();
        if (sel.Length == 0) return;
        string path = _list.GetItemMetadata(sel[0]).AsString();
        try
        {
            Main.Instance.ShowBattle(BattleScreen.Load(path, _admin, _onExit));
        }
        catch (Exception e)
        {
            Dialogs.Info(GetParent(), "Бой не загружен", e.Message);
        }
        QueueFree();
    }
}
