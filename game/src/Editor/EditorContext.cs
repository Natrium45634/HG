using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>Shared state of the administrative editor: draft content, validation, dirty flag, navigation.</summary>
public sealed class EditorContext
{
    public EditorContext(ContentDatabase draft)
    {
        Db = draft;
        Issues = ContentValidator.Validate(Db);
        History = new EditHistory(Db);
    }

    /// <summary>Undo/redo and the log of the editor (docs/DECISIONS_4.md §69).</summary>
    public EditHistory History { get; private set; }
    /// <summary>The history changed (a commit, an undo, a redo): the buttons and the log refresh.</summary>
    public event Action? HistoryChanged;
    private ulong _commitAt;

    public ContentDatabase Db { get; private set; }
    public List<ContentIssue> Issues { get; private set; }

    private ContentSearch? _search;
    /// <summary>
    /// Search over the draft — new, changed and incomplete objects included (docs/DECISIONS_5.md §77); rebuilt on the
    /// first use after a change.
    /// </summary>
    public ContentSearch Search => _search ??= ContentSearch.Build(Db, App.Main.Instance.Content.Published);

    /// <summary>Creates an object by name in the draft (from a reference list or the quick input) and returns it.</summary>
    public Def CreateByName(DefCategory c, string name, bool withElement = false)
    {
        var d = QuickRuleParser.Create(Db, c, name, withElement);
        MarkDirty();
        return d;
    }
    public bool Dirty { get; private set; }
    private bool _validatePending;

    public event Action? Validated;
    public event Action? DirtyChanged;
    public event Action<DefCategory, string>? NavigateRequested;
    public event Action? ContentReplaced;

    public int Errors => Issues.Count(i => i.Severity == IssueSeverity.Error);
    public int Warnings => Issues.Count(i => i.Severity == IssueSeverity.Warning);

    /// <summary>Something changed, the caller does not say what (the history compares everything at the commit).</summary>
    public void MarkDirty()
    {
        History.TouchAll();
        SetDirty();
    }

    /// <summary>An object was edited (the history compares only it).</summary>
    public void MarkDirty(Def d)
    {
        History.Touch(d);
        SetDirty();
    }

    private void SetDirty()
    {
        _search = null;
        bool was = Dirty;
        Dirty = true;
        _validatePending = true;
        App.Main.Instance.Content.DraftDirty = true;
        // Edits that follow one another within 0.7 s are one operation of the history.
        _commitAt = Time.GetTicksMsec() + 700;
        if (!was) DirtyChanged?.Invoke();
    }

    /// <summary>Stores the edits since the last commit as one operation (a compound operation passes its name).</summary>
    public EditEntry? Commit(string label = "")
    {
        _commitAt = 0;
        var e = History.Commit(label);
        if (e != null)
        {
            EditLog.Append(e, "");
            HistoryChanged?.Invoke();
        }
        return e;
    }

    /// <summary>After several undo steps done directly on the history (the log's «вернуть к этой точке»).</summary>
    public void MarkDirtyAfterHistory()
    {
        _search = null;
        bool was = Dirty;
        Dirty = true;
        App.Main.Instance.Content.DraftDirty = true;
        if (!was) DirtyChanged?.Invoke();
        HistoryChanged?.Invoke();
    }

    public EditEntry? Undo() => Step(false);

    public EditEntry? Redo() => Step(true);

    private EditEntry? Step(bool redo)
    {
        _commitAt = 0;
        int before = History.Log.Count;
        var e = redo ? History.Redo() : History.Undo();
        _search = null;
        if (History.Log.Count > before) EditLog.Append(History.Log[^1], "");
        if (e == null) return null;
        EditLog.Append(e, redo ? "повторено" : "отменено");
        bool was = Dirty;
        Dirty = true;
        App.Main.Instance.Content.DraftDirty = true;
        if (!was) DirtyChanged?.Invoke();
        Validate();
        HistoryChanged?.Invoke();
        return e;
    }

    public void ClearDirty()
    {
        Dirty = false;
        DirtyChanged?.Invoke();
    }

    /// <summary>Debounced validation: call every frame; validates when something changed.</summary>
    public void Tick()
    {
        // Not in the middle of a gesture (a route drawn with the mouse is one operation).
        if (_commitAt > 0 && Time.GetTicksMsec() >= _commitAt && !Input.IsMouseButtonPressed(MouseButton.Left) && !Input.IsMouseButtonPressed(MouseButton.Right))
            Commit();
        if (!_validatePending) return;
        _validatePending = false;
        Validate();
    }

    public void Validate()
    {
        Issues = ContentValidator.Validate(Db);
        Validated?.Invoke();
    }

    public void Replace(ContentDatabase db)
    {
        Db = db;
        _search = null;
        _commitAt = 0;
        History.Reset(db);
        Validate();
        ContentReplaced?.Invoke();
        HistoryChanged?.Invoke();
    }

    public IEnumerable<ContentIssue> IssuesOf(Def d)
    {
        string src = $"{d.Category}:{d.Id}";
        return Issues.Where(i => i.Source == src);
    }

    public IssueSeverity? WorstOf(Def d)
    {
        IssueSeverity? worst = null;
        foreach (var i in IssuesOf(d))
            if (worst == null || i.Severity > worst)
                worst = i.Severity;
        return worst;
    }

    public void Navigate(DefCategory c, string id) => NavigateRequested?.Invoke(c, id);
}

/// <summary>Small dialog helpers (confirmation, prompts, file dialogs, info).</summary>
public static class Dialogs
{
    public static void Confirm(Node parent, string title, string text, Action onOk, string ok = "Да", string cancel = "Отмена")
    {
        var d = new ConfirmationDialog { Title = title, DialogText = text, OkButtonText = ok, CancelButtonText = cancel, DialogAutowrap = true };
        d.Theme = App.Main.Instance.Theme;
        parent.AddChild(d);
        d.Confirmed += () =>
        {
            onOk();
            d.QueueFree();
        };
        d.Canceled += () => d.QueueFree();
        d.PopupCentered(new Vector2I(560, 0));
    }

    /// <summary>Dialog with custom content; onOk returns false to keep the dialog open (validation failed).</summary>
    public static ConfirmationDialog Custom(Node parent, string title, Control content, string ok, Func<bool> onOk, Vector2I? size = null)
    {
        var d = new ConfirmationDialog { Title = title, OkButtonText = ok, CancelButtonText = "Отмена", Theme = App.Main.Instance.Theme };
        d.DialogHideOnOk = false;
        d.AddChild(content);
        parent.AddChild(d);
        d.Confirmed += () =>
        {
            if (!onOk()) return;
            d.Hide(); // before follow-up dialogs: only one exclusive child window at a time
            d.QueueFree();
        };
        d.Canceled += () => d.QueueFree();
        d.PopupCentered(size ?? new Vector2I(560, 0));
        return d;
    }

    public static void Info(Node parent, string title, string text)
    {
        var d = new AcceptDialog { Title = title, DialogText = text, DialogAutowrap = true };
        d.Theme = App.Main.Instance.Theme;
        parent.AddChild(d);
        d.Confirmed += () => d.QueueFree();
        d.Canceled += () => d.QueueFree();
        d.PopupCentered(new Vector2I(620, 0));
    }

    public static void Prompt(Node parent, string title, string label, string initial, Action<string> onOk)
    {
        var d = new ConfirmationDialog { Title = title, OkButtonText = "OK", CancelButtonText = "Отмена" };
        d.Theme = App.Main.Instance.Theme;
        var v = Ui.VBox(6, Ui.Label(label, 14, Ui.TextDim, wrap: true).MinSize(420));
        var le = new LineEdit { Text = initial };
        v.AddChild(le);
        d.AddChild(v);
        parent.AddChild(d);
        d.Confirmed += () =>
        {
            onOk(le.Text.Trim());
            d.QueueFree();
        };
        d.Canceled += () => d.QueueFree();
        d.PopupCentered(new Vector2I(460, 0));
        le.GrabFocus();
        le.SelectAll();
    }

    /// <summary>UI tests: answers file dialogs by title (null = show the dialog).</summary>
    public static Func<string, string?>? AutoPath { get; set; }

    public const string JsonFilter = "*.json ; JSON";
    public const string PackageFilter = "*.hexpack ; Пакет контента";

    public static void OpenFile(Node parent, string title, Action<string> onPath, params string[] filters) =>
        FileDialogFor(parent, title, FileDialog.FileModeEnum.OpenFile, "", onPath, filters.Length == 0 ? new[] { JsonFilter } : filters);

    public static void OpenDir(Node parent, string title, Action<string> onPath) =>
        FileDialogFor(parent, title, FileDialog.FileModeEnum.OpenDir, "", onPath, Array.Empty<string>());

    public static void SaveFile(Node parent, string title, string defaultName, Action<string> onPath, params string[] filters) =>
        FileDialogFor(parent, title, FileDialog.FileModeEnum.SaveFile, defaultName, onPath, filters.Length == 0 ? new[] { JsonFilter } : filters);

    private static void FileDialogFor(Node parent, string title, FileDialog.FileModeEnum mode, string defaultName, Action<string> onPath, string[] filters)
    {
        if (AutoPath?.Invoke(title) is { } auto)
        {
            onPath(auto);
            return;
        }
        var fd = new FileDialog
        {
            Title = title,
            FileMode = mode,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = filters,
            CurrentFile = defaultName,
            UseNativeDialog = true,
        };
        parent.AddChild(fd);
        if (mode == FileDialog.FileModeEnum.OpenDir)
            fd.DirSelected += p =>
            {
                onPath(p);
                fd.QueueFree();
            };
        else
            fd.FileSelected += p =>
            {
                onPath(p);
                fd.QueueFree();
            };
        fd.Canceled += () => fd.QueueFree();
        fd.PopupCentered(new Vector2I(820, 520));
    }
}

/// <summary>«Журнал последних изменений» on disk: one line per operation (editor_log.txt in the user data, the last 2000 lines).</summary>
public static class EditLog
{
    public static string PathOf => System.IO.Path.Combine(App.Main.Instance.Content.DataRoot, "editor_log.txt");

    public static void Append(EditEntry e, string what)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} #{e.Number} {(what != "" ? what + ": " : "")}{e.Describe()}";
            System.IO.File.AppendAllText(PathOf, line + "\n");
            var info = new System.IO.FileInfo(PathOf);
            if (info.Length > 400_000)
            {
                var lines = System.IO.File.ReadAllLines(PathOf);
                System.IO.File.WriteAllLines(PathOf, lines.Skip(Math.Max(0, lines.Length - 2000)));
            }
        }
        catch (Exception ex)
        {
            GD.PushWarning("Журнал изменений: " + ex.Message);
        }
    }

    public static List<string> Recent(int n)
    {
        try
        {
            return System.IO.File.Exists(PathOf) ? System.IO.File.ReadLines(PathOf).TakeLast(n).Reverse().ToList() : new List<string>();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }
}

/// <summary>A restore point: the whole draft at a moment (before a mass change), in its own folder.</summary>
public sealed record RestorePoint(string Dir, string Label, DateTime Time);

/// <summary>
/// Restore points (docs/DECISIONS_4.md §69): before mass changes of levels, categories or templates the draft is
/// saved to restore_points/ in the user data; restoring one is itself an operation of the history (it can be undone).
/// </summary>
public static class RestorePoints
{
    public static string Root => System.IO.Path.Combine(App.Main.Instance.Content.DataRoot, "restore_points");
    public const int Keep = 20;

    public static RestorePoint? Create(EditorContext ctx, string label)
    {
        try
        {
            var time = DateTime.Now;
            string dir = System.IO.Path.Combine(Root, time.ToString("yyyyMMdd_HHmmss_fff"));
            System.IO.Directory.CreateDirectory(dir);
            ContentIO.Save(ctx.Db, new FileSystemStorage(dir));
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "restore_point.txt"), label);
            foreach (var old in List().Skip(Keep)) System.IO.Directory.Delete(old.Dir, true);
            return new RestorePoint(dir, label, time);
        }
        catch (Exception e)
        {
            GD.PushWarning("Точка восстановления не создана: " + e.Message);
            return null;
        }
    }

    public static List<RestorePoint> List()
    {
        if (!System.IO.Directory.Exists(Root)) return new List<RestorePoint>();
        return System.IO.Directory.GetDirectories(Root)
            .Select(d => (Dir: d, Meta: System.IO.Path.Combine(d, "restore_point.txt")))
            .Where(x => System.IO.File.Exists(x.Meta))
            .Select(x => new RestorePoint(x.Dir, System.IO.File.ReadAllText(x.Meta), System.IO.Directory.GetCreationTime(x.Dir)))
            .OrderByDescending(p => System.IO.Path.GetFileName(p.Dir), StringComparer.Ordinal).ToList();
    }

    /// <summary>Puts the draft of the point back (as one operation of the history); null — done, else the reason.</summary>
    public static string? Restore(EditorContext ctx, RestorePoint p)
    {
        var res = ContentIO.Load(new FileSystemStorage(p.Dir, readOnly: true), validate: false);
        if (!res.Db.AllDefs().Any()) return "точка восстановления пуста или повреждена";
        ctx.Commit();
        foreach (var t in ctx.Db.Tables) t.Clear();
        foreach (var d in res.Db.AllDefs()) ctx.Db.Add(d);
        ctx.Db.Rules = res.Db.Rules;
        ctx.MarkDirty();
        ctx.Commit($"Возврат к точке «{p.Label}»");
        ctx.Validate();
        return null;
    }
}