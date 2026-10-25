using System.Linq;
using Godot;
using HexTactics.Core.Content;
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
        AddChild(head);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _body = Ui.VBox(6).Expand();
        scroll.AddChild(_body);
        AddChild(scroll);
        _ctx.Validated += Refresh;
        Refresh();
    }

    public override void _ExitTree() => _ctx.Validated -= Refresh;

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
