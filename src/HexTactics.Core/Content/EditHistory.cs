using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace HexTactics.Core.Content;

/// <summary>One object of an edit: its JSON before and after (null — it did not exist), its place in the list before and after.</summary>
public sealed record EditChange(DefCategory Category, string Id, string Name, string? Before, string? After, int IndexBefore, int IndexAfter);

/// <summary>One operation of the history: what changed, when, under which name.</summary>
public sealed class EditEntry
{
    public int Number { get; init; }
    public string Label { get; init; } = "";
    public DateTime Time { get; init; }
    public List<EditChange> Changes { get; } = new();
    public string? RulesBefore { get; init; }
    public string? RulesAfter { get; init; }
    public bool Undone { get; set; }

    /// <summary>«Изменено: Волк-вожак (юнит)» or the explicit name of a compound operation.</summary>
    public string Describe()
    {
        var parts = Changes.Select(c => (c.Before == null ? "+" : c.After == null ? "−" : "") + $"«{c.Name}» ({ContentValidator.CatName(c.Category)})").ToList();
        if (RulesBefore != null) parts.Add("правила боя");
        string what = parts.Count <= 3 ? string.Join(", ", parts) : string.Join(", ", parts.Take(3)) + $" и ещё {parts.Count - 3}";
        return Label != "" ? $"{Label}: {what}" : what;
    }
}

/// <summary>
/// Undo and redo of the editor (docs/DECISIONS_4.md §69): the JSON of every object as of the last commit is the
/// baseline; a commit compares the touched objects (or, when the edit did not say which, all of them) with it and
/// stores their before/after as one entry. A compound operation (a mass change, a fit to a level, a drawn route)
/// is one commit — one entry. Undo puts the «before» back, redo the «after»; the objects are new instances, so
/// the open forms are rebuilt by the caller.
/// </summary>
public sealed class EditHistory
{
    private readonly Dictionary<(DefCategory, string), string> _baseline = new();
    private readonly Dictionary<(DefCategory, string), int> _index = new();
    private string _rules = "";
    private readonly HashSet<(DefCategory, string)> _touched = new();
    private bool _scanAll;
    private readonly List<EditEntry> _undo = new();
    private readonly List<EditEntry> _redo = new();
    private int _number;

    public EditHistory(ContentDatabase db, int limit = 200)
    {
        Db = db;
        Limit = limit;
        Rebase();
    }

    public ContentDatabase Db { get; private set; }
    public int Limit { get; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool Pending => _scanAll || _touched.Count > 0;
    /// <summary>Every operation, oldest first (undone ones are marked).</summary>
    public List<EditEntry> Log { get; } = new();
    public IReadOnlyList<EditEntry> UndoStack => _undo;
    public event Action<EditEntry>? Committed;

    private static string Json(Def d) => JsonSerializer.Serialize(d, d.GetType(), ContentJson.Options);
    private static string Json(RulesDef r) => JsonSerializer.Serialize(r, ContentJson.Options);

    /// <summary>The content was replaced (reverted, restored, reloaded): a new baseline, the stacks are cleared.</summary>
    public void Reset(ContentDatabase db)
    {
        Db = db;
        _undo.Clear();
        _redo.Clear();
        Rebase();
    }

    private void Rebase()
    {
        _baseline.Clear();
        _index.Clear();
        foreach (var d in Db.AllDefs()) _baseline[(d.Category, d.Id)] = Json(d);
        foreach (DefCategory c in Enum.GetValues<DefCategory>()) Reindex(c);
        _rules = Json(Db.Rules);
        _touched.Clear();
        _scanAll = false;
    }

    /// <summary>An object was edited (cheap: only it is compared at the commit).</summary>
    public void Touch(Def d) => _touched.Add((d.Category, d.Id));

    /// <summary>Something changed, the caller does not say what: the commit compares everything.</summary>
    public void TouchAll() => _scanAll = true;

    /// <summary>Stores the changes since the last commit as one entry; null — nothing changed.</summary>
    public EditEntry? Commit(string label = "")
    {
        if (!Pending) return null;
        var keys = _scanAll
            ? _baseline.Keys.Concat(Db.AllDefs().Select(d => (d.Category, d.Id))).Distinct().ToList()
            : _touched.ToList();
        _touched.Clear();
        _scanAll = false;
        var changes = new List<EditChange>();
        foreach (var (cat, id) in keys)
        {
            var cur = Db.Table(cat).Get(id);
            string? after = cur == null ? null : Json(cur);
            _baseline.TryGetValue((cat, id), out var before);
            if (before == after) continue;
            string name = cur?.DisplayName ?? (before != null ? NameOf(cat, before) : id);
            changes.Add(new EditChange(cat, id, name, before, after, _index.GetValueOrDefault((cat, id), -1), cur == null ? -1 : Db.Table(cat).IndexOf(id)));
            if (after == null) _baseline.Remove((cat, id));
            else _baseline[(cat, id)] = after;
        }
        foreach (var c in changes.Select(c => c.Category).Distinct()) Reindex(c);
        string rules = Json(Db.Rules);
        string? rulesBefore = rules != _rules ? _rules : null;
        if (changes.Count == 0 && rulesBefore == null) return null;
        var e = new EditEntry { Number = ++_number, Label = label, Time = DateTime.Now, RulesBefore = rulesBefore, RulesAfter = rulesBefore != null ? rules : null };
        e.Changes.AddRange(changes.OrderBy(c => c.Category).ThenBy(c => c.Id, StringComparer.Ordinal));
        _rules = rules;
        _undo.Add(e);
        if (_undo.Count > Limit) _undo.RemoveAt(0);
        foreach (var r in _redo) Log.Remove(r);
        _redo.Clear();
        Log.Add(e);
        if (Log.Count > Limit * 2) Log.RemoveAt(0);
        Committed?.Invoke(e);
        return e;
    }

    private void Reindex(DefCategory c)
    {
        if (!Db.HasTable(c)) return;
        foreach (var k in _index.Keys.Where(k => k.Item1 == c).ToList()) _index.Remove(k);
        var items = Db.Table(c).Items;
        for (int i = 0; i < items.Count; i++) _index[(c, items[i].Id)] = i;
    }

    private string NameOf(DefCategory cat, string json)
    {
        try
        {
            return (JsonSerializer.Deserialize(json, ContentDatabase.TypeOf(cat), ContentJson.Options) as Def)?.DisplayName ?? "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>Undoes the last operation (pending edits are committed first, so they are what is undone).</summary>
    public EditEntry? Undo()
    {
        Commit();
        if (_undo.Count == 0) return null;
        var e = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Apply(e, false);
        e.Undone = true;
        _redo.Add(e);
        return e;
    }

    public EditEntry? Redo()
    {
        Commit();
        if (_redo.Count == 0) return null;
        var e = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Apply(e, true);
        e.Undone = false;
        _undo.Add(e);
        return e;
    }

    /// <summary>Undoes every operation after the given one (the log's «вернуть к этой точке»).</summary>
    public int UndoTo(EditEntry entry)
    {
        int n = 0;
        while (_undo.Count > 0 && _undo[^1] != entry && _undo.Contains(entry))
        {
            Undo();
            n++;
        }
        return n;
    }

    private void Apply(EditEntry e, bool forward)
    {
        // Removals first, then additions and replacements at their places (the order of the lists is kept).
        foreach (var c in e.Changes)
            if ((forward ? c.After : c.Before) == null)
            {
                Db.Table(c.Category).Remove(c.Id);
                _baseline.Remove((c.Category, c.Id));
            }
        foreach (var c in e.Changes.OrderBy(c => forward ? c.IndexAfter : c.IndexBefore))
        {
            var json = forward ? c.After : c.Before;
            if (json == null) continue;
            var def = (Def)JsonSerializer.Deserialize(json, ContentDatabase.TypeOf(c.Category), ContentJson.Options)!;
            var table = Db.Table(c.Category);
            if (table.Contains(c.Id)) table.Replace(def);
            else table.Insert(forward ? c.IndexAfter : c.IndexBefore < 0 ? table.Count : c.IndexBefore, def);
            _baseline[(c.Category, c.Id)] = json;
        }
        foreach (var cat in e.Changes.Select(c => c.Category).Distinct()) Reindex(cat);
        var rules = forward ? e.RulesAfter : e.RulesBefore;
        if (rules != null)
        {
            Db.Rules = JsonSerializer.Deserialize<RulesDef>(rules, ContentJson.Options)!;
            _rules = rules;
        }
    }
}
