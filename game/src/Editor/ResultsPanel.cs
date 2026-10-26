using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using HexTactics.Core.Sim;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// «Результаты» (ТЗ ч. 5, 25.7; docs/DECISIONS_5.md §89): the saved reports of the TD scene, the tests and the calibration —
/// their numbers and text, two of them compared (B against A, the older one is A), the comparison and the summary exported as CSV.
/// </summary>
public partial class ResultsPanel : VBoxContainer
{
    public static string Folder => ProjectSettings.GlobalizePath("user://reports");

    private ItemList _list = null!;
    private OptionButton _kind = null!;
    private TextEdit _detail = null!;
    private VBoxContainer _compare = null!;
    private Label _status = null!;
    private readonly List<ResultEntry> _shown = new();

    /// <summary>Everything read from the folder, the newest first (UI tests).</summary>
    public List<ResultEntry> Entries { get; private set; } = new();
    public List<string> Problems { get; private set; } = new();
    public ResultComparison? LastComparison { get; private set; }
    public string Exported { get; private set; } = "";

    public ResultsPanel()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    public override void _Ready()
    {
        var head = Ui.HBox(8);
        head.AddChild(Ui.Label("Результаты", 18, Ui.Accent));
        _kind = new OptionButton { Name = "ResultsKind" };
        foreach (var k in new[] { "Все", "Пакеты", "Бои", "Калибровки" }) _kind.AddItem(k);
        _kind.ItemSelected += _ => Fill();
        head.AddChild(_kind);
        Btn(head, "Обновить", "ResultsRefresh", Refresh, "Перечитать папку отчётов");
        Btn(head, "Экспорт сравнения CSV", "ResultsExportComparison", () => ExportComparison(), "Сравнение двух отмеченных результатов — таблица в папку отчётов");
        Btn(head, "Сводная таблица CSV", "ResultsExportSummary", () => ExportSummary(), "Отмеченные результаты (или все показанные) — строка на результат, столбец на показатель");
        Btn(head, "Открыть папку", "ResultsOpenFolder", () =>
        {
            Directory.CreateDirectory(Folder);
            OS.ShellOpen(Folder);
        }, Folder);
        AddChild(head);
        AddChild(Ui.Label("Отметьте один результат — его отчёт; два (Ctrl+клик) — сравнение: A — более ранний, B — более поздний. " +
                          "Разница доли побед внутри 95 % интервала обоих пакетов не считается изменением.", 12, Ui.TextDim, wrap: true));

        var split = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SplitOffset = 440 };
        var left = Ui.VBox(4);
        _list = new ItemList { Name = "ResultsList", SelectMode = ItemList.SelectModeEnum.Multi, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(420, 0) };
        _list.MultiSelected += (_, _) => ShowSelection();
        _list.ItemSelected += _ => ShowSelection();
        left.AddChild(_list);
        _status = Ui.Label("", 12, Ui.TextDim, wrap: true);
        _status.Name = "ResultsStatus";
        left.AddChild(_status);
        split.AddChild(left);

        var right = Ui.VBox(6).Expand(true, true);
        _detail = new TextEdit { Name = "ResultsDetail", Editable = false, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 220), WrapMode = TextEdit.LineWrappingMode.Boundary };
        right.AddChild(_detail);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, 200) };
        _compare = Ui.VBox(2).Expand();
        _compare.Name = "ResultsCompare";
        scroll.AddChild(_compare);
        right.AddChild(scroll);
        split.AddChild(right);
        AddChild(split);
        Refresh();
    }

    private static void Btn(Container box, string text, string name, Action a, string tip)
    {
        var b = Ui.Button(text, a, tip);
        b.Name = name;
        box.AddChild(b);
    }

    public void Refresh()
    {
        (Entries, Problems) = RunResults.Load(Folder);
        Fill();
    }

    private void Fill()
    {
        _list.Clear();
        _shown.Clear();
        int k = _kind.Selected;
        foreach (var e in Entries.Where(e => k <= 0 || (int)e.Kind == k - 1))
        {
            _shown.Add(e);
            _list.AddItem(e.Title);
            _list.SetItemTooltip(_shown.Count - 1, e.File);
        }
        _status.Text = $"Результатов: {_shown.Count} из {Entries.Count} · папка {Folder}" +
                       (Problems.Count > 0 ? "\n⚠ Не прочитаны: " + string.Join("; ", Problems) : "");
        _detail.Text = _shown.Count == 0 ? "Сохранённых результатов нет: запустите автотест или пакет в TD-сцене, тест сценария или калибровку в «Обзоре проекта»." : "";
        ShowCompare(null);
    }

    /// <summary>Marks results by their place in the list (UI tests) and shows them.</summary>
    public void SelectShown(params int[] indices)
    {
        _list.DeselectAll();
        foreach (var i in indices) _list.Select(i, false);
        ShowSelection();
    }

    private List<ResultEntry> Selected() => _list.GetSelectedItems().Where(i => i < _shown.Count).Select(i => _shown[i]).ToList();

    private void ShowSelection()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        var e = sel[0];
        var text = e.Text();
        var files = e.Files().Select(Path.GetFileName).ToList();
        _detail.Text = (sel.Count > 1 ? $"Отмечено: {sel.Count}. Отчёт первого:\n\n" : "") + text + (files.Count > 0 ? "\nФайлы: " + string.Join(", ", files) : "");
        ShowCompare(sel.Count == 2 ? RunResults.Compare(sel.OrderBy(x => x.When).First(), sel.OrderBy(x => x.When).Last()) : null);
    }

    private void ShowCompare(ResultComparison? c)
    {
        LastComparison = c;
        Ui.ClearChildren(_compare);
        if (c == null) return;
        _compare.AddChild(Ui.Label("Сравнение", 15, Ui.Accent));
        _compare.AddChild(Ui.Label("A: " + c.A.Title, 12, Ui.TextDim, wrap: true));
        _compare.AddChild(Ui.Label("B: " + c.B.Title, 12, Ui.TextDim, wrap: true));
        foreach (var n in c.Notes) _compare.AddChild(Ui.Label(n, 12, n.StartsWith("⚠") ? Ui.Warn : Ui.TextDim, wrap: true));
        var grid = new GridContainer { Columns = 5, Name = "ResultsCompareGrid" };
        grid.AddThemeConstantOverride("h_separation", 14);
        foreach (var h in new[] { "Показатель", "A", "B", "Разница", "Итог" }) grid.AddChild(Ui.Label(h, 12, Ui.TextDim));
        foreach (var r in c.Rows)
        {
            var color = r.Verdict is "лучше" or "прогноз точнее" ? Ui.Good : r.Verdict is "хуже" or "прогноз хуже" ? Ui.Bad : Ui.Text;
            grid.AddChild(Ui.Label(r.Label, 13).MinSize(260));
            grid.AddChild(Ui.Label(r.AText, 13));
            grid.AddChild(Ui.Label(r.BText, 13));
            grid.AddChild(Ui.Label(r.Delta is { } d && Math.Abs(d) > 1e-9 ? (d > 0 ? "+" : "−") + Math.Abs(d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "", 13));
            grid.AddChild(Ui.Label(r.Verdict, 13, color));
        }
        _compare.AddChild(grid);
    }

    /// <summary>The comparison of the two marked results as CSV in the folder of reports; its path ("" — nothing to export).</summary>
    public string ExportComparison()
    {
        if (LastComparison == null)
        {
            _status.Text = "Отметьте два результата для сравнения";
            return "";
        }
        Directory.CreateDirectory(Folder);
        Exported = Path.Combine(Folder, $"comparison_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        ReportCsv.Write(Exported, LastComparison.ToCsv());
        _status.Text = "Сравнение сохранено: " + Exported;
        return Exported;
    }

    /// <summary>The marked results (or every shown) as one table.</summary>
    public string ExportSummary()
    {
        var sel = Selected();
        if (sel.Count == 0) sel = _shown.ToList();
        if (sel.Count == 0) return "";
        Directory.CreateDirectory(Folder);
        Exported = Path.Combine(Folder, $"results_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        ReportCsv.Write(Exported, RunResults.SummaryCsv(sel));
        _status.Text = $"Сводная таблица ({sel.Count}) сохранена: {Exported}";
        return Exported;
    }
}
