using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.App;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// «Оценка силы» of a template (docs/DECISIONS_4.md §65–67, §70): the level with its exact value, the table of
/// contributions and where each comes from, the profile of effective health, the passives with their shares, the
/// context (alone, typical group, group of a scenario, against a category), the threat (base, on the map, against
/// the defenders, on the route), the «чрезмерно сильный» warnings, where the template is used, and «Подогнать под
/// уровень».
/// </summary>
public partial class UnitPowerView : VBoxContainer
{
    private readonly EditorContext _ctx;
    private readonly UnitDef _unit;
    private readonly Action<UnitDef> _applied;
    private readonly List<(string Label, Func<PowerContext> Make, ScenarioDef? Scenario)> _contexts = new();
    private OptionButton _context = null!;
    private VBoxContainer _body = null!;

    public UnitPowerView(EditorContext ctx, UnitDef unit, Action<UnitDef> applied)
    {
        _ctx = ctx;
        _unit = unit;
        _applied = applied;
        Name = "UnitPower";
        AddThemeConstantOverride("separation", 3);
        AddChild(Ui.Label("Оценка силы", 15, Ui.Accent));
        var head = Ui.HBox(6);
        head.AddChild(Ui.Label("Контекст", 12, Ui.TextDim));
        _context = new OptionButton { Name = "PowerContext", ClipText = true, FitToLongestItem = false }.Expand();
        head.AddChild(_context);
        AddChild(head);
        _body = Ui.VBox(2);
        AddChild(_body);
        var fit = Ui.Button("Подогнать под уровень…", () => UnitFitDialog.Show(this, _ctx, _unit, _applied),
            "Подбор характеристик под целевой уровень по профилю масштабирования: три варианта, исходный шаблон не меняется");
        fit.Name = "PowerFit";
        AddChild(fit);
        var cmp = Ui.Button("Сравнить с опубликованной…", () => VersionCompareDialog.Show(this, _ctx, _unit),
            "Характеристики, уровень, угроза, навыки, пассивы, сопротивления, роль и пробные бои опубликованной версии и черновика");
        cmp.Name = "PowerCompare";
        AddChild(cmp);
        FillContexts();
        _context.ItemSelected += _ => Refresh();
        Refresh();
    }

    /// <summary>Alone, the typical group, the group of every scenario with the template in its waves, against every category.</summary>
    private void FillContexts()
    {
        var db = _ctx.Db;
        _contexts.Add(("Один", () => PowerContext.Solo, null));
        _contexts.Add(($"Типичная группа ({db.Rules.Power.TypicalAllies} союзн.)", () => PowerContext.Typical(_ctx.Db), null));
        foreach (var sc in ScenariosOf(db, _unit))
        {
            var s = sc;
            _contexts.Add(($"Группа сценария «{sc.DisplayName}»", () => PowerContext.InScenario(_ctx.Db, _unit, s), s));
        }
        foreach (var o in db.All<OriginDef>().OrderBy(o => o.DisplayName, StringComparer.CurrentCulture))
        {
            var id = o.Id;
            _contexts.Add(($"Против «{o.DisplayName}»", () => new PowerContext { Kind = PowerContextKind.VsCategory, VsCategory = id }, null));
        }
        foreach (var c in _contexts) _context.AddItem(c.Label);
        _context.Select(0);
    }

    /// <summary>The labels of the contexts (UI tests, screenshots).</summary>
    public IReadOnlyList<string> ContextLabels => _contexts.Select(c => c.Label).ToList();

    public void SetContext(int index)
    {
        _context.Select(Math.Clamp(index, 0, _contexts.Count - 1));
        Refresh();
    }

    private static IEnumerable<ScenarioDef> ScenariosOf(ContentDatabase db, UnitDef u) =>
        db.All<ScenarioDef>().Where(sc => sc.Waves.Any(w => w.Groups.Any(g => ContentRef.IdOf(g.Unit) == u.Id)) || sc.Units.Any(su => ContentRef.IdOf(su.Unit) == u.Id));

    public void Refresh()
    {
        if (!IsInstanceValid(_body)) return;
        Ui.ClearChildren(_body);
        var db = _ctx.Db;
        var (_, make, ctxScenario) = _contexts[Math.Clamp(_context.Selected, 0, _contexts.Count - 1)];
        PowerReport r, solo;
        try
        {
            r = PowerModel.Evaluate(db, _unit, make());
            solo = _context.Selected <= 0 ? r : PowerModel.Evaluate(db, _unit);
        }
        catch (Exception e)
        {
            _body.AddChild(Ui.Label("Оценка не рассчитана: " + e.Message, 12, Ui.Bad, wrap: true));
            return;
        }
        var level = Ui.Label($"Уровень {r.LevelText}", 20, Ui.Text);
        level.Name = "PowerLevel";
        level.TooltipText = "Отображается целое; в скобках — точное значение (небольшие изменения пассивов видны в нём)";
        _body.AddChild(level);
        _body.AddChild(Ui.Label($"сила {r.Power:0.000} к эталону · роль {EdUtil.EnumLabel(r.Role)}{(r.RoleAuto ? " (авто)" : "")} · ранг {EdUtil.EnumLabel(r.Rank)}", 12, Ui.TextDim, wrap: true));
        if (r.Rank != UnitRank.Normal) _body.AddChild(Ui.Label("Ранг не меняет уровень: он влияет на подбор волн, награды и вид.", 11, Ui.TextDim, wrap: true));

        foreach (var line in r.Lines)
        {
            bool total = line.Key == "total";
            var row = Ui.HBox(4);
            row.Name = "PowerLine_" + line.Key;
            row.TooltipText = line.Detail;
            row.AddChild(Ui.Label(line.Label, 13, total ? Ui.Accent : Ui.Text).Expand());
            var v = Ui.Label(total ? $"{line.Value:0.00}" : $"+{line.Value:0.00}", 13, total ? Ui.Accent : Ui.Text);
            v.HorizontalAlignment = HorizontalAlignment.Right;
            row.AddChild(v);
            if (total) _body.AddChild(Ui.Sep());
            _body.AddChild(row);
            if (line.Detail != "") _body.AddChild(Ui.Label("   " + line.Detail, 11, Ui.TextDim, wrap: true));
        }

        if (r.EhpProfile.Count > 0)
        {
            var differs = r.EhpProfile.Where(e => e.Type == "physical" || Math.Abs(e.Ratio - 1) > 0.01).ToList();
            _body.AddChild(Ui.Label("Эфф. здоровье по типам урона: " + (differs.Count <= 1
                ? $"одинаково ({r.EhpProfile[0].Ehp:0})"
                : string.Join(", ", differs.Select(e => $"{e.Name} {e.Ehp:0} (×{e.Ratio:0.##})"))), 12, Ui.TextDim, wrap: true));
        }
        foreach (var p in r.Passives)
            _body.AddChild(Ui.Label($"Пассив «{p.Name}» ({p.Source}): доля боя {p.Share:P0} — {p.Why}", 12, Ui.TextDim, wrap: true));
        foreach (var n in r.Notes) _body.AddChild(Ui.Label("ⓘ " + n, 12, Ui.TextDim, wrap: true));

        // Threat: base and, with a scene, on its map, against its defenders, on the route of the group.
        var sc = ctxScenario ?? ScenariosOf(db, _unit).FirstOrDefault();
        var threat = ThreatModel.Evaluate(db, _unit, sc);
        string text = threat.ToText();
        var route = sc?.Waves.SelectMany(w => w.Groups).Where(g => ContentRef.IdOf(g.Unit) == _unit.Id && g.Route != "").OrderByDescending(g => g.Count).FirstOrDefault()?.Route;
        if (sc != null && !threat.AuthorValue && route != null && ThreatModel.Evaluate(db, _unit, sc, route).OnRoute is { } onRoute)
            text = text.Replace("\n", $", на маршруте «{route}» {onRoute:0.#}\n", StringComparison.Ordinal);
        _body.AddChild(Ui.Sep());
        _body.AddChild(Ui.Label("Угроза" + (sc != null ? $" (сцена «{sc.DisplayName}»)" : " (без сцены)"), 13, Ui.Accent));
        var tl = Ui.Label(text, 12, Ui.Text, wrap: true);
        tl.Name = "ThreatText";
        _body.AddChild(tl);
        if (sc != null && !ThreatModel.HasContext(sc))
            _body.AddChild(Ui.Label("В сцене мало данных (нужны маршрут и защитники): подбор волн берёт базовую стоимость.", 11, Ui.TextDim, wrap: true));

        // «Чрезмерно сильный?» — warnings with the rule; nothing is forbidden.
        int w = 0;
        foreach (var (rule, msg) in BalanceChecks.Check(db, _unit, solo))
        {
            var l = Ui.Label($"⚠ [{rule}] {msg}", 12, Ui.Warn, wrap: true);
            l.Name = "PowerWarn_" + w++;
            _body.AddChild(l);
        }

        // Where the template is used: its changes reach every live reference (pinned «id@N» stay).
        var refs = db.FindReferences(DefCategory.Unit, _unit.Id).Where(x => x.Owner != null && x.Owner != _unit).ToList();
        int groups = refs.Count(x => x.Path.Contains("Waves", StringComparison.Ordinal));
        var owners = refs.Select(x => x.Owner!).Distinct().ToList();
        _body.AddChild(Ui.Sep());
        _body.AddChild(Ui.Label(refs.Count == 0
            ? "Не используется в сценариях."
            : $"Используется: {refs.Count} ссылок ({groups} групп волн) в {owners.Count} объектах — {string.Join(", ", owners.Take(4).Select(o => o.DisplayName))}{(owners.Count > 4 ? "…" : "")}. " +
              "Изменение уровня придёт во все живые ссылки; закреплённые на версии («id@N») не меняются.", 12, Ui.TextDim, wrap: true));
        var pub = Main.Instance.Content.Published;
        if (pub.Unit(_unit.Id) is { } pu && ContentJson.Serialize(pu) != ContentJson.Serialize(_unit))
        {
            try
            {
                var pr = PowerModel.Evaluate(pub, pu, PowerContext.Solo);
                var ch = Ui.Label($"Опубликованная версия v{pu.Version}: уровень {pr.LevelText} → в черновике {solo.LevelText}", 12, Math.Abs(pr.Level - solo.Level) > 0.005 ? Ui.Warn : Ui.TextDim, wrap: true);
                ch.Name = "PowerPublished";
                _body.AddChild(ch);
            }
            catch (Exception)
            {
                // The published build may reference removed content: nothing to compare.
            }
        }
    }
}

/// <summary>
/// «Подогнать под уровень» (docs/DECISIONS_4.md §66): target level and scaling profile → three variants (base stats,
/// skills, combined), each with its table of changes and the reached level; the author picks one and applies it
/// as a copy of the template or as its new version. Nothing is applied without the confirmation.
/// </summary>
public static class UnitFitDialog
{
    public const string Title = "Подогнать под уровень";

    public static void Show(Node parent, EditorContext ctx, UnitDef unit, Action<UnitDef> applied, double? targetLevel = null)
    {
        var db = ctx.Db;
        var before = PowerModel.Evaluate(db, unit);
        var auto = LevelFit.ProfileFor(db, unit, before.Role);
        var profiles = db.All<ScalingProfileDef>().ToList();

        var root = Ui.VBox(6);
        root.AddChild(Ui.Label($"«{unit.DisplayName}»: уровень {before.LevelText}, роль {EdUtil.EnumLabel(before.Role)}. " +
                               "Исходный шаблон не меняется: результат применяется как копия или как новая версия (прежняя остаётся в истории версий).", 13, Ui.TextDim, wrap: true).MinSize(720));
        if (unit.Id == db.Rules.Power.ReferenceUnit)
            root.AddChild(Ui.Label("⚠ Это эталон уровня 1 (правила → «Оценка силы»): его уровень всегда 1. Подгонка как новой версии изменит уровни всех шаблонов — подгоняйте копию.", 13, Ui.Warn, wrap: true).MinSize(720));
        var row = Ui.HBox(8);
        row.AddChild(Ui.Label("Целевой уровень", 13));
        var target = new SpinBox { Name = "FitTarget", MinValue = 1, MaxValue = 500, Step = 0.5, Value = targetLevel ?? Math.Max(1, Math.Round(before.Level) + 5), CustomMinimumSize = new Vector2(110, 0) };
        row.AddChild(target);
        row.AddChild(Ui.Label("Профиль", 13));
        var profile = new OptionButton { Name = "FitProfile" };
        profile.AddItem($"по шаблону: «{auto.DisplayName}»");
        foreach (var p in profiles) profile.AddItem($"{p.DisplayName} ({EdUtil.EnumLabel(p.Role)})");
        row.AddChild(profile.Expand());
        var results = Ui.VBox(6);
        results.Name = "FitResults";
        var group = new ButtonGroup();
        var current = new List<FitResult>();
        void Run()
        {
            Ui.ClearChildren(results);
            current.Clear();
            var prof = profile.Selected <= 0 ? auto : profiles[profile.Selected - 1];
            current.AddRange(LevelFit.Variants(db, unit, target.Value, prof));
            for (int i = 0; i < current.Count; i++)
            {
                var fr = current[i];
                var box = Ui.VBox(2);
                var pick = new CheckBox
                {
                    Name = "FitVariant_" + i, ButtonGroup = group, ButtonPressed = i == current.Count - 1,
                    Text = $"{EdUtil.EnumLabel(fr.Variant)}: уровень {fr.LevelBefore:0.0} → {fr.LevelAfter:0.0} (цель {fr.TargetLevel:0.#})" + (fr.Reached ? "" : " — не достигнут"),
                };
                pick.AddThemeColorOverride("font_color", fr.Reached ? Ui.Text : Ui.Warn);
                box.AddChild(pick);
                if (fr.Message != "") box.AddChild(Ui.Label(fr.Message, 12, fr.Reached ? Ui.TextDim : Ui.Warn, wrap: true).MinSize(700));
                var table = new GridContainer { Columns = 3 };
                table.AddThemeConstantOverride("h_separation", 18);
                foreach (var c in fr.Changes)
                {
                    table.AddChild(Ui.Label(c.Label, 12).MinSize(300));
                    table.AddChild(Ui.Label(c.Key == "role" ? "" : $"{c.From:0.##} → {c.To:0.##}", 12, Math.Abs(c.To - c.From) > 1e-9 ? Ui.Text : Ui.TextDim));
                    table.AddChild(Ui.Label(c.Note, 12, Ui.TextDim));
                }
                if (fr.Changes.Count == 0) table.AddChild(Ui.Label("изменений нет", 12, Ui.TextDim));
                box.AddChild(table);
                results.AddChild(Ui.PanelBox(box, Ui.PanelLight, 8));
            }
        }
        var run = Ui.Button("Рассчитать", Run, "Подобрать заново: три варианта");
        run.Name = "FitRun";
        row.AddChild(run);
        root.AddChild(row);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(740, 380), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(results.Expand());
        root.AddChild(scroll);
        var mode = new OptionButton { Name = "FitApplyMode" };
        mode.AddItem("как копию шаблона (новый ID, исходный не меняется)");
        mode.AddItem("как новую версию этого шаблона (прежняя — в истории версий)");
        var applyRow = Ui.HBox(8, Ui.Label("Применить", 13), mode.Expand());
        root.AddChild(applyRow);
        var status = Ui.Label("", 12, Ui.Warn, wrap: true);
        root.AddChild(status);
        Run();

        Dialogs.Custom(parent, Title, root, "Применить", () =>
        {
            int idx = current.FindIndex(fr => results.FindChild("FitVariant_" + current.IndexOf(fr), true, false) is CheckBox { ButtonPressed: true });
            if (idx < 0)
            {
                status.Text = "Выберите вариант.";
                return false;
            }
            var chosen = ContentJson.Clone(current[idx].Unit);
            ctx.Commit();
            if (mode.Selected == 0)
            {
                chosen.Id = ContentIO.UniqueId(db, DefCategory.Unit, $"{unit.Id}_lv{(int)Math.Round(current[idx].TargetLevel)}");
                chosen.Name = $"{unit.DisplayName} (ур. {current[idx].TargetLevel:0.#})";
                chosen.Version = 1;
                db.Add(chosen);
            }
            else db.Table(DefCategory.Unit).Replace(chosen);
            ctx.MarkDirty();
            ctx.Commit($"Подогнать под уровень {current[idx].TargetLevel:0.#}: «{unit.DisplayName}»" + (mode.Selected == 0 ? $" → копия [{chosen.Id}]" : " (новая версия)"));
            applied(chosen);
            return true;
        }, new Vector2I(780, 640));
    }
}
