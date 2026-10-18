using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexTactics.Core.Content;

/// <summary>A changed definition at publication: its new version and where it is used.</summary>
public sealed class LibraryChange
{
    public DefCategory Category { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int OldVersion { get; init; }
    public int NewVersion { get; init; }
    /// <summary>Direct references (scenario units, skills of a unit…).</summary>
    public List<ReferenceInfo> Direct { get; init; } = new();
    /// <summary>Indirect users: definitions that use the direct users (a scenario using a unit using the skill).</summary>
    public List<Def> Indirect { get; init; } = new();
}

public sealed class LibraryPublishResult
{
    public List<string> Written { get; } = new();
    public List<LibraryChange> Changes { get; } = new();
    public string BackupDir { get; set; } = "";
}

public sealed class IntegrityReport
{
    public List<string> Missing { get; } = new();
    public List<string> Damaged { get; } = new();
    public bool HasManifest { get; set; }
    public bool Ok => Missing.Count == 0 && Damaged.Count == 0;

    public string Describe() => !HasManifest ? "манифест отсутствует (пакет ещё не публиковался библиотекой)"
        : Ok ? "целостность в порядке" : $"повреждено: {Damaged.Count}, отсутствует: {Missing.Count}";
}

public sealed class RecoveryReport
{
    public bool Recovered { get; set; }
    public string Message { get; set; } = "";
    public List<string> Restored { get; } = new();
}

/// <summary>
/// The content library on disk (docs/DECISIONS_2.md §31): JSON files + manifest with SHA-256 of every
/// file + a transaction journal. Publication: versions of changed definitions grow and the previous
/// version goes to history/&lt;category&gt;/&lt;id&gt;/v&lt;N&gt;.json; journal «writing» → backup of the files about to
/// change → atomic writes (temp file + replace) → manifest → journal removed. An unfinished journal at
/// start means a crash: the pack is rolled back to the backup. Pinned references "id@N" are loaded
/// from the history.
/// </summary>
public sealed class ContentLibrary
{
    public const string ManifestFile = "manifest.json";
    public const string JournalFile = "journal.json";
    public const string HistoryDir = "history";
    public const string BackupDir = "backups";
    /// <summary>Mirror of the last published state (for restoring damaged files).</summary>
    public const string MirrorDir = "backups/current";
    /// <summary>How many rollback copies (one per publication) are kept.</summary>
    public int KeepBackups { get; set; } = 10;

    private readonly IContentStorage _storage;
    private readonly Func<DateTime> _clock;

    /// <summary>Fault injection for tests: called before every file write of a publication (throw to simulate a crash).</summary>
    public Action<string>? BeforeWrite { get; set; }

    public ContentLibrary(IContentStorage storage, Func<DateTime>? clock = null)
    {
        _storage = storage;
        _clock = clock ?? (() => DateTime.Now);
    }

    public static string Sha256(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string HistoryPath(DefCategory c, string id, int version) => $"{HistoryDir}/{c}/{ContentIO.SafeFileName(id)}/v{version}.json";

    // ------------------------------------------------------------------ versions

    private static string Canonical(Def d)
    {
        // Compare content without the version itself.
        var copy = ContentJson.Clone(d);
        copy.Version = 0;
        return JsonSerializer.Serialize(copy, copy.GetType(), ContentJson.Options);
    }

    /// <summary>Changed definitions of a draft against the published content, with their usages (direct and indirect).</summary>
    public static List<LibraryChange> Changes(ContentDatabase draft, ContentDatabase? previous)
    {
        var list = new List<LibraryChange>();
        if (previous == null) return list;
        foreach (var d in draft.AllDefs())
        {
            var prev = previous.Table(d.Category).Get(d.Id);
            if (prev == null || prev.GetType() != d.GetType()) continue;
            if (Canonical(prev) == Canonical(d)) continue;
            // Only references that follow the latest version are affected; pinned ones ("id@N") stay.
            var direct = draft.FindReferences(d.Category, d.Id).Where(r => r.Owner != null && r.Id == d.Id).ToList();
            var indirect = new List<Def>();
            var seen = new HashSet<Def>(direct.Select(r => r.Owner!));
            var frontier = new Queue<Def>(seen);
            while (frontier.Count > 0 && indirect.Count < 200)
            {
                var o = frontier.Dequeue();
                foreach (var r in draft.FindReferences(o.Category, o.Id))
                    if (r.Owner != null && seen.Add(r.Owner))
                    {
                        indirect.Add(r.Owner);
                        frontier.Enqueue(r.Owner);
                    }
            }
            list.Add(new LibraryChange
            {
                Category = d.Category, Id = d.Id, Name = d.DisplayName, OldVersion = Math.Max(1, prev.Version), NewVersion = Math.Max(1, prev.Version) + 1,
                Direct = direct, Indirect = indirect,
            });
        }
        return list;
    }

    /// <summary>«Сохранить старые экземпляры»: the chosen references point to the previous version (id@old).</summary>
    public static int PinUsages(ContentDatabase draft, LibraryChange change, IEnumerable<ReferenceInfo> which)
    {
        var keep = new HashSet<(Def, string)>(which.Where(r => r.Owner != null).Select(r => (r.Owner!, r.Path)));
        int n = 0;
        ReferenceWalker.Walk(draft, (owner, path, cat, value, set, _) =>
        {
            if (owner == null || cat != change.Category || value != change.Id || !keep.Contains((owner, path))) return;
            set(ContentRef.Pin(change.Id, change.OldVersion));
            n++;
        });
        return n;
    }

    public List<int> Versions(DefCategory c, string id)
    {
        var dir = $"{HistoryDir}/{c}/{ContentIO.SafeFileName(id)}";
        var list = new List<int>();
        foreach (var f in _storage.ListFiles(dir, ".json"))
            if (f.StartsWith('v') && int.TryParse(f.AsSpan(1, f.Length - 6), out var v))
                list.Add(v);
        list.Sort();
        return list;
    }

    public Def? LoadVersion(DefCategory c, string id, int version)
    {
        var text = _storage.ReadText(HistoryPath(c, id, version));
        if (text == null) return null;
        var issues = new List<ContentIssue>();
        return ContentIO.ImportDef(text, issues);
    }

    /// <summary>Loads every pinned version referenced by the content ("id@N") from the history.</summary>
    public int LoadPins(ContentDatabase db)
    {
        int n = 0;
        var wanted = new List<(DefCategory, string, int)>();
        ReferenceWalker.Walk(db, (_, _, cat, value, _, _) =>
        {
            var (id, v) = ContentRef.Parse(value);
            if (v != null) wanted.Add((cat, id, v.Value));
        });
        foreach (var (cat, id, v) in wanted.Distinct())
        {
            if (db.Pinned.ContainsKey((cat, id, v))) continue;
            var cur = db.Table(cat).Get(id);
            if (cur != null && cur.Version == v) continue;
            var old = LoadVersion(cat, id, v);
            if (old == null) continue;
            db.Pinned[(cat, id, v)] = old;
            n++;
        }
        return n;
    }

    // ------------------------------------------------------------------ publication (transactional)

    /// <summary>
    /// Publishes a draft: versions of changed definitions are increased (previous versions saved to the
    /// history), then all files are written transactionally with a backup and a manifest.
    /// </summary>
    public LibraryPublishResult Publish(ContentDatabase draft, ContentDatabase? previous)
    {
        var result = new LibraryPublishResult();
        var changes = Changes(draft, previous);
        result.Changes.AddRange(changes);
        var history = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ch in changes)
        {
            var prev = previous!.Table(ch.Category).Get(ch.Id)!;
            var old = ContentJson.Clone(prev);
            old.Version = ch.OldVersion;
            history[HistoryPath(ch.Category, ch.Id, ch.OldVersion)] = ContentIO.ExportDef(old);
            draft.Table(ch.Category).Get(ch.Id)!.Version = ch.NewVersion;
        }

        // Pinned versions that came from elsewhere (an imported package) are kept in the history too.
        foreach (var kv in draft.Pinned)
        {
            var path = HistoryPath(kv.Key.Category, kv.Key.Id, kv.Key.Version);
            if (history.ContainsKey(path) || _storage.Exists(path)) continue;
            var old = ContentJson.Clone(kv.Value);
            old.Version = kv.Key.Version;
            history[path] = ContentIO.ExportDef(old);
        }

        var (plan, toDelete) = ContentIO.BuildPlan(draft, _storage);
        foreach (var kv in history) plan[kv.Key] = kv.Value;
        var changed = plan.Where(kv => _storage.ReadText(kv.Key) != kv.Value).Select(kv => kv.Key).ToList();
        if (changed.Count == 0 && toDelete.Count == 0)
        {
            if (CheckIntegrity() is { HasManifest: false } or { Ok: false }) WriteManifest(plan);
            UpdateMirror(plan, new List<string>());
            return result;
        }

        string stamp = _clock().ToString("yyyyMMdd_HHmmss_fff");
        string backup = $"{BackupDir}/{stamp}";
        result.BackupDir = backup;

        // 1. Journal: what is about to change (the manifest included) and where the backup is.
        var touched = changed.Concat(toDelete).Append(ManifestFile).ToList();
        var journal = new JsonObject
        {
            ["state"] = "writing",
            ["backup"] = backup,
            ["started"] = _clock().ToString("O"),
            ["files"] = new JsonArray(touched.Select(f => (JsonNode)f).ToArray()),
            ["existed"] = new JsonArray(touched.Where(_storage.Exists).Select(f => (JsonNode)f).ToArray()),
        };
        _storage.WriteText(JournalFile, journal.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // 2. Backup of every file that will change or disappear.
        foreach (var rel in touched)
        {
            var old = _storage.ReadText(rel);
            if (old != null) _storage.WriteText($"{backup}/{rel}", old);
        }

        // 3. Atomic writes (temp file + replace per file).
        foreach (var rel in changed)
        {
            BeforeWrite?.Invoke(rel);
            _storage.WriteText(rel, plan[rel]);
            result.Written.Add(rel);
        }
        foreach (var rel in toDelete)
        {
            BeforeWrite?.Invoke(rel);
            _storage.Delete(rel);
            result.Written.Add(rel + " (удалён)");
        }

        // 4. Manifest, then the journal is removed: the transaction is complete.
        BeforeWrite?.Invoke(ManifestFile);
        WriteManifest(plan);
        _storage.Delete(JournalFile);

        // 5. Outside the transaction: the mirror of the published state and pruning of old copies.
        UpdateMirror(plan, toDelete);
        PruneBackups();
        return result;
    }

    private void UpdateMirror(Dictionary<string, string> plan, List<string> deleted)
    {
        try
        {
            foreach (var kv in plan)
                if (_storage.ReadText($"{MirrorDir}/{kv.Key}") != kv.Value)
                    _storage.WriteText($"{MirrorDir}/{kv.Key}", kv.Value);
            foreach (var rel in deleted) _storage.Delete($"{MirrorDir}/{rel}");
        }
        catch (Exception)
        {
            // The mirror is a convenience copy: a failure here never breaks a completed publication.
        }
    }

    /// <summary>Rollback copies, newest first (folder names are time stamps).</summary>
    public List<string> Backups() => _storage.ListDirectories(BackupDir).Where(d => d != "current")
        .OrderByDescending(d => d, StringComparer.Ordinal).ToList();

    private void PruneBackups()
    {
        foreach (var old in Backups().Skip(Math.Max(1, KeepBackups)))
        {
            try
            {
                _storage.DeleteDirectory($"{BackupDir}/{old}");
            }
            catch (Exception)
            {
                // Pruning is best-effort.
            }
        }
    }

    private void WriteManifest(Dictionary<string, string> plan)
    {
        var files = new JsonObject();
        foreach (var kv in plan.OrderBy(k => k.Key, StringComparer.Ordinal)) files[kv.Key] = Sha256(kv.Value);
        var manifest = new JsonObject
        {
            ["formatVersion"] = ContentJson.CurrentFormatVersion,
            ["published"] = _clock().ToString("O"),
            ["files"] = files,
        };
        _storage.WriteText(ManifestFile, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // ------------------------------------------------------------------ crash recovery and integrity

    /// <summary>An unfinished journal means the previous publication crashed: roll the pack back to the backup.</summary>
    public RecoveryReport RecoverIfNeeded()
    {
        var report = new RecoveryReport();
        var text = _storage.ReadText(JournalFile);
        if (text == null) return report;
        JsonObject? j;
        try
        {
            j = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            j = null;
        }
        if (j == null)
        {
            _storage.Delete(JournalFile);
            report.Message = "Журнал публикации повреждён — удалён; проверьте целостность пакета.";
            return report;
        }
        string backup = (string?)j["backup"] ?? "";
        var existed = new HashSet<string>((j["existed"] as JsonArray ?? new JsonArray()).Select(x => (string?)x ?? ""));
        foreach (var node in j["files"] as JsonArray ?? new JsonArray())
        {
            var rel = (string?)node;
            if (string.IsNullOrEmpty(rel)) continue;
            var saved = _storage.ReadText($"{backup}/{rel}");
            if (saved != null)
            {
                _storage.WriteText(rel, saved);
                report.Restored.Add(rel);
            }
            else if (!existed.Contains(rel) && _storage.Exists(rel))
            {
                _storage.Delete(rel); // a file created by the unfinished publication
                report.Restored.Add(rel + " (удалён)");
            }
        }
        _storage.Delete(JournalFile);
        report.Recovered = true;
        report.Message = $"Предыдущее сохранение было прервано (сбой). Пакет восстановлен из резервной копии: файлов {report.Restored.Count}.";
        return report;
    }

    /// <summary>Compares every file with the SHA-256 of the manifest.</summary>
    public IntegrityReport CheckIntegrity()
    {
        var r = new IntegrityReport();
        var text = _storage.ReadText(ManifestFile);
        if (text == null) return r;
        r.HasManifest = true;
        JsonObject? files;
        try
        {
            files = (JsonNode.Parse(text) as JsonObject)?["files"] as JsonObject;
        }
        catch (JsonException)
        {
            files = null;
        }
        if (files == null)
        {
            r.Damaged.Add(ManifestFile);
            return r;
        }
        foreach (var kv in files)
        {
            var cur = _storage.ReadText(kv.Key);
            if (cur == null) r.Missing.Add(kv.Key);
            else if (Sha256(cur) != (string?)kv.Value) r.Damaged.Add(kv.Key);
        }
        return r;
    }

    /// <summary>
    /// Restores files from a copy matching the manifest: the mirror of the published state first, then
    /// the rollback copies (newest first); without a matching copy the newest copy is used.
    /// </summary>
    public List<string> RestoreFromBackup(IEnumerable<string> files)
    {
        var restored = new List<string>();
        JsonObject? manifest;
        try
        {
            manifest = JsonNode.Parse(_storage.ReadText(ManifestFile) ?? "{}") as JsonObject;
        }
        catch (JsonException)
        {
            manifest = null;
        }
        var hashes = manifest?["files"] as JsonObject;
        var sources = new List<string> { MirrorDir };
        sources.AddRange(Backups().Select(b => $"{BackupDir}/{b}"));
        foreach (var rel in files)
        {
            string? want = (string?)hashes?[rel];
            string? best = null;
            foreach (var src in sources)
            {
                var copy = _storage.ReadText($"{src}/{rel}");
                if (copy == null) continue;
                if (want != null && Sha256(copy) == want)
                {
                    best = copy;
                    break;
                }
                best ??= copy;
            }
            if (best == null) continue;
            _storage.WriteText(rel, best);
            restored.Add(rel);
        }
        return restored;
    }
}
