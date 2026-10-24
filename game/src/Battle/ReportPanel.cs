using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Sim;
using HexTactics.Game.UI;

namespace HexTactics.Game.Battle;

/// <summary>
/// The report of the battle (docs/DECISIONS_4.md §64): the same report as the autobattle, filled from the running
/// battle; saved to user://reports (JSON + text), apart from the content.
/// </summary>
public partial class ReportPanel : PanelContainer
{
    private readonly BattleScreen _bs;
    private readonly RichTextLabel _text;
    private readonly Label _saved;

    public ReportPanel(BattleScreen bs)
    {
        _bs = bs;
        Name = "ReportPanel";
        Visible = false;
        AddThemeStyleboxOverride("panel", Ui.Box(new Color(0.05f, 0.07f, 0.1f, 0.96f), new Color(0.4f, 0.65f, 1f, 0.9f), 1, 6, 12));
        var v = Ui.VBox(8);
        var head = Ui.HBox(6);
        head.AddChild(Ui.Label("Отчёт прогона", 18, Ui.Accent).Expand());
        head.AddChild(Ui.Button("Обновить", Show_, "Значения на текущий момент боя"));
        var save = Ui.Button("Сохранить", Save, "user://reports — JSON и текст");
        save.Name = "ReportSave";
        head.AddChild(save);
        head.AddChild(Ui.Button("✕", () => Visible = false, "Закрыть"));
        v.AddChild(head);
        _text = new RichTextLabel { Name = "ReportText", FitContent = false, ScrollActive = true, SelectionEnabled = true, CustomMinimumSize = new Vector2(760, 420) };
        _text.AddThemeFontSizeOverride("normal_font_size", 14);
        v.AddChild(_text);
        _saved = Ui.Label("", 12, Ui.TextDim, wrap: true);
        _saved.Name = "ReportSaved";
        v.AddChild(_saved);
        AddChild(v);
    }

    public string Text => _text.Text;
    public string SavedPath { get; private set; } = "";

    private void Show_() => Open();

    public void Open()
    {
        var r = _bs.Report.Fill(_bs.StopReason);
        _text.Text = r.ToText(_bs.Db);
        Visible = true;
    }

    public void Save()
    {
        var r = _bs.Report.Fill(_bs.StopReason);
        string dir = ProjectSettings.GlobalizePath("user://reports");
        SavedPath = AutoBattle.Save(dir, r, _bs.Db);
        _saved.Text = "Сохранено: " + SavedPath + ".json / .txt";
    }
}

/// <summary>«Остановка на событии» (docs/DECISIONS_4.md §64): what pauses the battle for diagnostics.</summary>
public partial class BreakpointsPanel : PanelContainer
{
    private readonly BattleScreen _bs;
    private readonly Dictionary<BreakpointKind, (CheckBox Box, LineEdit? Value)> _rows = new();

    public BreakpointsPanel(BattleScreen bs)
    {
        _bs = bs;
        Name = "BreakpointsPanel";
        Visible = false;
        AddThemeStyleboxOverride("panel", Ui.Box(new Color(0.06f, 0.06f, 0.09f, 0.96f), new Color(1f, 0.7f, 0.35f, 0.9f), 1, 6, 12));
        var v = Ui.VBox(6);
        var head = Ui.HBox(6);
        head.AddChild(Ui.Label("Остановка на событии", 17, Ui.Warn).Expand());
        head.AddChild(Ui.Button("✕", () => Visible = false, "Закрыть"));
        v.AddChild(head);
        v.AddChild(Ui.Label("Бой встаёт на паузу, когда событие произошло (причина — в баннере и журнале).", 12, Ui.TextDim, wrap: true));
        foreach (BreakpointKind k in Enum.GetValues(typeof(BreakpointKind)))
        {
            if (k == BreakpointKind.Condition) continue;
            var row = Ui.HBox(6);
            var box = new CheckBox { Text = EdUtil.EnumLabel(k), Name = "Break_" + k };
            box.Toggled += _ => Apply();
            row.AddChild(box.Expand());
            LineEdit? value = null;
            if (k is BreakpointKind.UnitDeath or BreakpointKind.Passive)
            {
                value = new LineEdit
                {
                    Name = "BreakValue_" + k, CustomMinimumSize = new Vector2(150, 0),
                    PlaceholderText = k == BreakpointKind.UnitDeath ? "тег, метка, категория" : "ID пассива",
                };
                value.TextChanged += _ => Apply();
                row.AddChild(value);
            }
            _rows[k] = (box, value);
            v.AddChild(row);
        }
        AddChild(v);
    }

    /// <summary>Turns a breakpoint on (UI tests, automation).</summary>
    public void Set(BreakpointKind k, bool on, string value = "")
    {
        var (box, edit) = _rows[k];
        if (edit != null) edit.Text = value;
        box.ButtonPressed = on;
        Apply();
    }

    private void Apply()
    {
        var list = _bs.Breaks.List;
        list.Clear();
        foreach (var (k, (box, value)) in _rows)
            if (box.ButtonPressed)
                list.Add(new BreakpointDef { Kind = k, Value = value?.Text.Trim() ?? "" });
    }
}
