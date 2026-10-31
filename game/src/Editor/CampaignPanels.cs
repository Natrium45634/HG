using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// «Кампании» (ТЗ ч. 6, 9; docs/DECISIONS_6.md §103): the fields of the campaign, the check of its graph (rings, unreachable
/// missions, links, variants of other scenarios) and its scheme — missions over the scenarios, not copies of them.
/// </summary>
public partial class CampaignPanel : DefListPanel
{
    public CampaignPanel(EditorContext ctx) : base(ctx, DefCategory.Campaign, "Кампании (задания поверх готовых сценариев)", null,
        () => new CampaignDef { Name = "Новая кампания" })
    {
    }

    /// <summary>The scheme shown now (UI tests).</summary>
    public RuleGraphCanvas? Scheme { get; private set; }

    protected override Control BuildDetail(Def d)
    {
        var c = (CampaignDef)d;
        var box = Ui.VBox(6);
        var check = Ui.VBox(2);
        check.Name = "CampaignCheck";
        Scheme = new RuleGraphCanvas { Name = "CampaignScheme", ReadOnly = true, SchemeTitles = true, CustomMinimumSize = new Vector2(0, 280), MinimapEnabled = false };
        Scheme.SizeFlagsVertical = SizeFlags.Fill;
        void Refresh()
        {
            if (!IsInstanceValid(check)) return;
            Ui.ClearChildren(check);
            var problems = Campaigns.Check(Ctx.Db, c);
            check.AddChild(Ui.Label(problems.Count == 0 ? $"✓ Заданий {c.Nodes.Count}, без ошибок" : $"Ошибок: {problems.Count(p => p.Error)}, предупреждений: {problems.Count(p => !p.Error)}",
                13, problems.Count == 0 ? Ui.Good : problems.Any(p => p.Error) ? Ui.Bad : Ui.Warn));
            foreach (var p in problems)
                check.AddChild(Ui.Label((p.Error ? "⛔ " : "⚠ ") + p.Message, 12, p.Error ? Ui.Bad : Ui.Warn, wrap: true));
            Scheme.Marks.Clear();
            foreach (var s in c.StartNodes) Scheme.Marks[s] = Ui.Accent;
            Scheme.Show(Campaigns.Scheme(c, null, Ctx.Db));
        }
        var grid = new PropertyGrid();
        grid.SetTarget(c, Ctx.Db);
        grid.Changed += () =>
        {
            OnEdited(c);
            Refresh();
        };
        box.AddChild(check);
        box.AddChild(Ui.Label("Схема: стартовые задания — жёлтые; связи — «далее». Задания ссылаются на сценарии и варианты, не копируют их.", 12, Ui.TextDim, wrap: true));
        box.AddChild(Scheme);
        box.AddChild(grid);
        Refresh();
        return box;
    }
}

/// <summary>«Наборы наград» (ТЗ ч. 6, 10): the entries, their problems and the preview — what each gives and how often.</summary>
public partial class RewardSetPanel : DefListPanel
{
    public RewardSetPanel(EditorContext ctx) : base(ctx, DefCategory.RewardSet, "Наборы наград заданий кампаний", null, () => new RewardSetDef { Name = "Новые награды" })
    {
    }

    protected override Control BuildDetail(Def d)
    {
        var set = (RewardSetDef)d;
        var box = Ui.VBox(6);
        var preview = Ui.VBox(2);
        preview.Name = "RewardPreview";
        void Refresh()
        {
            if (!IsInstanceValid(preview)) return;
            Ui.ClearChildren(preview);
            preview.AddChild(Ui.Label("Предпросмотр выдачи:", 13, Ui.Accent));
            foreach (var line in Campaigns.Preview(set)) preview.AddChild(Ui.Label("• " + line, 13, Ui.Text, wrap: true));
            if (set.Entries.Count == 0) preview.AddChild(Ui.Label("Наград нет.", 12, Ui.TextDim));
            foreach (var p in Campaigns.Check(Ctx.Db, set))
                preview.AddChild(Ui.Label((p.Error ? "⛔ " : "⚠ ") + p.Message, 12, p.Error ? Ui.Bad : Ui.Warn, wrap: true));
            var users = Ctx.Db.All<CampaignDef>().SelectMany(c => c.Nodes.Where(n => n.RewardSet == set.Id).Select(n => $"«{c.DisplayName}» → {n.Id}")).ToList();
            preview.AddChild(Ui.Label(users.Count == 0 ? "Ни одно задание не выдаёт этот набор." : "Выдают: " + string.Join(", ", users), 12, Ui.TextDim, wrap: true));
        }
        var grid = new PropertyGrid();
        grid.SetTarget(set, Ctx.Db);
        grid.Changed += () =>
        {
            OnEdited(set);
            Refresh();
        };
        box.AddChild(preview);
        box.AddChild(grid);
        Refresh();
        return box;
    }
}
