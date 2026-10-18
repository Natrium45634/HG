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
    }

    public ContentDatabase Db { get; private set; }
    public List<ContentIssue> Issues { get; private set; }
    public bool Dirty { get; private set; }
    private bool _validatePending;

    public event Action? Validated;
    public event Action? DirtyChanged;
    public event Action<DefCategory, string>? NavigateRequested;
    public event Action? ContentReplaced;

    public int Errors => Issues.Count(i => i.Severity == IssueSeverity.Error);
    public int Warnings => Issues.Count(i => i.Severity == IssueSeverity.Warning);

    public void MarkDirty()
    {
        bool was = Dirty;
        Dirty = true;
        _validatePending = true;
        App.Main.Instance.Content.DraftDirty = true;
        if (!was) DirtyChanged?.Invoke();
    }

    public void ClearDirty()
    {
        Dirty = false;
        DirtyChanged?.Invoke();
    }

    /// <summary>Debounced validation: call every frame; validates when something changed.</summary>
    public void Tick()
    {
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
        Validate();
        ContentReplaced?.Invoke();
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
