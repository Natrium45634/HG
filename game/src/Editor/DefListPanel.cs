using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// Generic editor of one content category: searchable list with create / duplicate / rename /
/// delete (with dependency check) / import / export, and a form for the selected definition.
/// Specialised editors derive from it and override <see cref="BuildDetail"/>.
/// </summary>
public partial class DefListPanel : HSplitContainer
{
    protected readonly EditorContext Ctx;
    protected readonly DefCategory Category;
    private readonly string _title;
    private readonly Func<Def, bool>? _filter;
    private readonly Func<Def>? _factory;

    private LineEdit _search = null!;
    private ItemList _list = null!;
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
        _search = new LineEdit { PlaceholderText = "Поиск по названию, ID или тегу" };
        _search.TextChanged += _ => RefreshList();
        left.AddChild(_search);
        _list = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill, AllowReselect = true };
        _list.ItemSelected += i => Select(_shown[(int)i]);
        left.AddChild(_list);
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 4);
        grid.AddThemeConstantOverride("v_separation", 4);
        grid.AddChild(Ui.Button("Создать", Create, "Новый объект с значениями по умолчанию").Expand());
        grid.AddChild(Ui.Button("Дублировать", Duplicate, "Копия выбранного (создание из шаблона) с новым ID").Expand());
        grid.AddChild(Ui.Button("Переименовать", Rename, "Сменить название (ID неизменен)").Expand());
        grid.AddChild(Ui.Button("Удалить", Delete, "С проверкой зависимостей").Expand());
        grid.AddChild(Ui.Button("Импорт…", Import, "Загрузить объект из JSON-файла").Expand());
        grid.AddChild(Ui.Button("Экспорт…", Export, "Сохранить выбранный объект в JSON-файл").Expand());
        left.AddChild(grid);
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
        Ctx.ContentReplaced += () =>
        {
            Current = null;
            RefreshList();
            SelectFirst();
        };
        RefreshList();
        SelectFirst();
    }

    public override void _ExitTree()
    {
        Ctx.Validated -= OnValidated;
    }

    protected IEnumerable<Def> Items => Ctx.Db.Table(Category).Items.Where(d => _filter == null || _filter(d));

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
        _list.Clear();
        _shown.Clear();
        foreach (var d in Items)
        {
            if (q.Length > 0 && !(d.Id.ToLowerInvariant().Contains(q) || d.Name.ToLowerInvariant().Contains(q) || d.Tags.Any(t => t.ToLowerInvariant().Contains(q)))) continue;
            _shown.Add(d);
            var worst = Ctx.WorstOf(d);
            string mark = worst == IssueSeverity.Error ? "✗ " : worst == IssueSeverity.Warning ? "⚠ " : "";
            int idx = _list.AddItem(mark + (string.IsNullOrEmpty(d.Name) ? d.Id : $"{d.Name}  [{d.Id}]"));
            if (worst == IssueSeverity.Error) _list.SetItemCustomFgColor(idx, Ui.Bad);
            else if (worst == IssueSeverity.Warning) _list.SetItemCustomFgColor(idx, Ui.Warn);
            if (d == Current) _list.Select(idx);
        }
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

    /// <summary>Form of the selected definition. Override for custom editors.</summary>
    protected virtual Control BuildDetail(Def d)
    {
        var grid = new PropertyGrid();
        grid.SetTarget(d, Ctx.Db);
        grid.Changed += () => OnEdited(d);
        return grid;
    }

    protected void OnEdited(Def d)
    {
        var table = Ctx.Db.Table(Category);
        if (table.Get(d.Id) != d) table.Reindex();
        Ctx.MarkDirty();
        _header.Text = $"{ContentValidator.CatName(Category)}: {d.DisplayName} [{d.Id}]";
        int idx = _shown.IndexOf(d);
        if (idx >= 0) _list.SetItemText(idx, string.IsNullOrEmpty(d.Name) ? d.Id : $"{d.Name}  [{d.Id}]");
    }

    private void OnValidated()
    {
        RefreshIssues();
        // Update list markers without rebuilding the selection.
        for (int i = 0; i < _shown.Count; i++)
        {
            var worst = Ctx.WorstOf(_shown[i]);
            string mark = worst == IssueSeverity.Error ? "✗ " : worst == IssueSeverity.Warning ? "⚠ " : "";
            var d = _shown[i];
            _list.SetItemText(i, mark + (string.IsNullOrEmpty(d.Name) ? d.Id : $"{d.Name}  [{d.Id}]"));
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
            : $"На «{cur.DisplayName}» ссылаются {refs.Count} мест(а):\n" + string.Join("\n", refs.Take(12).Select(r => $" • {r.OwnerLabel} → {r.Path}")) +
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

    private void Import()
    {
        Dialogs.OpenFile(this, "Импорт объекта", path =>
        {
            var issues = new List<ContentIssue>();
            Def? d;
            try
            {
                d = ContentIO.ImportDef(File.ReadAllText(path), issues);
            }
            catch (Exception e)
            {
                Dialogs.Info(this, "Ошибка импорта", e.Message);
                return;
            }
            if (d == null || d.Category != Category)
            {
                Dialogs.Info(this, "Ошибка импорта", d == null ? string.Join("\n", issues) : $"Файл содержит {ContentValidator.CatName(d.Category)}, а не {ContentValidator.CatName(Category)}.");
                return;
            }
            if (Ctx.Db.Exists(Category, d.Id)) d.Id = ContentIO.UniqueId(Ctx.Db, Category, d.Id);
            Ctx.Db.Add(d);
            Ctx.MarkDirty();
            RefreshList();
            Select(d);
            Ctx.Validate();
            var problems = Ctx.IssuesOf(d).Where(i => i.Severity == IssueSeverity.Error).ToList();
            if (problems.Count > 0) Dialogs.Info(this, "Импортировано с ошибками", string.Join("\n", problems.Take(10).Select(p => p.Message)));
        });
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
