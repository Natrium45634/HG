using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using HexTactics.Core.Content;

namespace HexTactics.Game.App;

/// <summary>
/// Owns the published content and the editor draft.
/// Built-in pack: res://content (read-only, inside the build).
/// User pack: &lt;user data&gt;/content (written by the editor; preferred when present).
/// Backups: &lt;user data&gt;/content/backups/&lt;stamp&gt;/…, editor autosave: &lt;user data&gt;/autosave/.
/// </summary>
public sealed class ContentManager
{
    public const string BuiltInRoot = "res://content";

    public ContentManager()
    {
        UserRoot = ProjectSettings.GlobalizePath("user://content");
        AutosaveRoot = ProjectSettings.GlobalizePath("user://autosave");
    }

    public string UserRoot { get; }
    public string AutosaveRoot { get; }

    public ContentDatabase Published { get; private set; } = new();
    public List<ContentIssue> LoadIssues { get; private set; } = new();
    public bool UsingUserContent { get; private set; }

    /// <summary>Editor working copy. Null until the editor is opened.</summary>
    public ContentDatabase? Draft { get; private set; }
    public bool DraftDirty { get; set; }

    public int ErrorCount => LoadIssues.Count(i => i.Severity == IssueSeverity.Error);

    public string SourceDescription => UsingUserContent ? $"пользовательский пакет ({UserRoot})" : "встроенный пакет";

    public void Load()
    {
        IContentStorage storage;
        if (File.Exists(Path.Combine(UserRoot, ContentIO.RulesFile)))
        {
            storage = new FileSystemStorage(UserRoot);
            UsingUserContent = true;
        }
        else
        {
            storage = new ResStorage(BuiltInRoot);
            UsingUserContent = false;
        }
        var res = ContentIO.Load(storage);
        Published = res.Db;
        LoadIssues = res.Issues;
        foreach (var i in LoadIssues.Where(i => i.Severity == IssueSeverity.Error)) GD.PushWarning(i.ToString());
    }

    public static ContentLoadResult LoadBuiltIn() => ContentIO.Load(new ResStorage(BuiltInRoot));

    public ContentDatabase BeginEdit()
    {
        if (Draft == null)
        {
            Draft = TryLoadAutosave() ?? Published.Clone();
            DraftDirty = false;
        }
        return Draft;
    }

    public bool HasAutosave => File.Exists(Path.Combine(AutosaveRoot, ContentIO.RulesFile));

    private ContentDatabase? TryLoadAutosave() => null; // the editor asks explicitly (see RestoreAutosave)

    public ContentLoadResult? RestoreAutosave()
    {
        if (!HasAutosave) return null;
        var res = ContentIO.Load(new FileSystemStorage(AutosaveRoot));
        Draft = res.Db;
        DraftDirty = true;
        return res;
    }

    public void DiscardDraft()
    {
        Draft = Published.Clone();
        ContentValidator.Validate(Draft);
        DraftDirty = false;
        ClearAutosave();
    }

    public void Autosave()
    {
        if (Draft == null || !DraftDirty) return;
        try
        {
            ContentIO.Save(Draft, new FileSystemStorage(AutosaveRoot));
        }
        catch (Exception e)
        {
            GD.PushWarning("Автосохранение не удалось: " + e.Message);
        }
    }

    public void ClearAutosave()
    {
        try
        {
            if (Directory.Exists(AutosaveRoot)) Directory.Delete(AutosaveRoot, true);
        }
        catch (Exception e)
        {
            GD.PushWarning(e.Message);
        }
    }

    /// <summary>Publishes the draft: backup of the previous user pack, then write. Returns written files.</summary>
    public List<string> PublishDraft()
    {
        if (Draft == null) return new List<string>();
        var storage = new FileSystemStorage(UserRoot);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var written = ContentIO.Save(Draft, storage, UsingUserContent ? stamp : null);
        Published = Draft.Clone();
        LoadIssues = ContentValidator.Validate(Published);
        UsingUserContent = true;
        DraftDirty = false;
        ClearAutosave();
        return written;
    }

    /// <summary>Removes the user pack (after a backup copy) so the built-in pack is used again.</summary>
    public string ResetToBuiltIn()
    {
        if (!Directory.Exists(UserRoot)) return "";
        string backup = ProjectSettings.GlobalizePath("user://content_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.Move(UserRoot, backup);
        Draft = null;
        Load();
        return backup;
    }

    public void OpenUserFolder()
    {
        Directory.CreateDirectory(UserRoot);
        OS.ShellOpen(UserRoot);
    }
}
