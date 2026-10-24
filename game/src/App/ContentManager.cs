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
/// User pack: &lt;user data&gt;/content (written by the editor through <see cref="ContentLibrary"/>:
/// versions + history, journal, manifest, backups — docs/DECISIONS_2.md §31; preferred when present).
/// Editor autosave: &lt;user data&gt;/autosave/.
/// </summary>
public sealed class ContentManager
{
    public const string BuiltInRoot = "res://content";

    /// <param name="sandbox">Sub folder of user data for isolated runs (UI tests): the real user pack is never touched.</param>
    public ContentManager(string? sandbox = null)
    {
        string root = sandbox == null ? "user://" : $"user://{sandbox}/";
        UserRoot = ProjectSettings.GlobalizePath(root + "content");
        AutosaveRoot = ProjectSettings.GlobalizePath(root + "autosave");
        DataRoot = ProjectSettings.GlobalizePath(root);
    }

    /// <summary>The folder of user data (a sandbox in UI tests): the editor log, restore points.</summary>
    public string DataRoot { get; }

    public string UserRoot { get; }
    public string AutosaveRoot { get; }

    public ContentDatabase Published { get; private set; } = new();
    public List<ContentIssue> LoadIssues { get; private set; } = new();
    public bool UsingUserContent { get; private set; }

    /// <summary>Editor working copy. Null until the editor is opened.</summary>
    public ContentDatabase? Draft { get; private set; }
    public bool DraftDirty { get; set; }

    public int ErrorCount => LoadIssues.Count(i => i.Severity == IssueSeverity.Error);

    /// <summary>Library of the user pack (history, journal, manifest, backups).</summary>
    public ContentLibrary Library => new(new FileSystemStorage(UserRoot)) { BeforeWrite = FaultInjection };

    /// <summary>UI tests: called before every file write of a publication (throw to simulate a crash).</summary>
    public Action<string>? FaultInjection { get; set; }

    /// <summary>Set when the previous publication had crashed and the pack was rolled back at start.</summary>
    public string? RecoveryMessage { get; set; }

    /// <summary>Integrity of the user pack at the last load (null for the built-in pack).</summary>
    public IntegrityReport? Integrity { get; private set; }

    public string SourceDescription => UsingUserContent ? $"пользовательский пакет ({UserRoot})" : "встроенный пакет";

    public void Load()
    {
        IContentStorage storage;
        Integrity = null;
        if (File.Exists(Path.Combine(UserRoot, ContentLibrary.JournalFile)))
        {
            // An unfinished publication (crash, power loss): roll the pack back before reading it.
            try
            {
                var rec = Library.RecoverIfNeeded();
                if (rec.Message.Length > 0) RecoveryMessage = rec.Message;
            }
            catch (Exception e)
            {
                RecoveryMessage = "Не удалось восстановить пакет после сбоя: " + e.Message;
            }
        }
        if (File.Exists(Path.Combine(UserRoot, ContentIO.RulesFile)))
        {
            storage = new FileSystemStorage(UserRoot);
            UsingUserContent = true;
            Integrity = Library.CheckIntegrity();
        }
        else
        {
            storage = new ResStorage(BuiltInRoot);
            UsingUserContent = false;
        }
        var res = ContentIO.Load(storage);
        Published = res.Db;
        LoadIssues = res.Issues;
        // Resource files (models, textures, sounds): missing or damaged ones are reported, the game uses the base representation.
        Render.Assets.Reset();
        LoadIssues.AddRange(AssetValidator.CheckFiles(Published, Render.Assets.Read));
        foreach (var i in LoadIssues.Where(i => i.Severity == IssueSeverity.Error)) GD.PushWarning(i.ToString());
    }

    public static ContentLoadResult LoadBuiltIn() => ContentIO.Load(new ResStorage(BuiltInRoot));

    public ContentDatabase BeginEdit()
    {
        if (Draft == null)
        {
            Draft = Published.Clone();
            ContentValidator.Validate(Draft);
            DraftDirty = false;
        }
        return Draft;
    }

    public bool HasAutosave => File.Exists(Path.Combine(AutosaveRoot, ContentIO.RulesFile));

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

    /// <summary>
    /// Publishes the draft transactionally (docs/DECISIONS_2.md §31): versions of changed objects grow,
    /// previous versions go to the history, the pack is written with a journal, a backup and a manifest.
    /// A failure rolls the pack back immediately and is rethrown.
    /// </summary>
    public LibraryPublishResult PublishDraft()
    {
        if (Draft == null) return new LibraryPublishResult();
        var lib = Library;
        LibraryPublishResult result;
        try
        {
            result = lib.Publish(Draft, Published);
        }
        catch (Exception)
        {
            try
            {
                lib.RecoverIfNeeded();
            }
            catch (Exception e2)
            {
                GD.PushWarning("Откат после неудачной публикации не удался: " + e2.Message);
            }
            throw;
        }
        lib.LoadPins(Draft);
        Published = Draft.Clone();
        LoadIssues = ContentValidator.Validate(Published);
        Render.Assets.Reset();
        LoadIssues.AddRange(AssetValidator.CheckFiles(Published, Render.Assets.Read));
        UsingUserContent = true;
        Integrity = lib.CheckIntegrity();
        DraftDirty = false;
        ClearAutosave();
        return result;
    }

    /// <summary>Restores damaged/missing files of the user pack from the copies and reloads it.</summary>
    public List<string> RestoreDamaged()
    {
        var report = Library.CheckIntegrity();
        var restored = Library.RestoreFromBackup(report.Damaged.Concat(report.Missing));
        Load();
        if (Draft != null && !DraftDirty)
        {
            Draft = Published.Clone();
            ContentValidator.Validate(Draft);
        }
        return restored;
    }

    // ------------------------------------------------------------------ favourites (personal, not content)

    private static string FavKey(Def d) => $"{d.Category}:{d.Id}";

    public bool IsFavorite(Def d) => Main.Instance.Settings.Favorites.Contains(FavKey(d));

    public void SetFavorite(Def d, bool on)
    {
        var fav = Main.Instance.Settings.Favorites;
        bool has = fav.Contains(FavKey(d));
        if (on == has) return;
        if (on) fav.Add(FavKey(d));
        else fav.Remove(FavKey(d));
        Main.Instance.Settings.Save();
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
