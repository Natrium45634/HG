using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// Mass changes of templates (docs/DECISIONS_4.md §69): a characteristic, a category, a rank, an AI profile, a fit to
/// a level for the selected templates. A restore point is made first; the change is one operation of the history.
/// </summary>
public partial class DefListPanel
{
    public const string MassTitle = "Массовое изменение шаблонов";

    private void UnitMass(int op, List<UnitDef> sel)
    {
        if (sel.Count == 0) return;
        var db = Ctx.Db;
        var v = Ui.VBox(6);
        string names = string.Join(", ", sel.Take(8).Select(d => d.DisplayName)) + (sel.Count > 8 ? $" … (+{sel.Count - 8})" : "");
        v.AddChild(Ui.Label($"Шаблонов: {sel.Count} — {names}. Перед изменением создаётся точка восстановления; отмена — одним Ctrl+Z.", 13, Ui.TextDim, wrap: true).MinSize(520));
        OptionButton Options(string name, IEnumerable<string> items)
        {
            var o = new OptionButton { Name = name };
            foreach (var i in items) o.AddItem(i);
            return o;
        }
        Func<(string Label, int Changed)> apply;
        switch (op)
        {
            case 20:
            {
                var stats = db.Ids(DefCategory.Stat).ToList();
                var stat = Options("MassStat", stats.Select(id => $"{db.Stat(id)?.DisplayName ?? id} [{id}]"));
                stat.Select(Math.Max(0, stats.IndexOf(Stats.HpMax)));
                var mode = Options("MassMode", Enum.GetValues<MassStatMode>().Select(m => EdUtil.EnumLabel(m)));
                mode.Select(1);
                var value = new SpinBox { Name = "MassValue", MinValue = -100000, MaxValue = 100000, Step = 0.01, Value = 1.1, CustomMinimumSize = new Vector2(120, 0) };
                v.AddChild(Ui.HBox(6, Ui.Label("Характеристика", 13), stat.Expand()));
                v.AddChild(Ui.HBox(6, Ui.Label("Действие", 13), mode.Expand(), value));
                apply = () =>
                {
                    string id = stats[stat.Selected];
                    var m = (MassStatMode)mode.Selected;
                    return ($"{EdUtil.EnumLabel(m)} «{db.Stat(id)?.DisplayName ?? id}» {value.Value:0.##}", MassEdit.SetStat(db, sel, id, m, value.Value));
                };
                break;
            }
            case 21:
            {
                var cats = db.All<OriginDef>().OrderBy(o => o.DisplayName, StringComparer.CurrentCulture).ToList();
                var cat = Options("MassCategory", cats.Select(c => $"{string.Join(" → ", CategoryTree.Chain(db, c).Select(x => x.DisplayName))} [{c.Id}]"));
                var main = new CheckBox { Name = "MassCategoryMain", Text = "Основная категория (иначе — дополнительная)", ButtonPressed = true };
                v.AddChild(cat);
                v.AddChild(main);
                apply = () =>
                {
                    if (cats.Count == 0) return ("категорий нет", 0);
                    var c = cats[cat.Selected];
                    return ($"Категория «{c.DisplayName}» ({(main.ButtonPressed ? "основная" : "доп.")})", MassEdit.SetCategory(sel, c.Id, main.ButtonPressed));
                };
                break;
            }
            case 22:
            {
                var rank = Options("MassRank", Enum.GetValues<UnitRank>().Select(r => EdUtil.EnumLabel(r)));
                v.AddChild(rank);
                apply = () => ($"Ранг «{EdUtil.EnumLabel((UnitRank)rank.Selected)}»", MassEdit.SetRank(sel, (UnitRank)rank.Selected));
                break;
            }
            case 23:
            {
                var ids = db.Ids(DefCategory.AiProfile).ToList();
                var ai = Options("MassAi", ids.Select(id => $"{db.Get<AiProfileDef>(id)?.DisplayName ?? id} [{id}]"));
                v.AddChild(ai);
                apply = () => ($"Профиль ИИ «{ids[ai.Selected]}»", MassEdit.SetAiProfile(sel, ids[ai.Selected]));
                break;
            }
            default:
            {
                var level = new SpinBox { Name = "MassLevel", MinValue = 1, MaxValue = 500, Step = 0.5, Value = 10, CustomMinimumSize = new Vector2(120, 0) };
                var variant = Options("MassVariant", Enum.GetValues<FitVariant>().Select(x => EdUtil.EnumLabel(x)));
                variant.Select((int)FitVariant.Combined);
                v.AddChild(Ui.HBox(6, Ui.Label("Целевой уровень", 13), level, Ui.Label("Вариант", 13), variant.Expand()));
                v.AddChild(Ui.Label("Каждый шаблон — по своему профилю масштабирования (роль сохраняется); эталон уровня 1 не подгоняется.", 12, Ui.TextDim, wrap: true));
                apply = () =>
                {
                    var res = MassEdit.FitAll(db, sel, level.Value, (FitVariant)variant.Selected);
                    _lastFit = res;
                    return ($"Подогнать под уровень {level.Value:0.#}", res.Count(r => r.Changes.Count > 0));
                };
                break;
            }
        }
        Dialogs.Custom(this, MassTitle, v, "Применить", () =>
        {
            RestorePoints.Create(Ctx, $"перед массовым изменением ({sel.Count} шаблонов)");
            Ctx.Commit();
            var (label, changed) = apply();
            Ctx.MarkDirty();
            Ctx.Commit($"Массово: {label}");
            var keep = Current?.Id;
            RefreshList();
            if (keep != null && Ctx.Db.Unit(keep) is { } cur) Select(cur);
            string details = op == 24 && _lastFit != null
                ? "\n" + string.Join("\n", _lastFit.Select(r => $"«{r.Unit.DisplayName}»: {r.LevelBefore:0.0} → {r.LevelAfter:0.0}" + (r.Reached ? "" : $" — {r.Message}")))
                : "";
            Dialogs.Info(this, MassTitle, $"{label}: изменено шаблонов {changed} из {sel.Count}.{details}\nТочка восстановления создана; отменить — Ctrl+Z.");
            return true;
        }, new Vector2I(600, 0));
    }

    private List<FitResult>? _lastFit;
}
