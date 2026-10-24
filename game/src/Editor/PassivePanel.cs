using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// «Пассивные умения» and «Таланты» (docs/DECISIONS_3.md §46): one mechanism, two lists. Beside the fields —
/// the text the player sees (generated from the data), where the passive comes from and every object that uses
/// it (units, origins, statuses, terrain, skills, scenarios, auras), with a jump to each.
/// </summary>
public partial class PassivePanel : DefListPanel
{
    private readonly PassiveKind _kind;
    private VBoxContainer? _side;

    public PassivePanel(EditorContext ctx, PassiveKind kind) : base(ctx, DefCategory.Passive, kind == PassiveKind.Talent ? "Таланты" : "Пассивные умения",
        d => ((PassiveDef)d).Kind == kind,
        () => new PassiveDef { Name = kind == PassiveKind.Talent ? "Новый талант" : "Новое пассивное умение", Kind = kind, Source = kind == PassiveKind.Talent ? PassiveSource.Race : PassiveSource.Unit })
    {
        _kind = kind;
    }

    protected override Control BuildDetail(Def d)
    {
        var p = (PassiveDef)d;
        var root = Ui.HBox(10).Expand(true, true);
        var grid = new PropertyGrid();
        grid.SetTarget(p, Ctx.Db);
        grid.Changed += () =>
        {
            OnEdited(p);
            Side(p);
        };
        root.AddChild(grid.Expand(true));
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(400, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _side = Ui.VBox(6).Expand(true);
        scroll.AddChild(_side);
        root.AddChild(scroll);
        Side(p);
        return root;
    }

    private void Side(PassiveDef p)
    {
        if (_side == null || !IsInstanceValid(_side)) return;
        Ui.ClearChildren(_side);
        _side.AddChild(Ui.Label("Как видит игрок", 15, Ui.Accent));
        var lines = PassiveText.Lines(p, Ctx.Db);
        string trigger = p.Trigger == PassiveTrigger.Always ? "" : $"Срабатывает: {Names.Of(p.Trigger)}" +
                         (p.Chance < 1 ? $", шанс {p.Chance:P0}" : "") + (p.Cooldown > 0 ? $", перезарядка {p.Cooldown:0.#} с" : "") +
                         (p.MaxTriggers > 0 ? $", не более {p.MaxTriggers} за бой" : "") + (p.ActiveSeconds > 0 ? $"; действует {p.ActiveSeconds:0.#} с" : "");
        var text = new List<string> { $"{(string.IsNullOrEmpty(p.Icon) ? "◈" : p.Icon)} {p.DisplayName} — {Names.Of(p.Kind).ToLowerInvariant()}, источник: {Names.Of(p.Source).ToLowerInvariant()}" };
        if (!string.IsNullOrEmpty(p.Description)) text.Add(p.Description);
        if (p.ShowExactValues) text.AddRange(lines.Select(l => "• " + l));
        else text.Add("(точные значения скрыты от игрока)");
        if (p.When != null) text.Add("Условие: " + p.When.Describe());
        if (trigger != "") text.Add(trigger);
        if (p.AuraRadius > 0) text.Add($"Аура: радиус {p.AuraRadius}, на {Names.Of(p.AuraAffects).ToLowerInvariant()}{(p.AuraIncludesSelf ? " и владельца" : "")}");
        var preview = Ui.Label(string.Join("\n", text), 13, Ui.Text, wrap: true);
        preview.Name = "PassivePreview";
        _side.AddChild(preview);
        if (!p.ShowExactValues && lines.Count > 0) _side.AddChild(Ui.Label("Для автора: " + string.Join("; ", lines), 12, Ui.TextDim, wrap: true));

        _side.AddChild(Ui.Sep());
        _side.AddChild(Ui.Label("Где используется", 15, Ui.Accent));
        var users = Users(p).ToList();
        if (users.Count == 0) _side.AddChild(Ui.Label("Нигде — пассив пока никому не выдан.", 13, Ui.TextDim, wrap: true));
        foreach (var (def, how) in users)
        {
            var b = Ui.Button($"{ContentValidator.CatName(def.Category)} «{def.DisplayName}» — {how}", () => Ctx.Navigate(def.Category, def.Id), "Открыть");
            b.Alignment = HorizontalAlignment.Left;
            b.ClipText = true;
            b.Name = "UsedBy_" + def.Id;
            _side.AddChild(b);
        }
        foreach (var i in Ctx.IssuesOf(p))
            _side.AddChild(Ui.Label($"{(i.Severity == IssueSeverity.Error ? "✗" : i.Severity == IssueSeverity.Warning ? "⚠" : "ℹ")} {i.Message}", 12,
                i.Severity == IssueSeverity.Error ? Ui.Bad : i.Severity == IssueSeverity.Warning ? Ui.Warn : Ui.TextDim, wrap: true));
    }

    /// <summary>Every object that gives the passive, directly or through an origin.</summary>
    private IEnumerable<(Def Def, string How)> Users(PassiveDef p)
    {
        var db = Ctx.Db;
        var seen = new HashSet<string>();
        foreach (var d in ContentDependencies.UsedBy(db, p))
        {
            if (!seen.Add(d.Id)) continue;
            string how = d switch
            {
                UnitDef => "пассивы юнита",
                OriginDef => "пассивы происхождения",
                StatusDef => "пассивы уровня состояния",
                TerrainDef => "пока юнит стоит на гексе",
                SkillDef => "пока навык известен",
                ScenarioDef sc => sc.PassiveOverrides.Any(o => o.Passive == p.Id) ? "отключён или заменён сценарием" : "пассив сценария",
                PassiveDef => "через ауру",
                _ => "ссылка",
            };
            yield return (d, how);
        }
        // Units that inherit it through an origin (their list or a tag).
        foreach (var o in db.All<OriginDef>().Where(o => o.Passives.Contains(p.Id)))
            foreach (var u in db.All<UnitDef>().Where(u => u.Origins.Contains(o.Id) || o.AutoTags.Any(t => u.Tags.Contains(t))))
                if (seen.Add(u.Id))
                    yield return (u, $"наследует от «{o.DisplayName}»");
    }
}

/// <summary>
/// «Категории существ» (docs/DECISIONS_4.md §61): the origins of part 3 with a parent and rules — the tree, the
/// rules with their modes, the children and the templates under the category.
/// </summary>
public partial class OriginPanel : DefListPanel
{
    private VBoxContainer? _side;

    public OriginPanel(EditorContext ctx) : base(ctx, DefCategory.Origin, "Категории существ", null, () => new OriginDef { Name = "Новая категория" })
    {
    }

    protected override Control BuildDetail(Def d)
    {
        var o = (OriginDef)d;
        var root = Ui.HBox(10).Expand(true, true);
        var grid = new PropertyGrid();
        grid.SetTarget(o, Ctx.Db);
        grid.Changed += () =>
        {
            OnEdited(o);
            Side(o);
        };
        root.AddChild(grid.Expand(true));
        _side = Ui.VBox(6).MinSize(380);
        root.AddChild(_side);
        Side(o);
        return root;
    }

    private void Side(OriginDef o)
    {
        if (_side == null || !IsInstanceValid(_side)) return;
        Ui.ClearChildren(_side);
        var problems = new List<string>();
        var chain = CategoryTree.Chain(Ctx.Db, o, problems);
        var path = Ui.Label("Дерево: " + string.Join(" → ", chain.Select(c => c.DisplayName)), 14, Ui.Text, wrap: true);
        path.Name = "CategoryPath";
        _side.AddChild(path);
        foreach (var p in problems) _side.AddChild(Ui.Label("⛔ " + p, 12, Ui.Bad, wrap: true));
        var children = Ctx.Db.All<OriginDef>().Where(c => c.Parent == o.Id).ToList();
        if (children.Count > 0)
        {
            _side.AddChild(Ui.Label("Потомки:", 13, Ui.TextDim));
            foreach (var c in children) _side.AddChild(Ui.Button("↳ " + c.DisplayName, () => Ctx.Navigate(DefCategory.Origin, c.Id)));
        }
        if (o.Rules.Count > 0)
        {
            _side.AddChild(Ui.Label("Правила:", 13, Ui.TextDim));
            foreach (var r in o.Rules) _side.AddChild(Ui.Label($"• {r.Describe(Ctx.Db)} — {r.Mode}{(r.Priority != 0 ? $", приоритет {r.Priority}" : "")}", 12, Ui.Text, wrap: true));
        }
        _side.AddChild(Ui.Sep());
        _side.AddChild(Ui.Label("Что получают юниты", 15, Ui.Accent));
        foreach (var id in o.Passives)
        {
            var p = Ctx.Db.Get<PassiveDef>(id);
            if (p == null) continue;
            var b = Ui.Button($"{(p.Kind == PassiveKind.Talent ? "★" : "◈")} {p.DisplayName}: {string.Join("; ", PassiveText.Lines(p, Ctx.Db))}", () => Ctx.Navigate(DefCategory.Passive, p.Id));
            b.Alignment = HorizontalAlignment.Left;
            b.ClipText = true;
            _side.AddChild(b);
        }
        _side.AddChild(Ui.Sep());
        _side.AddChild(Ui.Label("Шаблоны в этой категории (и в потомках)", 15, Ui.Accent));
        var units = Ctx.Db.All<UnitDef>().Where(u => CategoryTree.Resolve(Ctx.Db, u).Ids.Contains(o.Id)).ToList();
        if (units.Count == 0) _side.AddChild(Ui.Label("Нет (укажите категорию у юнита или тег автоназначения).", 13, Ui.TextDim, wrap: true));
        foreach (var u in units)
        {
            string how = u.MainCategory == o.Id ? " (основная)" : u.Origins.Contains(o.Id) ? "" : o.AutoTags.Any(t => u.Tags.Contains(t)) ? " (по тегу)" : " (через потомка)";
            _side.AddChild(Ui.Button($"{u.DisplayName}{how}", () => Ctx.Navigate(DefCategory.Unit, u.Id)));
        }
        var scen = Ctx.Db.All<ScenarioDef>().Where(s => s.PassiveOverrides.Any(x => o.Passives.Contains(x.Passive))).ToList();
        if (scen.Count > 0)
        {
            _side.AddChild(Ui.Label("Сценарии, меняющие его пассивы", 13, Ui.TextDim));
            foreach (var s in scen) _side.AddChild(Ui.Button(s.DisplayName, () => Ctx.Navigate(DefCategory.Scenario, s.Id)));
        }
    }
}
