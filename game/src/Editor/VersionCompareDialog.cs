using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.App;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// «Сравнение версий» of a template (docs/DECISIONS_4.md §70): two builds side by side by section, the changed rows
/// highlighted, and on request the trial battles of both against the same reference units with a verdict.
/// </summary>
public static class VersionCompareDialog
{
    public const string Title = "Сравнение версий";

    /// <summary><paramref name="other"/> — an older version (null — the published one) against the draft.</summary>
    public static void Show(Node parent, EditorContext ctx, UnitDef draft, UnitDef? other = null, string otherLabel = "")
    {
        var cm = Main.Instance.Content;
        other ??= cm.Published.Unit(draft.Id);
        if (other == null)
        {
            Dialogs.Info(parent, Title, $"У «{draft.DisplayName}» нет опубликованной версии: сравнивать не с чем. Сравнение с прежними версиями — «Версии…».");
            return;
        }
        string a = otherLabel != "" ? otherLabel : $"опубликованная v{other.Version}";
        const string b = "черновик";
        var cmp = VersionCompare.Compare(ctx.Db, other, draft, a, b);
        var v = Ui.VBox(3);
        v.AddChild(Ui.Label($"«{draft.DisplayName}»: {a} → {b}. Изменённое выделено.", 14, Ui.Text, wrap: true).MinSize(720));
        var grid = new GridContainer { Columns = 3, Name = "CompareGrid" };
        grid.AddThemeConstantOverride("h_separation", 16);
        int i = 0;
        foreach (var sec in cmp.Lines.GroupBy(l => l.Section))
        {
            grid.AddChild(Ui.Label(sec.Key, 13, Ui.Accent));
            grid.AddChild(Ui.Label(a, 12, Ui.TextDim));
            grid.AddChild(Ui.Label(b, 12, Ui.TextDim));
            foreach (var l in sec)
            {
                var col = l.Changed ? Ui.Warn : Ui.TextDim;
                var name = Ui.Label((l.Changed ? "≠ " : "   ") + l.Label, 12, col);
                name.Name = $"CompareLine_{i++}";
                grid.AddChild(name);
                grid.AddChild(Ui.Label(l.A, 12, col, wrap: true).MinSize(220));
                grid.AddChild(Ui.Label(l.B, 12, l.Changed ? Ui.Text : Ui.TextDim, wrap: true).MinSize(220));
            }
        }
        v.AddChild(grid);
        v.AddChild(Ui.Sep());
        var duel = Ui.Label("Пробные бои не запускались: обе версии против одних и тех же эталонных юнитов, одинаковые seed.", 12, Ui.TextDim, wrap: true).MinSize(720);
        duel.Name = "CompareDuel";
        var run = Ui.Button("Пробные бои (5 прогонов)", () =>
        {
            var full = VersionCompare.Compare(ctx.Db, other, draft, a, b, duelRuns: 5);
            duel.Text = $"{a}: {full.DuelA!.ToText()}\n{b}: {full.DuelB!.ToText()}\nИтог: {full.Verdict}";
            duel.AddThemeColorOverride("font_color", Ui.Text);
        }, "Показывает, стал ли шаблон действительно сильнее или бой только удлинился");
        run.Name = "CompareRun";
        v.AddChild(run);
        v.AddChild(duel);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(760, 480), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(v.Expand());
        Dialogs.Custom(parent, Title, scroll, "Закрыть", () => true, new Vector2I(800, 600));
    }
}
