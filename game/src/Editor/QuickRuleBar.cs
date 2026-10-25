using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// «Быстрый ввод» (docs/DECISIONS_5.md §77): a rule typed in words becomes modifiers linked to what the draft already
/// has. While typing, the word under the caret is looked up among elements, damage types, stats, statuses, resources,
/// categories of creatures…, new draft objects included; the preview shows what each word was linked to, and a word
/// that names nothing yet can be created as an element, a damage type, a stat… with one click.
/// </summary>
public partial class QuickRuleBar : VBoxContainer
{
    private readonly EditorContext _ctx;
    private readonly Action<List<ModifierDef>, string, string> _onAdd;
    private readonly Action<PassiveDef>? _onAttach;
    private readonly bool _withName;
    private readonly string _addText;

    private LineEdit _text = null!;
    private LineEdit? _name;
    private Button _add = null!;
    private HFlowContainer _suggest = null!;
    private VBoxContainer _preview = null!;
    private List<QuickRuleItem> _items = new();

    private static readonly DefCategory[] Suggested =
    {
        DefCategory.Element, DefCategory.DamageType, DefCategory.Stat, DefCategory.Status, DefCategory.Resource, DefCategory.Terrain,
        DefCategory.Origin, DefCategory.Unit, DefCategory.Skill,
    };

    /// <param name="onAdd">Adds the parsed modifiers (the text and the name typed for a new passive are passed).</param>
    /// <param name="onAttach">Attaches an existing passive found by the text (categories and units); null — not offered.</param>
    /// <param name="withName">A field for the name of the new passive.</param>
    public QuickRuleBar(EditorContext ctx, Action<List<ModifierDef>, string, string> onAdd, Action<PassiveDef>? onAttach = null, bool withName = false,
        string addText = "Добавить")
    {
        _ctx = ctx;
        _onAdd = onAdd;
        _onAttach = onAttach;
        _withName = withName;
        _addText = addText;
        Name = "QuickRuleBar";
    }

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 3);
        var head = Ui.HBox(6);
        head.AddChild(Ui.Label("⚡ Быстрый ввод", 14, Ui.Accent));
        head.AddChild(Ui.Label("словами: «+25% входящего урона от святой магии; броня +10; иммунитет к яду» — уже добавленное подставится само", 11, Ui.TextDim, wrap: true).Expand());
        AddChild(head);
        var row = Ui.HBox(4);
        _text = new LineEdit { Name = "QuickRuleText", PlaceholderText = "что даёт: «+15% урона по нежити», «уклонение +10%», «−30% урона от огня»…", ClearButtonEnabled = true }.Expand();
        _text.TextChanged += _ => Reparse();
        _text.TextSubmitted += _ => Add();
        row.AddChild(_text);
        if (_withName)
        {
            _name = new LineEdit { Name = "QuickRuleName", PlaceholderText = "название (необязательно)", CustomMinimumSize = new Vector2(170, 0) };
            row.AddChild(_name);
        }
        _add = Ui.Button(_addText, Add, "Добавить распознанные правила (Enter)");
        _add.Name = "QuickRuleAdd";
        _add.Disabled = true;
        row.AddChild(_add);
        AddChild(row);
        _suggest = new HFlowContainer { Name = "QuickRuleSuggest" };
        _suggest.AddThemeConstantOverride("h_separation", 4);
        AddChild(_suggest);
        _preview = Ui.VBox(1);
        _preview.Name = "QuickRulePreview";
        AddChild(_preview);
        AddChild(Ui.Sep());
        // Screenshots: --quick="text" types it in.
        if (App.Main.Instance.Args.TryGetValue("quick", out var q)) Callable.From(() => Text = q).CallDeferred();
    }

    public string Text
    {
        get => _text.Text;
        set
        {
            _text.Text = value;
            _text.CaretColumn = value.Length;
            Reparse();
        }
    }

    public IReadOnlyList<QuickRuleItem> Items => _items;

    /// <summary>The word being typed (letters before the caret).</summary>
    private (int From, string Word) CurrentWord()
    {
        var t = _text.Text;
        int end = Math.Clamp(_text.CaretColumn, 0, t.Length);
        int start = end;
        while (start > 0 && char.IsLetter(t[start - 1])) start--;
        return (start, t[start..end]);
    }

    private void Reparse()
    {
        var text = _text.Text.Trim();
        _items = text.Length == 0 ? new() : QuickRuleParser.Parse(_ctx.Db, text, _ctx.Search);
        FillSuggestions();
        FillPreview();
        _add.Disabled = _items.Count == 0 || _items.Any(i => !i.Ok);
    }

    private void FillSuggestions()
    {
        Ui.ClearChildren(_suggest);
        var (from, word) = CurrentWord();
        if (word.Length >= 2)
        {
            foreach (var h in _ctx.Search.Find(word, e => e.Category is { } c ? Suggested.Contains(c) : e.Value.StartsWith(DamageTaxonomy.CategoryPrefix, StringComparison.Ordinal), 6, 40))
            {
                var e = h.Entry;
                var b = Ui.Button($"{e.Name} · {e.Kind}{(e.State == SearchState.New ? " · новое" : "")}", () => Insert(from, word, e), "Подставить: " + e.Label);
                b.Name = "Suggest_" + (e.Def?.Id ?? e.Value);
                if (e.State == SearchState.New) b.AddThemeColorOverride("font_color", Ui.Accent);
                _suggest.AddChild(b);
            }
        }
        // Categories and units: a ready passive that says the same.
        if (_onAttach != null && _text.Text.Trim().Length >= 3)
            foreach (var h in _ctx.Search.Find(_text.Text.Trim(), e => e.Category == DefCategory.Passive, 3, 62))
            {
                var p = (PassiveDef)h.Entry.Def!;
                var b = Ui.Button($"⤷ подключить готовый «{p.DisplayName}»", () =>
                {
                    _onAttach(p);
                    _text.Text = "";
                    Reparse();
                }, string.Join("; ", PassiveText.Lines(p, _ctx.Db)));
                b.Name = "Attach_" + p.Id;
                _suggest.AddChild(b);
            }
    }

    private void Insert(int from, string word, SearchEntry e)
    {
        var t = _text.Text;
        string name = e.Name.ToLowerInvariant();
        _text.Text = t[..from] + name + t[(from + word.Length)..];
        _text.CaretColumn = from + name.Length;
        _text.GrabFocus();
        Reparse();
    }

    private void FillPreview()
    {
        Ui.ClearChildren(_preview);
        foreach (var it in _items)
        {
            if (it.Modifier != null && it.Error == null)
            {
                var l = Ui.Label((it.Ok ? "✓ " : "… ") + it.Modifier.Describe(_ctx.Db), 13, it.Ok ? Ui.Good : Ui.Warn, wrap: true);
                l.Name = "QuickRuleOk";
                _preview.AddChild(l);
            }
            else if (it.Error != null)
            {
                _preview.AddChild(Ui.Label($"✗ «{it.Source}»: {it.Error}", 12, Ui.Bad, wrap: true));
            }
            if (it.Links.Count > 0)
            {
                var links = Ui.Label("    ↳ " + string.Join(" · ", it.Links.Select(l => $"«{l.Text}» → {l.Entry.Name} [{l.Entry.Def?.Id ?? l.Entry.Value}], {l.Role}{SearchEntry.StateText(l.Entry.State)}")),
                    11, it.Links.Any(l => l.Entry.State == SearchState.New) ? Ui.Accent : Ui.TextDim, wrap: true);
                links.Name = "QuickRuleLinks";
                _preview.AddChild(links);
            }
            foreach (var m in it.Missing)
            {
                var h = new HFlowContainer();
                h.AddThemeConstantOverride("h_separation", 4);
                h.AddChild(Ui.Label($"    ? «{m.Term}» — такого ещё нет, создать:", 12, Ui.Warn));
                foreach (var c in m.CreateAs)
                {
                    var cat = c;
                    var b = Ui.Button($"＋ {ContentSearch.KindOf(cat)} «{m.SuggestedName}»", () => CreateMissing(cat, m.SuggestedName, false), "Создать в черновике и связать");
                    b.Name = "QuickCreate_" + cat;
                    h.AddChild(b);
                }
                if (m.CreateAs.Contains(DefCategory.Element) && m.CreateAs.Contains(DefCategory.DamageType))
                {
                    var both = Ui.Button($"＋ элемент и тип урона «{m.SuggestedName}»", () => CreateMissing(DefCategory.DamageType, m.SuggestedName, true),
                        "Элемент и тип урона этого элемента, связанные друг с другом");
                    both.Name = "QuickCreate_Both";
                    h.AddChild(both);
                }
                _preview.AddChild(h);
            }
            foreach (var n in it.Notes) _preview.AddChild(Ui.Label("    ℹ " + n, 11, Ui.TextDim, wrap: true));
        }
    }

    private void CreateMissing(DefCategory c, string name, bool withElement)
    {
        var d = _ctx.CreateByName(c, name, withElement);
        _ctx.Commit($"Создан {ContentSearch.KindOf(c)} «{d.DisplayName}»");
        Reparse();
    }

    private void Add()
    {
        if (_add.Disabled) return;
        var mods = _items.Select(i => i.Modifier!).ToList();
        string text = _text.Text.Trim();
        string name = _name?.Text.Trim() ?? "";
        _text.Text = "";
        if (_name != null) _name.Text = "";
        _items.Clear();
        _onAdd(mods, text, name);
    }

    /// <summary>A name for a passive made from the text: «+25% урона от святой магии» → «+25% урона от святой магии».</summary>
    public static string NameFrom(string text)
    {
        var t = text.Trim();
        if (t.Length > 48) t = t[..48].TrimEnd() + "…";
        return t.Length == 0 ? "Новое пассивное умение" : char.ToUpperInvariant(t[0]) + t[1..];
    }
}
