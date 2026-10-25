using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;

namespace HexTactics.Game.UI;

/// <summary>
/// A drop-down whose list opens with a search field (docs/DECISIONS_5.md §77): the typed words find objects by name,
/// ID, synonym, tag and any word form («святой» → «Святость»), objects new in the draft are marked, and a missing one
/// can be created on the spot. The items stay those of an OptionButton, so the code that fills and reads it is the
/// same as for a plain drop-down.
/// </summary>
public partial class SearchableOption : OptionButton
{
    /// <summary>The searchable entry of an item (null — the item text is searched).</summary>
    public Func<int, SearchEntry?>? EntryOf { get; set; }
    /// <summary>Creates an object named by the query and returns the index of its new item (−1 — not created).</summary>
    public Func<string, int>? Create { get; set; }
    /// <summary>What «Создать» makes, for the button text («элемент», «характеристика»…).</summary>
    public string CreateKind { get; set; } = "";

    private PopupPanel? _popup;
    private LineEdit? _query;
    private ItemList? _list;
    private Label? _hint;
    private readonly List<int> _shown = new();

    public SearchableOption()
    {
        TooltipText = "Клик — список с поиском: название, ID, синоним, тег, любая форма слова";
    }

    public override void _GuiInput(InputEvent e)
    {
        // The native list never opens: the search list replaces it.
        if (!Disabled && e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            AcceptEvent();
            OpenSearch();
        }
    }

    /// <summary>Opens the search list (with a query typed in advance, for tests and the quick input).</summary>
    public void OpenSearch(string query = "")
    {
        _popup?.QueueFree();
        _popup = new PopupPanel { Name = "SearchPopup" };
        var box = Ui.VBox(4);
        _query = new LineEdit { Name = "SearchQuery", PlaceholderText = "Поиск: название, ID, синоним, тег…", ClearButtonEnabled = true, Text = query };
        _list = new ItemList { Name = "SearchList", CustomMinimumSize = new Vector2(Math.Max(Size.X, 420), 320), SizeFlagsVertical = SizeFlags.ExpandFill };
        _hint = Ui.Label("", 11, Ui.TextDim);
        box.AddChild(_query);
        box.AddChild(_list);
        box.AddChild(_hint);
        _popup.AddChild(box);
        AddChild(_popup);
        _query.TextChanged += Fill;
        _query.TextSubmitted += _ => ChooseRow(_list.GetSelectedItems().Length > 0 ? _list.GetSelectedItems()[0] : 0);
        _query.GuiInput += ev =>
        {
            if (ev is InputEventKey { Pressed: true } k && (k.Keycode == Key.Down || k.Keycode == Key.Up) && _list.ItemCount > 0)
            {
                int cur = _list.GetSelectedItems().Length > 0 ? _list.GetSelectedItems()[0] : -1;
                int next = Math.Clamp(cur + (k.Keycode == Key.Down ? 1 : -1), 0, _list.ItemCount - 1);
                _list.Select(next);
                _list.EnsureCurrentIsVisible();
                _query.AcceptEvent();
            }
        };
        _list.ItemClicked += (i, _, button) =>
        {
            if (button == (long)MouseButton.Left) ChooseRow((int)i);
        };
        _list.ItemActivated += i => ChooseRow((int)i);
        _popup.PopupHide += () =>
        {
            var p = _popup;
            _popup = null;
            p?.QueueFree();
        };
        Fill(query);
        var at = (Vector2I)GetScreenPosition() + new Vector2I(0, (int)Size.Y);
        _popup.Popup(new Rect2I(at, new Vector2I((int)Math.Max(Size.X, 420), 380)));
        _query.GrabFocus();
    }

    /// <summary>Rows of the list for the current query (best first; all items when it is empty).</summary>
    public List<string> Rows => _list == null ? new() : Enumerable.Range(0, _list.ItemCount).Select(i => _list.GetItemText(i)).ToList();

    private SearchEntry Entry(int i) => EntryOf?.Invoke(i) ?? ContentSearch.TextEntry(GetItemText(i), GetItemText(i));

    private void Fill(string q)
    {
        if (_list == null || _hint == null) return;
        _list.Clear();
        _shown.Clear();
        var hits = new List<(int Index, double Score)>();
        bool exact = false;
        string query = q.Trim();
        for (int i = 0; i < ItemCount; i++)
        {
            if (query.Length == 0)
            {
                hits.Add((i, 0));
                continue;
            }
            double s = ContentSearch.Score(Entry(i), query);
            if (s <= 0 && RuText.SwapLayout(query) is { } sw) s = ContentSearch.Score(Entry(i), sw) * 0.9;
            if (s > 0) hits.Add((i, s));
            exact |= s >= 100;
        }
        foreach (var (i, _) in query.Length == 0 ? hits : hits.OrderByDescending(h => h.Score).ThenBy(h => h.Index).ToList())
        {
            var e = EntryOf?.Invoke(i);
            string text = e != null ? e.Label : GetItemText(i);
            int row = _list.AddItem(text);
            _shown.Add(i);
            if (e?.State == SearchState.New) _list.SetItemCustomFgColor(row, Ui.Accent);
            else if (e?.State == SearchState.Incomplete) _list.SetItemCustomFgColor(row, Ui.Warn);
            else if (e?.State == SearchState.Changed) _list.SetItemCustomFgColor(row, new Color(0.62f, 0.8f, 1f));
            if (e != null && !string.IsNullOrEmpty(e.Detail)) _list.SetItemTooltip(row, e.Detail);
            if (i == Selected) _list.Select(row);
        }
        if (Create != null && query.Length > 0 && !exact)
        {
            int row = _list.AddItem($"＋ Создать «{query}»" + (CreateKind != "" ? $" — {CreateKind}" : "") + " в черновике");
            _list.SetItemCustomFgColor(row, Ui.Good);
            _shown.Add(-1);
        }
        if (_list.ItemCount > 0 && _list.GetSelectedItems().Length == 0) _list.Select(0);
        _hint.Text = query.Length == 0 ? $"{ItemCount} вариантов · ↑↓ и Enter, Esc — закрыть" : $"найдено {hits.Count} · жёлтым — новое в черновике, голубым — изменённое";
    }

    private void ChooseRow(int row)
    {
        if (row < 0 || row >= _shown.Count) return;
        int idx = _shown[row];
        string query = _query?.Text.Trim() ?? "";
        _popup?.Hide();
        if (idx < 0)
        {
            if (Create == null || query.Length == 0) return;
            idx = Create(query);
            if (idx < 0) return;
        }
        Pick(idx);
    }

    /// <summary>Selects the item as a click in the list does (the change handlers run).</summary>
    public void Pick(int idx)
    {
        if (idx < 0 || idx >= ItemCount) return;
        Select(idx);
        EmitSignal(OptionButton.SignalName.ItemSelected, (long)idx);
    }

    /// <summary>Runs the search and picks the best match or creates the object (tests and the quick input).</summary>
    public bool PickByQuery(string query, bool allowCreate = false)
    {
        OpenSearch(query);
        if (_shown.Count == 0) return false;
        if (_shown[0] < 0 && !allowCreate) return false;
        ChooseRow(0);
        return true;
    }
}
