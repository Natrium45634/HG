using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Core.Expr;
using HexTactics.Core.Hex;

namespace HexTactics.Game.UI;

/// <summary>
/// Generic visual editor of a content object, built by reflection from [Ed*] attributes:
/// labels/tooltips, groups, ranges, references to other definitions (drop-downs), conditional
/// fields, nested objects, lists (add/remove/reorder/duplicate), dictionaries, colours, formulas.
/// Adding a field with an attribute to a definition makes it editable without UI code.
/// </summary>
public partial class PropertyGrid : VBoxContainer
{
    private object? _target;
    private ContentDatabase? _db;
    private readonly List<(PropertyInfo Prop, object Owner, Control Row)> _conditional = new();
    private readonly HashSet<string> _collapsed = new();
    private int _depth;

    public event Action? Changed;
    /// <summary>Raised when a property changes (owner, property name) — used by custom editors.</summary>
    public event Action<object, string>? PropertyChanged;

    public float LabelWidth { get; set; } = 210;
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Returns true when the ID of a definition may no longer change (published objects:
    /// docs/DECISIONS_2.md §31). Set by the editor context.
    /// </summary>
    public static Func<Def, bool>? IdLocked { get; set; }
    /// <summary>
    /// The search index of a database (docs/DECISIONS_5.md §77): the editor gives its draft's, null — the grid builds its
    /// own. Reference lists search it.
    /// </summary>
    public static Func<ContentDatabase, ContentSearch?>? SearchIndex { get; set; }
    /// <summary>Creates an object by name in the database, from a reference list («＋ Создать «…»»); null — no creation.</summary>
    public static Func<ContentDatabase, DefCategory, string, Def?>? CreateRef { get; set; }
    /// <summary>Categories a reference list may create by name: simple objects that make sense with default values.</summary>
    public static readonly HashSet<DefCategory> CreatableByName = new()
    {
        DefCategory.Element, DefCategory.DamageType, DefCategory.Stat, DefCategory.Status, DefCategory.Resource, DefCategory.Passive,
        DefCategory.Origin, DefCategory.Faction, DefCategory.Formation, DefCategory.AiProfile,
    };
    private ContentSearch? _ownIndex;

    private ContentSearch Index()
    {
        var db = _db ?? new ContentDatabase();
        return SearchIndex?.Invoke(db) ?? (_ownIndex ??= ContentSearch.Build(db));
    }

    /// <summary>Properties to skip (by name) at the top level (edited by a custom tool).</summary>
    public HashSet<string> Skip { get; } = new();
    /// <summary>
    /// Choices for string properties by name (IDs inside a scenario: bases, routes — docs/DECISIONS_4.md §69): shown as
    /// a list instead of a text field; nested grids inherit them.
    /// </summary>
    public Dictionary<string, Func<IEnumerable<(string Id, string Label)>>> Choices { get; } = new();
    /// <summary>
    /// Top-level properties whose values differ between the objects the form edits at once (several cells of a map,
    /// ТЗ ч. 5, 5.8): the row is marked «≠» with the values in its tooltip; a change sets the value for all.
    /// </summary>
    public Dictionary<string, string> Mixed { get; } = new();

    public PropertyGrid()
    {
        AddThemeConstantOverride("separation", 4);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
    }

    public object? Target => _target;

    public void SetTarget(object? target, ContentDatabase? db, int depth = 0)
    {
        _target = target;
        _db = db;
        _depth = depth;
        Rebuild();
    }

    public void Rebuild()
    {
        Ui.ClearChildren(this);
        _conditional.Clear();
        _inheritHints.Clear();
        if (_target == null) return;
        BuildObject(this, _target, _depth == 0);
        UpdateVisibility();
    }

    private void RaiseChanged(object owner, string prop)
    {
        PropertyChanged?.Invoke(owner, prop);
        Changed?.Invoke();
        RefreshInheritHints();
    }

    // ------------------------------------------------------------------ inheritance hints (elements, damage types)

    private readonly List<(IInheritable Owner, string Prop, Label Hint)> _inheritHints = new();

    private void AddInheritHint(VBoxContainer parent, object owner, PropertyInfo prop)
    {
        if (owner is not IInheritable inh || prop.GetCustomAttribute<EdInheritAttribute>() == null || _db == null) return;
        var h = Ui.HBox(4);
        h.AddChild(Ui.Spacer(LabelWidth));
        var label = Ui.Label("", 12, Ui.TextDim, wrap: true).Expand();
        h.AddChild(label);
        parent.AddChild(h);
        _inheritHints.Add((inh, prop.Name, label));
        RefreshInheritHints();
    }

    private void RefreshInheritHints()
    {
        if (_db == null) return;
        foreach (var (owner, prop, label) in _inheritHints)
        {
            if (!GodotObject.IsInstanceValid(label)) continue;
            var text = owner.InheritedHint(_db, prop) ?? "";
            label.Text = text.Length > 0 ? "↳ " + text : "";
            label.AddThemeColorOverride("font_color", text.StartsWith("унаследовано") ? new Color(0.62f, 0.8f, 1f) : Ui.TextDim);
        }
    }

    // ------------------------------------------------------------------ damage matches ("*", type, el:, cat:, tag:)

    /// <summary>Hex list as text "q,r; q,r" (objective hexes, route points).</summary>
    private Control CoordList(VBoxContainer parent, object owner, PropertyInfo prop, EdAttribute ed, List<HexCoord> list)
    {
        var row = Row(parent, ed);
        var le = new LineEdit { Text = string.Join("; ", list.Select(c => $"{c.Q},{c.R}")), PlaceholderText = "q,r; q,r …" }.Expand();
        le.TooltipText = "Координаты гексов через «;», например 3,2; 4,2";
        le.TextSubmitted += _ => le.ReleaseFocus();
        le.FocusExited += () =>
        {
            var parsed = new List<HexCoord>();
            foreach (var part in le.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var xy = part.Split(',', StringSplitOptions.TrimEntries);
                if (xy.Length == 2 && int.TryParse(xy[0], out var q) && int.TryParse(xy[1], out var r)) parsed.Add(new HexCoord(q, r));
            }
            list.Clear();
            list.AddRange(parsed);
            le.Text = string.Join("; ", list.Select(c => $"{c.Q},{c.R}"));
            RaiseChanged(owner, prop.Name);
        };
        row.AddChild(le);
        return row;
    }

    public OptionButton MatchOption(string current, Action<string> set)
    {
        var ob = new SearchableOption { FocusMode = FocusModeEnum.None, FitToLongestItem = false, ClipText = true, CustomMinimumSize = new Vector2(90, 0) };
        var options = _db != null ? DamageTaxonomy.MatchOptions(_db) : new List<(string, string)> { ("*", "любой урон") };
        var ids = options.Select(o => o.Item1).ToList();
        foreach (var (_, label) in options) ob.AddItem(label);
        if (!string.IsNullOrEmpty(current) && !ids.Contains(current))
        {
            ids.Add(current);
            ob.AddItem(current.StartsWith("tag:", StringComparison.Ordinal) ? $"тег: {current[4..]}" : $"⚠ {current} (не найден)");
        }
        ob.Selected = Math.Max(0, ids.IndexOf(string.IsNullOrEmpty(current) ? "*" : current));
        ob.TooltipText = "Любой урон, тип урона (и его потомки), элемент (и его потомки) или категория · клик — поиск";
        ob.ItemSelected += idx => set(ids[(int)idx]);
        ob.EntryOf = i => i < ids.Count ? MatchEntry(ids[i]) : null;
        if (_db != null && CreateRef != null)
        {
            // A damage not in the content yet: its element and a damage type of it, linked; the match takes the element.
            ob.CreateKind = "элемент и тип урона";
            ob.Create = q =>
            {
                var t = CreateRef(_db, DefCategory.DamageType, q) as DamageTypeDef;
                if (t == null) return -1;
                if (string.IsNullOrEmpty(t.Element) && CreateRef(_db, DefCategory.Element, q) is ElementDef el) t.Element = el.Id;
                var v = string.IsNullOrEmpty(t.Element) ? t.Id : DamageTaxonomy.ElementPrefix + t.Element;
                ids.Add(v);
                ob.AddItem(string.IsNullOrEmpty(t.Element) ? $"тип: {t.DisplayName}" : $"элемент: {_db.Element(t.Element)?.DisplayName ?? t.Element}");
                return ob.ItemCount - 1;
            };
        }
        return ob;
    }

    private SearchEntry? MatchEntry(string v)
    {
        var index = Index();
        if (v == DamageTaxonomy.Any || v == "") return ContentSearch.TextEntry("любой урон", DamageTaxonomy.Any, "любой");
        if (v.StartsWith(DamageTaxonomy.ElementPrefix, StringComparison.Ordinal)) return index.Get(DefCategory.Element, v[DamageTaxonomy.ElementPrefix.Length..]);
        if (v.StartsWith(DamageTaxonomy.CategoryPrefix, StringComparison.Ordinal) || v.StartsWith(DamageTaxonomy.TagPrefix, StringComparison.Ordinal)) return index.Get(null, v);
        return index.Get(DefCategory.DamageType, v);
    }

    private Control MatchList(VBoxContainer parent, object owner, PropertyInfo prop, EdAttribute ed, List<string> list)
    {
        var row = Row(parent, ed);
        var col = Ui.VBox(3).Expand();
        row.AddChild(col);
        void Fill()
        {
            Ui.ClearChildren(col);
            for (int i = 0; i < list.Count; i++)
            {
                int idx = i;
                var h = Ui.HBox(4);
                h.AddChild(MatchOption(list[i], v =>
                {
                    list[idx] = v;
                    RaiseChanged(owner, prop.Name);
                }).Expand());
                h.AddChild(Ui.Button("✕", () =>
                {
                    list.RemoveAt(idx);
                    RaiseChanged(owner, prop.Name);
                    Fill();
                }, "Удалить"));
                col.AddChild(h);
            }
            col.AddChild(Ui.Button("+ добавить", () =>
            {
                list.Add("*");
                RaiseChanged(owner, prop.Name);
                Fill();
            }));
        }
        Fill();
        return row;
    }

    private static IEnumerable<PropertyInfo> EditableProps(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<EdAttribute>() != null && p.GetCustomAttribute<EdHiddenAttribute>() == null && p.GetIndexParameters().Length == 0)
            .Select((p, i) => (p, i, order: p.GetCustomAttribute<EdAttribute>()!.Order))
            .OrderBy(x => x.order).ThenBy(x => DeclarationRank(t, x.p)).ThenBy(x => x.i)
            .Select(x => x.p);

    // Base class (Def) properties first, then the derived ones in declaration order.
    private static int DeclarationRank(Type t, PropertyInfo p) => p.DeclaringType == t ? 1 : 0;

    private void BuildObject(VBoxContainer parent, object obj, bool topLevel)
    {
        var groups = new List<(string Name, VBoxContainer Box)>();
        VBoxContainer BoxFor(string group)
        {
            if (string.IsNullOrEmpty(group)) return parent;
            foreach (var g in groups)
                if (g.Name == group)
                    return g.Box;
            var section = Section(parent, group, topLevel ? 0 : 1, obj.GetType().Name + "/" + group);
            groups.Add((group, section));
            return section;
        }

        foreach (var prop in EditableProps(obj.GetType()))
        {
            if (topLevel && Skip.Contains(prop.Name)) continue;
            var ed = prop.GetCustomAttribute<EdAttribute>()!;
            var box = BoxFor(ed.Group);
            var row = BuildProperty(box, obj, prop, ed);
            if (row != null && prop.GetCustomAttribute<EdShowIfAttribute>() != null) _conditional.Add((prop, obj, row));
            if (topLevel && row != null && Mixed.TryGetValue(prop.Name, out var mixed) && row.GetChildCount() > 0 && row.GetChild(0) is Label ml)
            {
                ml.Text = ml.Text + " ≠";
                ml.AddThemeColorOverride("font_color", Ui.Warn);
                ml.TooltipText = $"Разные значения у выделенных — {mixed}. Изменение задаст значение всем.";
                ml.Name = "Mixed_" + prop.Name;
            }
        }
    }

    private VBoxContainer Section(VBoxContainer parent, string title, int level, string key)
    {
        var wrap = Ui.VBox(3);
        var header = new Button
        {
            Text = (_collapsed.Contains(key) ? "▸ " : "▾ ") + title,
            Alignment = HorizontalAlignment.Left,
            Flat = true,
            FocusMode = FocusModeEnum.None,
        };
        header.AddThemeColorOverride("font_color", level == 0 ? Ui.Accent : Ui.Text);
        header.AddThemeFontSizeOverride("font_size", level == 0 ? 15 : 14);
        var content = Ui.VBox(4);
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddChild(content);
        margin.Visible = !_collapsed.Contains(key);
        header.Pressed += () =>
        {
            margin.Visible = !margin.Visible;
            if (margin.Visible) _collapsed.Remove(key);
            else _collapsed.Add(key);
            header.Text = (margin.Visible ? "▾ " : "▸ ") + title;
        };
        wrap.AddChild(header);
        wrap.AddChild(margin);
        parent.AddChild(wrap);
        return content;
    }

    private HBoxContainer Row(VBoxContainer parent, EdAttribute ed)
    {
        var row = Ui.HBox(6);
        var label = Ui.Label(ed.Label, 14, Ui.TextDim);
        label.CustomMinimumSize = new Vector2(LabelWidth, 0);
        label.TooltipText = string.IsNullOrEmpty(ed.Tip) ? ed.Label : ed.Tip;
        label.MouseFilter = MouseFilterEnum.Pass;
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        row.AddChild(label);
        parent.AddChild(row);
        return row;
    }

    private static bool IsNumeric(Type t) => t == typeof(int) || t == typeof(double) || t == typeof(float) || t == typeof(long) || t == typeof(ulong);

    private Control? BuildProperty(VBoxContainer parent, object owner, PropertyInfo prop, EdAttribute ed)
    {
        var type = prop.PropertyType;
        var value = prop.GetValue(owner);
        var refAttr = prop.GetCustomAttribute<EdRefAttribute>();
        var nullable = Nullable.GetUnderlyingType(type);

        // Nested object.
        if (type.IsClass && type != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(type))
        {
            // An optional (null) nested object is shown with defaults and stored only once it is edited,
            // so unused options do not fill the JSON.
            bool wasNull = value == null;
            value ??= Activator.CreateInstance(type);
            var section = Section(parent, ed.Label, 1, owner.GetType().Name + "." + prop.Name + "#" + owner.GetHashCode());
            var sub = new PropertyGrid { LabelWidth = Math.Max(140, LabelWidth - 14), ReadOnly = ReadOnly };
            foreach (var kv in Choices) sub.Choices[kv.Key] = kv.Value;
            sub.SetTarget(value!, _db, _depth + 1);
            var created = value;
            sub.Changed += () =>
            {
                if (wasNull && prop.CanWrite && prop.GetValue(owner) == null) prop.SetValue(owner, created);
                Changed?.Invoke();
            };
            sub.PropertyChanged += (o, p) => PropertyChanged?.Invoke(o, p);
            section.AddChild(sub);
            return section.GetParent().GetParent() as Control;
        }

        // Lists of objects.
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            var itemType = type.GetGenericArguments()[0];
            if (value == null)
            {
                value = Activator.CreateInstance(type);
                prop.SetValue(owner, value);
            }
            var list = (IList)value!;
            if (itemType == typeof(string) && prop.GetCustomAttribute<EdDamageMatchAttribute>() != null)
                return MatchList(parent, owner, prop, ed, (List<string>)list);
            if (itemType == typeof(string))
            {
                var c = refAttr != null ? RefList(parent, owner, prop, ed, (List<string>)list, refAttr) : TagList(parent, owner, prop, ed, (List<string>)list);
                AddInheritHint(parent, owner, prop);
                return c;
            }
            if (itemType.IsClass && prop.GetCustomAttribute<EdInheritAttribute>() != null)
            {
                var c = ObjectList(parent, owner, prop, ed, list, itemType);
                AddInheritHint(parent, owner, prop);
                return c;
            }
            if (itemType == typeof(double)) return NumberList(parent, owner, prop, ed, (List<double>)list);
            if (itemType == typeof(HexCoord)) return CoordList(parent, owner, prop, ed, (List<HexCoord>)list);
            if (itemType.IsClass) return ObjectList(parent, owner, prop, ed, list, itemType);
            return null;
        }

        // Dictionaries string → double.
        if (type == typeof(Dictionary<string, double>))
        {
            if (value == null)
            {
                value = new Dictionary<string, double>();
                prop.SetValue(owner, value);
            }
            return DictEditor(parent, owner, prop, ed, (Dictionary<string, double>)value!);
        }

        var row = Row(parent, ed);
        Control editor;
        if (nullable != null)
        {
            var check = new CheckBox { Text = "задано", ButtonPressed = value != null, FocusMode = FocusModeEnum.None };
            row.AddChild(check);
            var inner = Ui.HBox(4).Expand();
            row.AddChild(inner);
            void Fill()
            {
                Ui.ClearChildren(inner);
                var cur = prop.GetValue(owner);
                if (cur == null) return;
                inner.AddChild(ScalarEditor(nullable, cur, prop, v =>
                {
                    prop.SetValue(owner, v);
                    RaiseChanged(owner, prop.Name);
                }, refAttr));
            }
            check.Toggled += on =>
            {
                prop.SetValue(owner, on ? Activator.CreateInstance(nullable) : null);
                Fill();
                RaiseChanged(owner, prop.Name);
            };
            Fill();
            return row;
        }
        if (type == typeof(string) && prop.GetCustomAttribute<EdDamageMatchAttribute>() != null)
            editor = MatchOption((string?)value ?? "*", v =>
            {
                prop.SetValue(owner, v);
                RaiseChanged(owner, prop.Name);
            });
        else
            editor = ScalarEditor(type, value, prop, v =>
            {
                prop.SetValue(owner, v);
                RaiseChanged(owner, prop.Name);
                if (prop.PropertyType.IsEnum || prop.PropertyType == typeof(bool)) UpdateVisibility();
            }, refAttr);
        editor.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        if (!string.IsNullOrEmpty(ed.Tip)) editor.TooltipText = ed.Tip;
        row.AddChild(editor);
        if (prop.GetCustomAttribute<EdFormulaAttribute>() is { } fa) AddFormulaHelpers(parent, owner, prop, fa, (LineEdit)editor);
        AddInheritHint(parent, owner, prop);
        bool locked = prop.GetCustomAttribute<EdReadOnlyAttribute>() != null
                      || (owner is Def def && prop.Name == nameof(Def.Id) && IdLocked?.Invoke(def) == true);
        if (ReadOnly || locked) SetReadOnly(editor);
        if (locked && prop.Name == nameof(Def.Id)) editor.TooltipText = "ID опубликованного объекта не меняется. Новый ID — через «Дублировать».";
        return row;
    }

    private static void SetReadOnly(Control c)
    {
        switch (c)
        {
            case LineEdit le:
                le.Editable = false;
                break;
            case SpinBox sb:
                sb.Editable = false;
                break;
            case BaseButton bb:
                bb.Disabled = true;
                break;
        }
    }

    private Control ScalarEditor(Type type, object? value, PropertyInfo prop, Action<object?> set, EdRefAttribute? refAttr)
    {
        if (type == typeof(string))
        {
            var s = (string?)value ?? "";
            if (refAttr != null) return RefOption(refAttr, s, v => set(v));
            if (Choices.TryGetValue(prop.Name, out var choices)) return ChoiceOption(choices(), s, v => set(v));
            if (prop.GetCustomAttribute<EdColorAttribute>() != null)
            {
                // Empty colour = "not set" (no outline, default side colour…): shown explicitly, never as white.
                var box = Ui.HBox(4);
                var on = new CheckBox { Text = "задан", ButtonPressed = s.Length > 0, FocusMode = FocusModeEnum.None, TooltipText = "Снимите, чтобы не задавать цвет (значение по умолчанию)" };
                var cp = new ColorPickerButton { Color = Ui.Parse(s, Colors.White), CustomMinimumSize = new Vector2(80, 26), EditAlpha = false, Disabled = s.Length == 0 };
                cp.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                if (s.Length == 0) cp.Modulate = new Color(1, 1, 1, 0.25f);
                cp.ColorChanged += c => set("#" + c.ToHtml(false));
                on.Toggled += v =>
                {
                    cp.Disabled = !v;
                    cp.Modulate = v ? Colors.White : new Color(1, 1, 1, 0.25f);
                    set(v ? "#" + cp.Color.ToHtml(false) : "");
                };
                box.AddChild(on);
                box.AddChild(cp);
                return box;
            }
            if (prop.GetCustomAttribute<EdMultilineAttribute>() != null)
            {
                var te = new TextEdit { Text = s, CustomMinimumSize = new Vector2(0, 64), WrapMode = TextEdit.LineWrappingMode.Boundary };
                te.TextChanged += () => set(te.Text);
                return te;
            }
            var le = new LineEdit { Text = s };
            le.TextChanged += t => set(t);
            return le;
        }
        if (type == typeof(bool))
        {
            var cb = new CheckBox { ButtonPressed = value is true, FocusMode = FocusModeEnum.None };
            cb.Toggled += on => set(on);
            return cb;
        }
        if (IsNumeric(type))
        {
            var range = prop.GetCustomAttribute<EdRangeAttribute>();
            bool integer = type == typeof(int) || type == typeof(long) || type == typeof(ulong);
            var sb = new SpinBox
            {
                MinValue = range?.Min ?? (type == typeof(ulong) ? 0 : -1e9),
                MaxValue = range?.Max ?? 1e12,
                Step = range != null && range.Step > 0 ? range.Step : integer ? 1 : 0.01,
                AllowGreater = range == null,
                AllowLesser = range == null && type != typeof(ulong),
                CustomMinimumSize = new Vector2(110, 0),
                SelectAllOnFocus = true,
            };
            sb.Value = Convert.ToDouble(value ?? 0, CultureInfo.InvariantCulture);
            sb.ValueChanged += v =>
            {
                object boxed = type == typeof(int) ? (int)Math.Round(v)
                    : type == typeof(long) ? (long)Math.Round(v)
                    : type == typeof(ulong) ? (ulong)Math.Max(0, Math.Round(v))
                    : type == typeof(float) ? (float)v
                    : v;
                set(boxed);
            };
            return sb;
        }
        if (type.IsEnum)
        {
            var ob = new OptionButton { FocusMode = FocusModeEnum.None, FitToLongestItem = false, ClipText = true, CustomMinimumSize = new Vector2(90, 0) };
            var names = Enum.GetNames(type);
            for (int i = 0; i < names.Length; i++) ob.AddItem(Names.Of(type, names[i]), i);
            ob.Selected = Array.IndexOf(names, value?.ToString() ?? names[0]);
            ob.ItemSelected += idx => set(Enum.Parse(type, names[idx]));
            return ob;
        }
        if (type == typeof(HexCoord))
        {
            var h = (HexCoord)(value ?? HexCoord.Zero);
            var box = Ui.HBox(4);
            var q = new SpinBox { MinValue = -999, MaxValue = 999, Value = h.Q, Prefix = "q", CustomMinimumSize = new Vector2(80, 0) };
            var r = new SpinBox { MinValue = -999, MaxValue = 999, Value = h.R, Prefix = "r", CustomMinimumSize = new Vector2(80, 0) };
            q.ValueChanged += _ => set(new HexCoord((int)q.Value, (int)r.Value));
            r.ValueChanged += _ => set(new HexCoord((int)q.Value, (int)r.Value));
            box.AddChild(q);
            box.AddChild(r);
            return box;
        }
        return Ui.Label($"({type.Name})", 13, Ui.TextDim);
    }

    private static OptionButton ChoiceOption(IEnumerable<(string Id, string Label)> choices, string current, Action<string> set)
    {
        var ob = new OptionButton { FocusMode = FocusModeEnum.None, FitToLongestItem = false, ClipText = true, CustomMinimumSize = new Vector2(90, 0) };
        var ids = new List<string> { "" };
        ob.AddItem("(нет)");
        foreach (var (id, label) in choices)
        {
            ids.Add(id);
            ob.AddItem(label);
        }
        if (!ids.Contains(current))
        {
            ids.Add(current);
            ob.AddItem($"⚠ {current} (не найден)");
        }
        ob.Selected = Math.Max(0, ids.IndexOf(current));
        ob.ItemSelected += i => set(ids[(int)i]);
        return ob;
    }

    public OptionButton RefOption(EdRefAttribute refAttr, string current, Action<string> set, bool allowEmptyOverride = false)
    {
        var ob = new SearchableOption { FocusMode = FocusModeEnum.None, FitToLongestItem = false, ClipText = true, CustomMinimumSize = new Vector2(90, 0) };
        var ids = new List<string>();
        if (refAttr.AllowEmpty || allowEmptyOverride || string.IsNullOrEmpty(current))
        {
            ob.AddItem("(нет)");
            ids.Add("");
        }
        if (_db != null)
        {
            foreach (var id in _db.Ids(refAttr.Category))
            {
                ids.Add(id);
                ob.AddItem(LabelOf(refAttr.Category, id));
            }
        }
        if (!string.IsNullOrEmpty(current) && !ids.Contains(current))
        {
            // A pinned reference "id@N" (docs/DECISIONS_2.md §31): the old instance; choosing the plain id follows the latest version.
            var (bare, pin) = ContentRef.Parse(current);
            int at = ids.IndexOf(bare);
            bool ok = _db != null && pin != null && _db.Exists(refAttr.Category, current);
            string label = pin == null ? $"⚠ {current} (не найден)"
                : ok ? $"📌 {LabelOf(refAttr.Category, bare)} · v{pin} (старый экземпляр)"
                : $"⚠ {LabelOf(refAttr.Category, bare)} · v{pin} (версия не найдена)";
            if (at >= 0)
            {
                ids.Insert(at + 1, current);
                ob.AddItem(label);
                // OptionButton has no insert: move the item right after the latest version.
                for (int i = ob.ItemCount - 1; i > at + 1; i--) SwapItems(ob, i, i - 1);
            }
            else
            {
                ids.Add(current);
                ob.AddItem(label);
            }
        }
        ob.Selected = Math.Max(0, ids.IndexOf(current));
        ob.ItemSelected += idx => set(ids[(int)idx]);
        // Search over the draft: pinned old versions («id@N») are found by their item text.
        ob.EntryOf = i => i < ids.Count && ids[i] != "" && ids[i].IndexOf('@') < 0 ? Index().Get(refAttr.Category, ids[i]) : null;
        if (_db != null && CreateRef != null && CreatableByName.Contains(refAttr.Category))
        {
            ob.CreateKind = ContentSearch.KindOf(refAttr.Category);
            ob.Create = q =>
            {
                var d = CreateRef(_db, refAttr.Category, q);
                if (d == null) return -1;
                ids.Add(d.Id);
                ob.AddItem(LabelOf(refAttr.Category, d.Id));
                return ob.ItemCount - 1;
            };
        }
        return ob;
    }

    private static void SwapItems(OptionButton ob, int a, int b)
    {
        string ta = ob.GetItemText(a);
        ob.SetItemText(a, ob.GetItemText(b));
        ob.SetItemText(b, ta);
    }

    private string LabelOf(DefCategory c, string id)
    {
        if (_db == null || c == DefCategory.MoveCategory) return id;
        var d = _db.Table(c).Get(id);
        return d == null || string.IsNullOrEmpty(d.Name) || d.Name == id ? id : $"{d.Name} [{id}]";
    }

    private void AddFormulaHelpers(VBoxContainer parent, object owner, PropertyInfo prop, EdFormulaAttribute fa, LineEdit le)
    {
        var help = Ui.HBox(4);
        help.AddChild(Ui.Spacer(LabelWidth));
        var status = Ui.Label("", 12, Ui.TextDim);
        void Validate()
        {
            var t = le.Text;
            if (string.IsNullOrWhiteSpace(t))
            {
                status.Text = "встроенная формула";
                status.AddThemeColorOverride("font_color", Ui.TextDim);
            }
            else if (SafeExpression.TryParse(t, fa.Variables, out _, out var err))
            {
                status.Text = "✓ формула корректна";
                status.AddThemeColorOverride("font_color", Ui.Good);
            }
            else
            {
                status.Text = "✗ " + err;
                status.AddThemeColorOverride("font_color", Ui.Bad);
            }
        }
        var menu = new MenuButton { Text = "+ вставить", Flat = false, FocusMode = FocusModeEnum.None };
        var pm = menu.GetPopup();
        int i = 0;
        foreach (var v in fa.Variables) pm.AddItem(v, i++);
        foreach (var f in SafeExpression.FunctionNames) pm.AddItem(f + "(…)", i++);
        foreach (var op in new[] { " + ", " - ", " * ", " / ", " ^ ", "(", ")" }) pm.AddItem(op.Trim(), i++);
        var all = fa.Variables.Concat(SafeExpression.FunctionNames.Select(f => f + "(")).Concat(new[] { " + ", " - ", " * ", " / ", " ^ ", "(", ")" }).ToList();
        pm.IdPressed += id =>
        {
            le.InsertTextAtCaret(all[(int)id]);
            prop.SetValue(owner, le.Text);
            RaiseChanged(owner, prop.Name);
            Validate();
        };
        le.TextChanged += _ => Validate();
        help.AddChild(menu);
        help.AddChild(status);
        parent.AddChild(help);
        Validate();
    }

    private Control TagList(VBoxContainer parent, object owner, PropertyInfo prop, EdAttribute ed, List<string> list)
    {
        var row = Row(parent, ed);
        var le = new LineEdit { Text = string.Join(", ", list), PlaceholderText = "через запятую" }.Expand();
        le.TextChanged += t =>
        {
            list.Clear();
            list.AddRange(t.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));
            RaiseChanged(owner, prop.Name);
        };
        row.AddChild(le);
        return row;
    }

    private Control NumberList(VBoxContainer parent, object owner, PropertyInfo prop, EdAttribute ed, List<double> list)
    {
        var row = Row(parent, ed);
        var le = new LineEdit { Text = string.Join("; ", list.Select(x => x.ToString(CultureInfo.InvariantCulture))), PlaceholderText = "числа через ;" }.Expand();
        le.TextChanged += t =>
        {
            var parsed = new List<double>();
            foreach (var p in t.Split(';', ' '))
                if (double.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    parsed.Add(d);
            list.Clear();
            list.AddRange(parsed);
            RaiseChanged(owner, prop.Name);
        };
        row.AddChild(le);
        return row;
    }

    private Control RefList(VBoxContainer parent, object owner, PropertyInfo prop, EdAttribute ed, List<string> list, EdRefAttribute refAttr)
    {
        var row = Row(parent, ed);
        var col = Ui.VBox(3).Expand();
        row.AddChild(col);
        void Fill()
        {
            Ui.ClearChildren(col);
            for (int i = 0; i < list.Count; i++)
            {
                int idx = i;
                var h = Ui.HBox(4);
                if (refAttr.AllowTags && list[i].StartsWith("tag:", StringComparison.Ordinal))
                {
                    var le = new LineEdit { Text = list[i] }.Expand();
                    le.TextChanged += t =>
                    {
                        list[idx] = t;
                        RaiseChanged(owner, prop.Name);
                    };
                    h.AddChild(le);
                }
                else
                {
                    var ob = RefOption(refAttr, list[i], v =>
                    {
                        list[idx] = v;
                        RaiseChanged(owner, prop.Name);
                    }).Expand();
                    h.AddChild(ob);
                }
                h.AddChild(Ui.Button("✕", () =>
                {
                    list.RemoveAt(idx);
                    RaiseChanged(owner, prop.Name);
                    Fill();
                }, "Удалить"));
                col.AddChild(h);
            }
            var add = Ui.HBox(4);
            add.AddChild(Ui.Button("+ добавить", () =>
            {
                var first = _db?.Ids(refAttr.Category).FirstOrDefault(x => !list.Contains(x)) ?? "";
                list.Add(first);
                RaiseChanged(owner, prop.Name);
                Fill();
            }));
            if (refAttr.AllowTags)
                add.AddChild(Ui.Button("+ тег", () =>
                {
                    list.Add("tag:");
                    RaiseChanged(owner, prop.Name);
                    Fill();
                }, "Ссылка на тег вида tag:имя"));
            col.AddChild(add);
        }
        Fill();
        return row;
    }

    private Control DictEditor(VBoxContainer parent, object owner, PropertyInfo prop, EdAttribute ed, Dictionary<string, double> dict)
    {
        var keyRef = prop.GetCustomAttribute<EdKeyRefAttribute>();
        var row = Row(parent, ed);
        var col = Ui.VBox(3).Expand();
        row.AddChild(col);
        void Fill()
        {
            Ui.ClearChildren(col);
            foreach (var key in dict.Keys.ToList())
            {
                string k = key;
                var h = Ui.HBox(4);
                if (keyRef != null)
                {
                    var ob = RefOption(new EdRefAttribute(keyRef.Category) { AllowEmpty = false }, k, nk =>
                    {
                        if (nk == k || dict.ContainsKey(nk)) return;
                        var v = dict[k];
                        dict.Remove(k);
                        dict[nk] = v;
                        RaiseChanged(owner, prop.Name);
                        Fill();
                    }).Expand();
                    h.AddChild(ob);
                }
                else
                {
                    h.AddChild(Ui.Label(k).Expand());
                }
                var sb = new SpinBox { MinValue = -1e9, MaxValue = 1e9, Step = 0.01, Value = dict[k], AllowGreater = true, AllowLesser = true, CustomMinimumSize = new Vector2(100, 0) };
                var sd = keyRef?.Category == DefCategory.Stat ? _db?.Stat(k) : null;
                if (sd != null)
                {
                    sb.MinValue = sd.Min;
                    sb.MaxValue = sd.Max;
                    sb.AllowGreater = false;
                    sb.AllowLesser = false;
                }
                if (keyRef?.Category == DefCategory.DamageType || keyRef?.Category == DefCategory.Status)
                {
                    sb.MinValue = -1;
                    sb.MaxValue = 1;
                    sb.AllowGreater = false;
                    sb.AllowLesser = false;
                }
                sb.ValueChanged += v =>
                {
                    dict[k] = v;
                    RaiseChanged(owner, prop.Name);
                };
                h.AddChild(sb);
                h.AddChild(Ui.Button("✕", () =>
                {
                    dict.Remove(k);
                    RaiseChanged(owner, prop.Name);
                    Fill();
                }));
                col.AddChild(h);
            }
            col.AddChild(Ui.Button("+ добавить", () =>
            {
                string? nk = keyRef != null ? _db?.Ids(keyRef.Category).FirstOrDefault(x => !dict.ContainsKey(x)) : "key" + dict.Count;
                if (nk == null) return;
                double def = keyRef?.Category == DefCategory.Stat ? _db?.StatDefault(nk) ?? 0 : 0;
                dict[nk] = def;
                RaiseChanged(owner, prop.Name);
                Fill();
            }));
        }
        Fill();
        return row;
    }

    private static string ItemCaption(object item, int index)
    {
        string s = item switch
        {
            SkillEffectDef e => e.Describe(),
            StatusLevelDef l => $"{(string.IsNullOrEmpty(l.Name) ? "уровень" : l.Name)} (порог {l.Threshold:0.##})",
            SkillStageDef st => $"{(string.IsNullOrEmpty(st.Name) ? "этап" : st.Name)}: {Names.Of(st.Area.Shape)}{(st.Delay > 0 ? $", +{st.Delay:0.##} с" : "")}",
            StatModDef m => $"{m.Stat} {m.Add:+0.##;-0.##;0} / {m.Mul:+0%;-0%;0%}",
            ResourceCostDef rc => $"{rc.Resource}: {rc.Amount:0.#}",
            UnitResourceDef ur => $"{ur.Resource}: {ur.Start:0}/{ur.Max:0} (+{ur.Regen:0.##}/с)",
            ConditionDef c => c.Describe(),
            WaveDef w => $"{w.Name} @ {w.Time:0} с",
            TriggerDef t => $"{t.Name}: {t.Condition.Describe()} → {string.Join("; ", t.Actions.Select(a => a.Describe()))}",
            TriggerActionDef a => a.Describe(),
            AnimationMapDef am => $"{Names.Of(am.Role)} → {(string.IsNullOrEmpty(am.Clip) ? "(процедурно)" : am.Clip)}",
            AiRuleDef r => $"{r.Name}: {r.When.Describe()} → {string.Join("; ", r.Actions.Select(a => a.Describe()))}",
            BossPhaseDef bp => $"{bp.Name}: {bp.When.Describe()}",
            SectorRule sr => $"{Names.Of(sr.Sector)}: неожиданность {sr.SurpriseChance:P0}, заметить {sr.NoticeChance:P0}",
            ScenarioSideDef sd => $"{sd.Faction}: {Names.Of(sd.Controller)}",
            // Part 3: modifiers, conditions, series costs, scenario passives — readable lines.
            ModifierDef md => md.Describe(),
            UnitConditionDef uc => uc.Describe(),
            StageCostDef sc => SkillText.Cost(sc),
            ScenarioPassiveDef sp => sp.ToString(),
            PassiveOverrideDef po => po.ToString(),
            _ => item.GetType().GetProperty("Name")?.GetValue(item) as string ?? "",
        };
        return $"{index + 1}. {(string.IsNullOrEmpty(s) ? item.GetType().Name : s)}";
    }

    private Control ObjectList(VBoxContainer parent, object owner, PropertyInfo prop, EdAttribute ed, IList list, Type itemType)
    {
        var section = Section(parent, $"{ed.Label} ({list.Count})", 1, owner.GetType().Name + "." + prop.Name + "#" + owner.GetHashCode());
        if (!string.IsNullOrEmpty(ed.Tip)) section.AddChild(Ui.Label(ed.Tip, 12, Ui.TextDim, wrap: true));
        var col = Ui.VBox(6);
        section.AddChild(col);
        void Fill()
        {
            Ui.ClearChildren(col);
            for (int i = 0; i < list.Count; i++)
            {
                int idx = i;
                var item = list[i]!;
                var box = Ui.VBox(3);
                var head = Ui.HBox(4);
                var caption = Ui.Label(ItemCaption(item, i), 14, Ui.Text).Expand();
                head.AddChild(caption);
                head.AddChild(Ui.Button("↑", () => Move(idx, -1), "Выше (раньше)"));
                head.AddChild(Ui.Button("↓", () => Move(idx, 1), "Ниже (позже)"));
                head.AddChild(Ui.Button("❐", () =>
                {
                    list.Insert(idx + 1, ContentJson.Clone(item));
                    RaiseChanged(owner, prop.Name);
                    Fill();
                }, "Дублировать"));
                head.AddChild(Ui.Button("✕", () =>
                {
                    list.RemoveAt(idx);
                    RaiseChanged(owner, prop.Name);
                    Fill();
                }, "Удалить"));
                box.AddChild(head);
                var sub = new PropertyGrid { LabelWidth = Math.Max(140, LabelWidth - 16), ReadOnly = ReadOnly };
                foreach (var kv in Choices) sub.Choices[kv.Key] = kv.Value;
                sub.SetTarget(item, _db, _depth + 1);
                sub.Changed += () =>
                {
                    caption.Text = ItemCaption(item, idx);
                    Changed?.Invoke();
                };
                sub.PropertyChanged += (o, p) => PropertyChanged?.Invoke(o, p);
                var pad = new MarginContainer();
                pad.AddThemeConstantOverride("margin_left", 10);
                pad.AddChild(sub);
                box.AddChild(pad);
                col.AddChild(Ui.PanelBox(box, new Color(0.12f, 0.13f, 0.15f, 0.9f), 6));
            }
            var catalog = OperationCatalog.For(itemType);
            if (catalog == null)
            {
                col.AddChild(Ui.Button("+ добавить", () =>
                {
                    list.Add(Activator.CreateInstance(itemType)!);
                    RaiseChanged(owner, prop.Name);
                    Fill();
                }));
            }
            else
            {
                // The catalog of ready operations / conditions (docs/DECISIONS_2.md §33), grouped.
                col.AddChild(CatalogButton(catalog, created =>
                {
                    list.Add(created);
                    RaiseChanged(owner, prop.Name);
                    Fill();
                }));
            }
        }
        void Move(int idx, int d)
        {
            int j = idx + d;
            if (j < 0 || j >= list.Count) return;
            (list[idx], list[j]) = (list[j], list[idx]);
            RaiseChanged(owner, prop.Name);
            Fill();
        }
        Fill();
        return section.GetParent().GetParent() as Control ?? section;
    }

    /// <summary>«+ из каталога ▾»: a menu of catalog entries by group; the chosen one is created with its defaults.</summary>
    public static MenuButton CatalogButton(IReadOnlyList<CatalogEntry> catalog, Action<object> onCreated)
    {
        var mb = new MenuButton { Text = "+ из каталога ▾", Flat = false, TooltipText = "Готовые операции и условия — без программирования" };
        var root = mb.GetPopup();
        int id = 0;
        var byId = new Dictionary<int, CatalogEntry>();
        foreach (var group in catalog.GroupBy(e => e.Group))
        {
            var sub = new PopupMenu { Name = "g" + byId.Count };
            foreach (var e in group)
            {
                sub.AddItem(e.Name, id);
                if (e.Description.Length > 0) sub.SetItemTooltip(sub.ItemCount - 1, e.Description);
                byId[id++] = e;
            }
            sub.IdPressed += i => onCreated(byId[(int)i].Create());
            root.AddChild(sub);
            root.AddSubmenuNodeItem(group.Key, sub);
        }
        return mb;
    }

    private void UpdateVisibility()
    {
        foreach (var (prop, owner, row) in _conditional)
            if (IsInstanceValid(row))
                row.Visible = EdUtil.IsShown(owner, prop);
    }
}
