using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Sim;
using HexTactics.Game.App;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// «Обзор проекта» (ТЗ ч. 5, 25.1; docs/DECISIONS_5.md §84): every catalogue with its count, what is new or changed in
/// the draft, the problems by catalogue, the broken references and the fields of other versions — each with a jump to
/// its object.
/// </summary>
public partial class OverviewPanel : VBoxContainer
{
    private readonly EditorContext _ctx;
    private VBoxContainer _body = null!;

    public OverviewPanel(EditorContext ctx)
    {
        _ctx = ctx;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    public override void _Ready()
    {
        var head = Ui.HBox(8);
        head.AddChild(Ui.Label("Обзор проекта", 18, Ui.Accent));
        head.AddChild(Ui.Button("Проверить сейчас", () => _ctx.Validate()));
        var cal = Ui.Button("Калибровка оценки силы…", () => StartCalibration(),
            "ТЗ ч. 5, 24: эталоны (воин, танк, рейдер, стрелок, лекарь, контролёр, нежить, вожак один и со стаей) дерутся попарно на нескольких seed; " +
            "прогноз по силе сравнивается с долей побед, плохо предсказанные пары — в начале отчёта. Черновик не меняется.");
        cal.Name = "OverviewCalibration";
        head.AddChild(cal);
        _calStatus = Ui.Label("", 12, Ui.TextDim);
        head.AddChild(_calStatus);
        AddChild(head);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _body = Ui.VBox(6).Expand();
        scroll.AddChild(_body);
        AddChild(scroll);
        _ctx.Validated += Refresh;
        Refresh();
    }

    public override void _ExitTree() => _ctx.Validated -= Refresh;

    private Label _calStatus = null!;
    private Task<CalibrationReport>? _calibration;
    private int _calDone, _calTotal;

    /// <summary>The last calibration (UI tests) and where its CSV was saved.</summary>
    public CalibrationReport? LastCalibration { get; private set; }
    public string CalibrationSaved { get; private set; } = "";

    /// <summary>Starts the calibration in the background on a copy of the draft.</summary>
    public void StartCalibration(int runs = 6)
    {
        if (_calibration is { IsCompleted: false }) return;
        var db = _ctx.Db.Clone();
        var refs = Calibration.DefaultSet(db);
        if (refs.Count < 2)
        {
            _calStatus.Text = "Эталонов меньше двух — калибровать нечего";
            return;
        }
        _calDone = 0;
        _calibration = Task.Run(() => Calibration.Run(db, refs, runs, 1000, progress: (d, t) =>
        {
            _calDone = d;
            _calTotal = t;
        }));
    }

    public override void _Process(double delta)
    {
        if (_calibration == null) return;
        if (!_calibration.IsCompleted)
        {
            _calStatus.Text = $"Калибровка: {_calDone}/{_calTotal}";
            return;
        }
        var t = _calibration;
        _calibration = null;
        if (t.IsFaulted)
        {
            _calStatus.Text = "Калибровка прервана: " + t.Exception?.GetBaseException().Message;
            return;
        }
        LastCalibration = t.Result;
        var dir = ProjectSettings.GlobalizePath("user://reports");
        System.IO.Directory.CreateDirectory(dir);
        CalibrationSaved = System.IO.Path.Combine(dir, $"calibration_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        ReportCsv.Write(CalibrationSaved, t.Result.ToCsv());
        // The text beside the table: the screen «Результаты» shows it (§89).
        System.IO.File.WriteAllText(System.IO.Path.ChangeExtension(CalibrationSaved, ".txt"), t.Result.ToText());
        _calStatus.Text = $"Калибровка: средняя ошибка {t.Result.MeanAbsError:P0}, плохих пар {t.Result.Bad.Count()}";
        Dialogs.Info(this, "Калибровка оценки силы", t.Result.ToText() + "\nCSV: " + CalibrationSaved);
    }

    public ProjectOverview? Last { get; private set; }

    private void Refresh()
    {
        if (!IsInstanceValid(_body)) return;
        Ui.ClearChildren(_body);
        var o = ProjectOverview.Build(_ctx.Db, Main.Instance.Content.Published, _ctx.Issues);
        Last = o;
        var summary = Ui.Label($"Объектов {o.Objects} · новых в черновике {o.New} · изменённых {o.Changed} · ошибок {o.Errors} · предупреждений {o.Warnings} · " +
                               Main.Instance.Content.SourceDescription, 14, o.Errors > 0 ? Ui.Bad : Ui.Text, wrap: true);
        summary.Name = "OverviewSummary";
        _body.AddChild(summary);

        var grid = new GridContainer { Columns = 7, Name = "OverviewTable" };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 2);
        foreach (var h in new[] { "Каталог", "Всего", "Новых", "Изменено", "Неполных", "Ошибок", "Предупр." }) grid.AddChild(Ui.Label(h, 13, Ui.TextDim));
        foreach (var c in o.Categories)
        {
            var cat = c.Category;
            var b = Ui.Button(EditorScreen.SectionTitle(cat), () => _ctx.Navigate(cat, _ctx.Db.Table(cat).Items.FirstOrDefault()?.Id ?? ""), "Открыть раздел");
            b.Alignment = HorizontalAlignment.Left;
            b.Flat = true;
            b.Name = "Open_" + cat;
            grid.AddChild(b);
            grid.AddChild(Ui.Label(c.Total.ToString(), 13));
            grid.AddChild(Ui.Label(c.New > 0 ? c.New.ToString() : "—", 13, c.New > 0 ? Ui.Accent : Ui.TextDim));
            grid.AddChild(Ui.Label(c.Changed > 0 ? c.Changed.ToString() : "—", 13, c.Changed > 0 ? new Color(0.62f, 0.8f, 1f) : Ui.TextDim));
            grid.AddChild(Ui.Label(c.Incomplete > 0 ? c.Incomplete.ToString() : "—", 13, c.Incomplete > 0 ? Ui.Warn : Ui.TextDim));
            grid.AddChild(Ui.Label(c.Errors > 0 ? c.Errors.ToString() : "—", 13, c.Errors > 0 ? Ui.Bad : Ui.TextDim));
            grid.AddChild(Ui.Label(c.Warnings > 0 ? c.Warnings.ToString() : "—", 13, c.Warnings > 0 ? Ui.Warn : Ui.TextDim));
        }
        _body.AddChild(grid);

        Problems("Битые ссылки", o.BrokenReferences, "Broken_", "Ссылок на несуществующие объекты нет.");
        Problems("Поля другой версии программы (сохраняются без изменений)", o.OtherVersionFields, "Foreign_", "Нет.");
    }

    private void Problems(string title, System.Collections.Generic.List<ContentIssue> list, string prefix, string none)
    {
        _body.AddChild(Ui.Sep());
        _body.AddChild(Ui.Label($"{title}: {list.Count}", 15, list.Count > 0 ? Ui.Warn : Ui.Accent));
        if (list.Count == 0) _body.AddChild(Ui.Label(none, 13, Ui.TextDim));
        int n = 0;
        foreach (var i in list.Take(60))
        {
            var target = ProjectOverview.Target(i);
            var label = target is { } t ? $"{ContentValidator.CatName(t.Category)} «{_ctx.Db.Table(t.Category).Get(t.Id)?.DisplayName ?? t.Id}»" : i.Source;
            var b = Ui.Button($"{label}: {i.Path} — {i.Message}", () =>
            {
                if (target is { } tt) _ctx.Navigate(tt.Category, tt.Id);
            }, "Перейти к объекту");
            b.Alignment = HorizontalAlignment.Left;
            b.ClipText = true;
            b.Name = prefix + n++;
            _body.AddChild(b);
        }
    }
}
