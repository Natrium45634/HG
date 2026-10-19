using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// Reference of the catalog (docs/DECISIONS_2.md §33): every ready operation and condition the
/// editors offer under «+ из каталога», with where it is used and which item of the spec it covers.
/// </summary>
public partial class CatalogPanel : VBoxContainer
{
    private LineEdit _search = null!;
    private VBoxContainer _list = null!;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 6);
        AddChild(Ui.Label("Каталог операций и условий", 18, Ui.Accent));
        AddChild(Ui.Label("Из этих готовых блоков собираются навыки, пассивные способности, эффекты клеток, состояния, поведение врагов (правила профилей ИИ) " +
                          "и сценарные события. В любом списке операций, действий или условий нажмите «+ из каталога ▾». Условия объединяются через И / ИЛИ / НЕ; " +
                          "формулы считает безопасный парсер, произвольный код не выполняется.", 13, Ui.TextDim, wrap: true));
        _search = new LineEdit { PlaceholderText = "Поиск по названию, группе или описанию" };
        _search.TextChanged += _ => Fill();
        AddChild(_search);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _list = Ui.VBox(4).Expand();
        var pad = new MarginContainer();
        pad.AddThemeConstantOverride("margin_right", 14);
        pad.AddChild(_list);
        scroll.AddChild(pad.Expand());
        AddChild(scroll);
        Fill();
    }

    private void Fill()
    {
        Ui.ClearChildren(_list);
        string q = _search.Text.Trim().ToLowerInvariant();
        var sections = new (string Title, string Where, System.Collections.Generic.IReadOnlyList<CatalogEntry> Entries)[]
        {
            ("Операции навыков и способностей", "навыки, пассивки, эффекты клеток, тики состояний, синергии, а также действие «Операции над юнитами»", OperationCatalog.Abilities),
            ("Действия сценария", "триггеры, волны (начало/конец), фазы боссов, правила поведения ИИ", OperationCatalog.Actions),
            ("Условия", "цели, волны, триггеры, фазы боссов, правила ИИ, условия пассивок, состояния «пока выполняется условие»", OperationCatalog.Conditions),
        };
        foreach (var (title, where, entries) in sections)
        {
            var shown = entries.Where(e => q.Length == 0 || e.Name.ToLowerInvariant().Contains(q) || e.Group.ToLowerInvariant().Contains(q) || e.Description.ToLowerInvariant().Contains(q)).ToList();
            if (shown.Count == 0) continue;
            _list.AddChild(Ui.Label($"{title} ({shown.Count})", 16, Ui.Text));
            _list.AddChild(Ui.Label("Где: " + where, 12, Ui.TextDim, wrap: true));
            foreach (var g in shown.GroupBy(e => e.Group))
            {
                _list.AddChild(Ui.Label("  " + g.Key, 14, Ui.Accent));
                foreach (var e in g)
                {
                    string covers = e.Covers.Length > 0 ? $"   [ТЗ: {string.Join(", ", e.Covers)}]" : "";
                    _list.AddChild(Ui.Label($"    • {e.Name}{(e.Description.Length > 0 ? " — " + e.Description : "")}{covers}", 13, Ui.Text, wrap: true));
                }
            }
            _list.AddChild(Ui.Sep());
        }
        _list.AddChild(Ui.Label("Длительность временных модификаторов (состояний): в течение времени, до конца боя, пока выполняется условие, пока юнит стоит на клетке, " +
                                "пока активен другой эффект; «Изменить характеристику до конца боя» — постоянное изменение, не снимается очищением.", 12, Ui.TextDim, wrap: true));
        _list.AddChild(Ui.Label("Перед публикацией проверяются: обязательные поля, диапазоны, ссылки, циклические триггеры, несовместимые эффекты, недостижимые цели, " +
                                "отсутствующие ресурсы и потенциально бесконечные реакции. Ошибки блокируют публикацию, предупреждения принимаются подтверждением.", 12, Ui.TextDim, wrap: true));
    }
}
