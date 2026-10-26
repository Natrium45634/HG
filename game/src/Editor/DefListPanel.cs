using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.App;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// Generic editor of one content category — a view of the content library (docs/DECISIONS_2.md §31):
/// search, filters (errors, warnings, favourites, incomplete, group), sorting, favourites,
/// multi-selection with mass operations (delete, tag, group, favourite, export), dependencies,
/// version history, create / duplicate / rename / delete (with dependency check) / import / export,
/// and a form for the selected definition. Specialised editors derive from it and override
/// <see cref="BuildDetail"/>.
/// </summary>
public partial class DefListPanel : HSplitContainer
{
    protected readonly EditorContext Ctx;
    protected readonly DefCategory Category;
    private readonly string _title;
    private readonly Func<Def, bool>? _filter;
    private readonly Func<Def>? _factory;

    private LineEdit _search = null!;
    private OptionButton _filterMode = null!;
    private OptionButton _groupFilter = null!;
    private OptionButton _sort = null!;
    private Label _countLabel = null!;
    private readonly List<string> _groups = new();
    private ItemList _list = null!;

    private static readonly string[] FilterModes = { "Все", "С ошибками", "С предупреждениями", "Избранное", "Неполные" };
    private static readonly string[] SortModes = { "Как в пакете", "По названию", "По ID", "По группе", "По версии", "Сначала ошибки" };
    private readonly List<Def> _shown = new();
    protected VBoxContainer DetailRoot { get; private set; } = null!;
    private VBoxContainer _issuesBox = null!;
    private Label _header = null!;

    public Def? Current { get; private set; }

    /// <summary>Detail area inside a scroll container (forms) or not (3D editors).</summary>
    protected virtual bool UseScroll => true;
    public event Action<Def?>? SelectionChanged;

    public DefListPanel(EditorContext ctx, DefCategory category, string title, Func<Def, bool>? filter = null, Func<Def>? factory = null)
    {
        Ctx = ctx;
        Category = category;
        _title = title;
        _filter = filter;
        _factory = factory;
        SplitOffset = 300;
    }

    public override void _Ready()
    {
        var left = Ui.VBox(6).MinSize(260);
        left.AddChild(Ui.Label(_title, 18, Ui.Accent));
        _search = new LineEdit { PlaceholderText = "Поиск по названию, ID, тегу, группе" };
        _search.TextChanged += _ => RefreshList();
        left.AddChild(_search);
        var filters = Ui.HBox(4);
        _filterMode = Option(FilterModes, "Фильтр");
        _groupFilter = new OptionButton { FocusMode = FocusModeEnum.None, ClipText = true, CustomMinimumSize = new Vector2(80, 0), TooltipText = "Категория в библиотеке (группа)" };
        _groupFilter.ItemSelected += _ => RefreshList();
        filters.AddChild(_filterMode.Expand());
        filters.AddChild(_groupFilter.Expand());
        left.AddChild(filters);
        var sortRow = Ui.HBox(4);
        _sort = Option(SortModes, "Сортировка");
        sortRow.AddChild(_sort.Expand());
        _countLabel = Ui.Label("", 12, Ui.TextDim);
        sortRow.AddChild(_countLabel);
        left.AddChild(sortRow);
        _list = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, AllowReselect = true, SelectMode = ItemList.SelectModeEnum.Multi };
        _list.TooltipText = "Ctrl/Shift — выбрать несколько для массовых операций";
        _list.MultiSelected += (i, selected) =>
        {
            if (selected && i >= 0 && i < _shown.Count) SelectFromList(_shown[(int)i]);
            UpdateCount();
        };
        left.AddChild(_list);
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 4);
        grid.AddThemeConstantOverride("v_separation", 4);
        grid.AddChild(Ui.Button("Создать", Create, "Новый объект с значениями по умолчанию").Expand());
        grid.AddChild(Ui.Button("Дублировать", Duplicate, "Копия выбранного (создание из шаблона) с новым ID").Expand());
        grid.AddChild(Ui.Button("Переименовать", Rename, "Сменить название (ID неизменен)").Expand());
        grid.AddChild(Ui.Button("Удалить", Delete, "С проверкой зависимостей").Expand());
        grid.AddChild(Ui.Button("Импорт…", Import, "Объект (.json) или пакет (.hexpack): проверка, зависимости, предпросмотр, конфликты ID").Expand());
        grid.AddChild(Ui.Button("Экспорт…", Export, "Сохранить выбранный объект в JSON-файл").Expand());
        grid.AddChild(Ui.Button("Пакет…", ExportPackage, "Экспорт выбранных объектов в пакет .hexpack со всеми зависимостями").Expand());
        grid.AddChild(Ui.Button("★ Избранное", ToggleFavorite, "Отметить / снять отметку (личная настройка, не часть контента)").Expand());
        grid.AddChild(Ui.Button("Связи…", ShowDependencies, "Что объект использует и где используется сам").Expand());
        grid.AddChild(Ui.Button("Версии…", ShowVersions, "История версий: различия и возврат прежней версии в черновик").Expand());
        var mass = new MenuButton { Text = "Массово ▾", Flat = false, TooltipText = "Операции над выбранными (Ctrl/Shift + клик)" };
        var pm = mass.GetPopup();
        pm.AddItem("Удалить выбранные", 0);
        pm.AddItem("Добавить тег…", 1);
        pm.AddItem("Убрать тег…", 2);
        pm.AddItem("Задать категорию (группу)…", 3);
        pm.AddItem("В избранное", 4);
        pm.AddItem("Убрать из избранного", 5);
        pm.AddItem("Снять отметку «неполный»", 6);
        pm.AddItem("Экспорт выбранных в папку…", 7);
        pm.AddItem("Экспорт выбранных в пакет…", 9);
        pm.AddItem("Выбрать все показанные", 8);
        if (Category == DefCategory.Unit)
        {
            // Part 4 stage 7 (docs/DECISIONS_4.md §69): mass changes of templates — a restore point first, one undo.
            pm.AddSeparator("Шаблоны");
            pm.AddItem("Характеристика: задать / умножить / прибавить…", 20);
            pm.AddItem("Назначить категорию существа…", 21);
            pm.AddItem("Ранг…", 22);
            pm.AddItem("Профиль ИИ…", 23);
            pm.AddItem("Подогнать под уровень…", 24);
        }
        pm.IdPressed += id => MassOperation((int)id);
        left.AddChild(grid);
        left.AddChild(mass.Expand());
        AddChild(left);

        var right = Ui.VBox(8).Expand(true, true);
        _header = Ui.Label("", 16, Ui.Text);
        right.AddChild(_header);
        _issuesBox = Ui.VBox(2);
        right.AddChild(_issuesBox);
        DetailRoot = Ui.VBox(6).Expand(true, true);
        right.AddChild(DetailRoot);
        if (UseScroll)
        {
            var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var pad = new MarginContainer();
            pad.AddThemeConstantOverride("margin_right", 14);
            pad.AddChild(right);
            scroll.AddChild(pad.Expand());
            AddChild(scroll);
        }
        else
        {
            AddChild(right);
        }

        Ctx.Validated += OnValidated;
        Ctx.ContentReplaced += OnContentReplaced;
        RefreshList();
        SelectFirst();
    }

    public override void _ExitTree()
    {
        Ctx.Validated -= OnValidated;
        Ctx.ContentReplaced -= OnContentReplaced;
    }

    private void OnContentReplaced()
    {
        Current = null;
        RefreshList();
        SelectFirst();
    }

    protected IEnumerable<Def> Items => Ctx.Db.Table(Category).Items.Where(d => _filter == null || _filter(d));

    private OptionButton Option(string[] items, string tooltip)
    {
        var ob = new OptionButton { FocusMode = FocusModeEnum.None, ClipText = true, CustomMinimumSize = new Vector2(80, 0), TooltipText = tooltip };
        foreach (var i in items) ob.AddItem(i);
        ob.Selected = 0;
        ob.ItemSelected += _ => RefreshList();
        return ob;
    }

    /// <summary>Selected definitions (multi-selection), the current one when nothing is selected.</summary>
    public List<Def> SelectedDefs()
    {
        var list = _list == null ? new List<Def>() : _list.GetSelectedItems().Where(i => i >= 0 && i < _shown.Count).Select(i => _shown[i]).ToList();
        if (list.Count == 0 && Current != null) list.Add(Current);
        return list;
    }

    private void UpdateCount()
    {
        if (_countLabel == null) return;
        int total = Items.Count();
        int sel = _list.GetSelectedItems().Length;
        _countLabel.Text = $"{_shown.Count}/{total}" + (sel > 1 ? $" · выбрано {sel}" : "");
    }

    private void RefreshGroups()
    {
        var groups = Items.Select(d => d.Group).Where(g => !string.IsNullOrEmpty(g)).Distinct().OrderBy(g => g, StringComparer.CurrentCulture).ToList();
        string chosen = _groupFilter.Selected > 0 && _groupFilter.Selected <= _groups.Count ? _groups[_groupFilter.Selected - 1] : "";
        bool noneChosen = _groupFilter.Selected == _groups.Count + 1 && _groupFilter.ItemCount > _groups.Count + 1;
        _groups.Clear();
        _groups.AddRange(groups);
        _groupFilter.Clear();
        _groupFilter.AddItem("Все категории");
        foreach (var g in _groups) _groupFilter.AddItem(g);
        _groupFilter.AddItem("(без категории)");
        _groupFilter.Selected = noneChosen ? _groups.Count + 1 : Math.Max(0, _groups.IndexOf(chosen) + 1);
    }

    private bool PassesFilters(Def d, string q)
    {
        // Word forms, synonyms and the other keyboard layout count too (docs/DECISIONS_5.md §77).
        if (q.Length > 0 && !(d.Id.ToLowerInvariant().Contains(q) || d.Name.ToLowerInvariant().Contains(q) || d.Group.ToLowerInvariant().Contains(q)
                              || d.Tags.Any(t => t.ToLowerInvariant().Contains(q)) || ContentSearch.Score(ContentSearch.EntryOf(d), q) > 0
                              || (RuText.SwapLayout(q) is { } sw && ContentSearch.Score(ContentSearch.EntryOf(d), sw) > 0)))
            return false;
        int g = _groupFilter.Selected;
        if (g > 0 && g <= _groups.Count && d.Group != _groups[g - 1]) return false;
        if (g == _groups.Count + 1 && !string.IsNullOrEmpty(d.Group)) return false;
        return _filterMode.Selected switch
        {
            1 => Ctx.WorstOf(d) == IssueSeverity.Error,
            2 => Ctx.WorstOf(d) == IssueSeverity.Warning,
            3 => Main.Instance.Content.IsFavorite(d),
            4 => d.Incomplete,
            _ => true,
        };
    }

    private IEnumerable<Def> Sorted(IEnumerable<Def> items) => _sort.Selected switch
    {
        1 => items.OrderBy(d => d.DisplayName, StringComparer.CurrentCulture),
        2 => items.OrderBy(d => d.Id, StringComparer.Ordinal),
        3 => items.OrderBy(d => string.IsNullOrEmpty(d.Group) ? "\uffff" : d.Group, StringComparer.CurrentCulture).ThenBy(d => d.DisplayName, StringComparer.CurrentCulture),
        4 => items.OrderByDescending(d => d.Version).ThenBy(d => d.DisplayName, StringComparer.CurrentCulture),
        5 => items.OrderByDescending(d => (int?)Ctx.WorstOf(d) ?? -1).ThenBy(d => d.DisplayName, StringComparer.CurrentCulture),
        _ => items, // content order (as authored)
    };

    private string ItemText(Def d)
    {
        var worst = Ctx.WorstOf(d);
        string mark = worst == IssueSeverity.Error ? "✗ " : worst == IssueSeverity.Warning ? "⚠ " : "";
        string fav = Main.Instance.Content.IsFavorite(d) ? "★ " : "";
        string draft = d.Incomplete ? " (неполный)" : "";
        // ⚙ — the core reads the characteristic (ТЗ ч. 5, 12.5); the others act only through content.
        string core = d is StatDef st && (StatUsage.Core.ContainsKey(st.Id) || st.Id.StartsWith("resist_", StringComparison.Ordinal)) ? "⚙ " : "";
        return mark + fav + core + (string.IsNullOrEmpty(d.Name) ? d.Id : $"{d.Name}  [{d.Id}]") + draft;
    }

    private static string ItemTooltip(Def d) =>
        $"{d.DisplayName} [{d.Id}] · v{d.Version}" + (string.IsNullOrEmpty(d.Group) ? "" : $"\nКатегория: {d.Group}") +
        (d.Tags.Count > 0 ? "\nТеги: " + string.Join(", ", d.Tags) : "");

    private void SelectFirst()
    {
        var first = Items.FirstOrDefault();
        if (first != null) Select(first);
        else ShowDetail(null);
    }

    public void RefreshList()
    {
        if (_list == null) return;
        string q = _search.Text.Trim().ToLowerInvariant();
        var keep = new HashSet<Def>(_list.GetSelectedItems().Where(i => i < _shown.Count).Select(i => _shown[i]));
        RefreshGroups();
        _list.Clear();
        _shown.Clear();
        foreach (var d in Sorted(Items.Where(d => PassesFilters(d, q))))
        {
            _shown.Add(d);
            var worst = Ctx.WorstOf(d);
            int idx = _list.AddItem(ItemText(d));
            _list.SetItemTooltip(idx, ItemTooltip(d));
            if (worst == IssueSeverity.Error) _list.SetItemCustomFgColor(idx, Ui.Bad);
            else if (worst == IssueSeverity.Warning) _list.SetItemCustomFgColor(idx, Ui.Warn);
            if (d == Current || keep.Contains(d)) _list.Select(idx, false);
        }
        UpdateCount();
    }

    /// <summary>A click in the list: show the definition without dropping the multi-selection.</summary>
    private void SelectFromList(Def d)
    {
        Current = d;
        ShowDetail(d);
        SelectionChanged?.Invoke(d);
    }

    public void Select(string id)
    {
        var d = Ctx.Db.Table(Category).Get(id);
        if (d != null) Select(d);
    }

    public void Select(Def d)
    {
        Current = d;
        int idx = _shown.IndexOf(d);
        if (idx >= 0) _list.Select(idx);
        ShowDetail(d);
        SelectionChanged?.Invoke(d);
    }

    private void ShowDetail(Def? d)
    {
        Ui.ClearChildren(DetailRoot);
        _header.Text = d == null ? "Нет объектов — нажмите «Создать»" : $"{ContentValidator.CatName(Category)}: {d.DisplayName} [{d.Id}]";
        RefreshIssues();
        if (d == null) return;
        DetailRoot.AddChild(BuildDetail(d));
    }

    /// <summary>
    /// Runs after the detail form is laid out — unless it was replaced in between (content reloaded,
    /// another object or section chosen): then the stale preview is not built.
    /// </summary>
    protected static void AfterLayout(Control anchor, Action action) => Callable.From(() =>
    {
        if (IsInstanceValid(anchor) && anchor.IsInsideTree()) action();
    }).CallDeferred();

    /// <summary>Form of the selected definition. Override for custom editors.</summary>
    protected virtual Control BuildDetail(Def d)
    {
        var grid = new PropertyGrid();
        grid.SetTarget(d, Ctx.Db);
        grid.Changed += () => OnEdited(d);
        if (d is not StatDef stat) return grid;
        // ТЗ ч. 5, 12.5: whether the core reads the characteristic or it is a field without behaviour.
        var (use, text) = StatUsage.Of(Ctx.Db, stat.Id);
        var box = Ui.VBox(6);
        var mark = Ui.Label((use == StatUse.Core ? "⚙ " : use == StatUse.Content ? "◇ " : "○ ") + text, 13,
            use == StatUse.Core ? Ui.Good : use == StatUse.Content ? Ui.Accent : Ui.Warn, wrap: true);
        mark.Name = "StatUsage";
        box.AddChild(mark);
        box.AddChild(grid);
        return box;
    }

    protected void OnEdited(Def d)
    {
        var table = Ctx.Db.Table(Category);
        if (table.Get(d.Id) != d)
        {
            // The ID was edited in place: the history compares everything (the old ID is gone).
            table.Reindex();
            Ctx.MarkDirty();
        }
        else Ctx.MarkDirty(d);
        _header.Text = $"{ContentValidator.CatName(Category)}: {d.DisplayName} [{d.Id}]";
        int idx = _shown.IndexOf(d);
        if (idx >= 0) _list.SetItemText(idx, ItemText(d));
    }

    private void OnValidated()
    {
        RefreshIssues();
        // Update list markers without rebuilding the selection.
        for (int i = 0; i < _shown.Count; i++)
        {
            var worst = Ctx.WorstOf(_shown[i]);
            var d = _shown[i];
            _list.SetItemText(i, ItemText(d));
            _list.SetItemCustomFgColor(i, worst == IssueSeverity.Error ? Ui.Bad : worst == IssueSeverity.Warning ? Ui.Warn : Ui.Text);
        }
    }

    private void RefreshIssues()
    {
        if (_issuesBox == null) return;
        Ui.ClearChildren(_issuesBox);
        if (Current == null) return;
        foreach (var i in Ctx.IssuesOf(Current).Take(12))
        {
            var col = i.Severity == IssueSeverity.Error ? Ui.Bad : i.Severity == IssueSeverity.Warning ? Ui.Warn : Ui.TextDim;
            string path = i.Path.StartsWith(Current.Id + ".") ? i.Path[(Current.Id.Length + 1)..] : i.Path;
            _issuesBox.AddChild(Ui.Label($"{(i.Severity == IssueSeverity.Error ? "✗" : "⚠")} {path}: {i.Message}", 13, col, wrap: true));
        }
    }

    // ------------------------------------------------------------------ CRUD

    protected virtual Def CreateNew()
    {
        if (_factory != null) return _factory();
        var d = (Def)Activator.CreateInstance(ContentDatabase.TypeOf(Category))!;
        d.Name = "Новый объект";
        return d;
    }

    private void Create()
    {
        var d = CreateNew();
        d.Id = ContentIO.UniqueId(Ctx.Db, Category, string.IsNullOrEmpty(d.Id) ? "new_" + Category.ToString().ToLowerInvariant() : d.Id);
        Ctx.Db.Add(d);
        Ctx.MarkDirty();
        RefreshList();
        Select(d);
    }

    private void Duplicate()
    {
        if (Current == null) return;
        var copy = ContentJson.Clone(Current);
        copy.Id = ContentIO.UniqueId(Ctx.Db, Category, Current.Id);
        copy.Name = Current.Name + " (копия)";
        Ctx.Db.Add(copy);
        Ctx.MarkDirty();
        RefreshList();
        Select(copy);
    }

    /// <summary>Renames the object (name only): IDs are stable after publication (docs/DECISIONS_2.md §31).</summary>
    private void Rename()
    {
        if (Current == null) return;
        var cur = Current;
        Dialogs.Prompt(this, "Переименовать", $"Новое название для [{cur.Id}]. ID не меняется — ссылки и пакеты остаются действительными.", cur.DisplayName, name =>
        {
            if (name.Trim().Length == 0 || name == cur.Name) return;
            cur.Name = name.Trim();
            Ctx.MarkDirty();
            RefreshList();
            Select(cur);
        });
    }

    private void Delete()
    {
        if (Current == null) return;
        var cur = Current;
        var refs = Ctx.Db.FindReferences(Category, cur.Id);
        string text = refs.Count == 0
            ? $"Удалить «{cur.DisplayName}» [{cur.Id}]?"
            : $"«{cur.DisplayName}» {ContentDependencies.Summary(refs)}; на него ссылаются {refs.Count} мест(а):\n" + string.Join("\n", refs.Take(12).Select(r => $" • {r.OwnerLabel} → {r.Path}")) +
              (refs.Count > 12 ? $"\n … и ещё {refs.Count - 12}" : "") + "\n\nПосле удаления эти ссылки станут ошибками (их покажет проверка). Удалить?";
        Dialogs.Confirm(this, "Удаление", text, () =>
        {
            Ctx.Db.Table(Category).Remove(cur.Id);
            Ctx.MarkDirty();
            Current = null;
            RefreshList();
            SelectFirst();
        }, "Удалить");
    }

    // ------------------------------------------------------------------ library: favourites, dependencies, versions, mass operations

    private void ToggleFavorite()
    {
        var sel = SelectedDefs();
        if (sel.Count == 0) return;
        var cm = Main.Instance.Content;
        bool on = !sel.All(cm.IsFavorite);
        foreach (var d in sel) cm.SetFavorite(d, on);
        RefreshList();
    }

    private void ShowDependencies()
    {
        if (Current != null) LibraryDialogs.Dependencies(this, Ctx, Current);
    }

    private void ShowVersions()
    {
        if (Current == null) return;
        LibraryDialogs.Versions(this, Ctx, Current, restored =>
        {
            RefreshList();
            Select(restored);
        });
    }

    private void MassOperation(int op)
    {
        var sel = SelectedDefs();
        if (op == 8)
        {
            for (int i = 0; i < _shown.Count; i++) _list.Select(i, false);
            UpdateCount();
            return;
        }
        if (sel.Count == 0) return;
        if (op == 9)
        {
            PackageDialogs.Export(this, Ctx, sel);
            return;
        }
        var cm = Main.Instance.Content;
        string names = string.Join(", ", sel.Take(8).Select(d => d.DisplayName)) + (sel.Count > 8 ? $" … (+{sel.Count - 8})" : "");
        switch (op)
        {
            case 0:
            {
                var refs = sel.SelectMany(d => Ctx.Db.FindReferences(Category, d.Id)).Where(r => r.Owner == null || !sel.Contains(r.Owner)).ToList();
                string text = $"Удалить {sel.Count} объект(ов): {names}?" + (refs.Count == 0 ? "" :
                    $"\n\nНа них ссылаются {refs.Count} мест(а) в других объектах — эти ссылки станут ошибками:\n" +
                    string.Join("\n", refs.Take(10).Select(r => $" • {r.OwnerLabel} → {r.Path}")));
                Dialogs.Confirm(this, "Массовое удаление", text, () =>
                {
                    RestorePoints.Create(Ctx, $"перед удалением {sel.Count} объектов");
                    Ctx.Commit();
                    foreach (var d in sel) Ctx.Db.Table(Category).Remove(d.Id);
                    Ctx.MarkDirty();
                    Ctx.Commit($"Массовое удаление ({sel.Count})");
                    Current = null;
                    RefreshList();
                    SelectFirst();
                }, "Удалить");
                break;
            }
            case 1:
            case 2:
                Dialogs.Prompt(this, op == 1 ? "Добавить тег" : "Убрать тег", $"Тег для {sel.Count} объект(ов): {names}", "", tag =>
                {
                    if (tag.Length == 0) return;
                    foreach (var d in sel)
                        if (op == 1 && !d.Tags.Contains(tag)) d.Tags.Add(tag);
                        else if (op == 2) d.Tags.Remove(tag);
                    Ctx.MarkDirty();
                    RefreshList();
                    if (Current != null) ShowDetail(Current);
                });
                break;
            case 3:
                Dialogs.Prompt(this, "Категория в библиотеке", $"Категория (группа) для {sel.Count} объект(ов): {names}. Пусто — без категории.", sel[0].Group, g =>
                {
                    foreach (var d in sel) d.Group = g;
                    Ctx.MarkDirty();
                    RefreshList();
                    if (Current != null) ShowDetail(Current);
                });
                break;
            case 4:
            case 5:
                foreach (var d in sel) cm.SetFavorite(d, op == 4);
                RefreshList();
                break;
            case 6:
                foreach (var d in sel) d.Incomplete = false;
                Ctx.MarkDirty();
                RefreshList();
                if (Current != null) ShowDetail(Current);
                break;
            case 7:
                Dialogs.OpenDir(this, "Папка для экспорта", dir =>
                {
                    int n = 0;
                    try
                    {
                        foreach (var d in sel)
                        {
                            File.WriteAllText(Path.Combine(dir, ContentIO.SafeFileName(d.Id) + ".json"), ContentIO.ExportDef(d));
                            n++;
                        }
                        Dialogs.Info(this, "Экспорт", $"Экспортировано файлов: {n}\n{dir}");
                    }
                    catch (Exception e)
                    {
                        Dialogs.Info(this, "Ошибка экспорта", e.Message);
                    }
                });
                break;
            default:
                if (op >= 20) UnitMass(op, sel.OfType<UnitDef>().ToList());
                break;
        }
    }

    private void Import()
    {
        PackageDialogs.Import(this, Ctx, report =>
        {
            var first = report.Added.Concat(report.Copied.Select(c => c.Def)).Concat(report.Replaced).FirstOrDefault(d => d.Category == Category);
            RefreshList();
            if (first != null) Select(first);
        });
    }

    private void ExportPackage()
    {
        var sel = SelectedDefs();
        if (sel.Count > 0) PackageDialogs.Export(this, Ctx, sel);
    }

    private void Export()
    {
        if (Current == null) return;
        var cur = Current;
        Dialogs.SaveFile(this, "Экспорт объекта", cur.Id + ".json", path =>
        {
            try
            {
                File.WriteAllText(path, ContentIO.ExportDef(cur));
            }
            catch (Exception e)
            {
                Dialogs.Info(this, "Ошибка экспорта", e.Message);
            }
        });
    }
}
