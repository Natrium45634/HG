using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HexTactics.Core.Content;

/// <summary>An object listed in a package manifest.</summary>
public sealed class PackageObjectInfo
{
    public string Category { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Version { get; set; } = 1;
    /// <summary>A pinned old version (referenced as id@N), not the current object of the source library.</summary>
    public bool Pinned { get; set; }
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    /// <summary>References of the object: "Category:id" or "Category:id@N".</summary>
    public List<string> Dependencies { get; set; } = new();
    public bool Incomplete { get; set; }
}

/// <summary>A visual resource file of a package (stage 10 fills them).</summary>
public sealed class PackageAssetInfo
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>manifest.json of a .hexpack (docs/DECISIONS_2.md §32).</summary>
public sealed class PackageManifest
{
    public const int CurrentPackageFormat = 1;

    public int PackageFormat { get; set; } = CurrentPackageFormat;
    /// <summary>Content format of the object files (migrations apply to older ones).</summary>
    public int FormatVersion { get; set; } = ContentJson.CurrentFormatVersion;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public string Created { get; set; } = "";
    public string Game { get; set; } = "HexTactics";
    public List<PackageObjectInfo> Objects { get; set; } = new();
    public List<PackageAssetInfo> Assets { get; set; } = new();
    /// <summary>The package lacks some dependencies (they are listed): it imports only as a draft.</summary>
    public bool Incomplete { get; set; }
    public List<string> MissingDependencies { get; set; } = new();
    /// <summary>SHA-256 over "path:sha" lines of every file (integrity of the whole package).</summary>
    public string Hash { get; set; } = "";
}

public sealed class PackageExportOptions
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    public DateTime? Created { get; set; }
    /// <summary>Visual resources to include (relative paths) and how to read them.</summary>
    public List<string> AssetPaths { get; set; } = new();
    public Func<string, byte[]?>? ReadAsset { get; set; }
}

/// <summary>A read and checked package (steps 1–2 of the import: format, integrity, version).</summary>
public sealed class PackageContents
{
    public PackageManifest Manifest { get; init; } = new();
    /// <summary>Current versions of the objects.</summary>
    public List<Def> Objects { get; } = new();
    /// <summary>Pinned old versions (id@N references).</summary>
    public List<Def> PinnedObjects { get; } = new();
    public Dictionary<string, byte[]> Assets { get; } = new(StringComparer.Ordinal);
    public List<ContentIssue> Issues { get; } = new();
    /// <summary>Content format of the files before migration (when older than the current one).</summary>
    public int? MigratedFrom { get; set; }
    public bool Ok => Issues.All(i => i.Severity != IssueSeverity.Error);
    public bool IsSingleObject { get; init; }
}

public enum ImportStatus
{
    /// <summary>No object with this ID in the library.</summary>
    New,
    /// <summary>The library already has the same content: nothing to do.</summary>
    Identical,
    /// <summary>The library has a different object with this ID: the admin chooses.</summary>
    Conflict,
}

public enum ConflictResolution
{
    Skip,
    /// <summary>Import as a copy with a new ID; references inside the package are rewritten.</summary>
    Copy,
    /// <summary>Replace the existing object (only after an explicit confirmation).</summary>
    Replace,
}

public sealed class ImportItem
{
    public required Def Def { get; init; }
    public ImportStatus Status { get; init; }
    public Def? Existing { get; init; }
    public List<DiffLine> Diff { get; init; } = new();
    public ConflictResolution Resolution { get; set; } = ConflictResolution.Skip;
    public List<DependencyInfo> MissingDependencies { get; } = new();
    public string Key => $"{Def.Category}:{Def.Id}";
}

/// <summary>Steps 3–5: dependencies, preview, ID conflicts.</summary>
public sealed class ImportPlan
{
    public required PackageContents Package { get; init; }
    public List<ImportItem> Items { get; } = new();
    /// <summary>Dependencies found neither in the package nor in the library.</summary>
    public List<(Def Owner, DependencyInfo Dependency)> Missing { get; } = new();
    /// <summary>Dependencies satisfied by the library (not in the package).</summary>
    public List<(Def Owner, DependencyInfo Dependency)> FromLibrary { get; } = new();
    public bool NeedsIncomplete => Missing.Count > 0 || Package.Manifest.Incomplete;
    public IEnumerable<ImportItem> Conflicts => Items.Where(i => i.Status == ImportStatus.Conflict);
}

public sealed class ImportReport
{
    public List<Def> Added { get; } = new();
    public List<(Def Def, string OldId)> Copied { get; } = new();
    public List<Def> Replaced { get; } = new();
    public List<ImportItem> Skipped { get; } = new();
    public int Identical { get; set; }
    public List<Def> MarkedIncomplete { get; } = new();
    public int PinnedAdded { get; set; }
    public List<ContentIssue> Issues { get; } = new();
    public int Assets { get; set; }

    public string Describe()
    {
        var lines = new List<string>
        {
            $"Добавлено: {Added.Count}, копий с новым ID: {Copied.Count}, заменено: {Replaced.Count}, пропущено: {Skipped.Count}, уже было: {Identical}",
        };
        foreach (var (d, old) in Copied.Take(10)) lines.Add($"  копия: {ContentValidator.CatName(d.Category)} [{old}] → [{d.Id}]");
        foreach (var d in Replaced.Take(10)) lines.Add($"  заменён: {ContentValidator.CatName(d.Category)} «{d.DisplayName}»");
        if (PinnedAdded > 0) lines.Add($"Старых экземпляров (закреплённых версий): {PinnedAdded}");
        if (MarkedIncomplete.Count > 0)
            lines.Add($"Отмечены как неполные (черновики): {string.Join(", ", MarkedIncomplete.Take(8).Select(d => d.DisplayName))}");
        if (Assets > 0) lines.Add($"Ресурсов в пакете: {Assets}");
        int errors = Issues.Count(i => i.Severity == IssueSeverity.Error);
        int warns = Issues.Count(i => i.Severity == IssueSeverity.Warning);
        lines.Add(errors + warns == 0 ? "Проверка импортированного: без замечаний." : $"Проверка импортированного: ошибок {errors}, предупреждений {warns}.");
        foreach (var i in Issues.Where(i => i.Severity == IssueSeverity.Error).Take(8)) lines.Add("  ✗ " + i);
        return string.Join("\n", lines);
    }
}

/// <summary>
/// Packages of related objects (docs/DECISIONS_2.md §32): .hexpack = ZIP with manifest.json,
/// objects/&lt;Category&gt;/&lt;id&gt;.json (pinned old versions: &lt;id&gt;@&lt;N&gt;.json) and assets/…. Export
/// collects every transitive dependency; import runs the nine steps of TZ §14.2 — read (format,
/// integrity, version) → <see cref="Plan"/> (dependencies, preview, conflicts) → resolution →
/// <see cref="Apply"/> (import into the draft, validation, report). Nothing is overwritten silently.
/// </summary>
public static class ContentPackage
{
    public const string Extension = ".hexpack";
    public const string ManifestEntry = "manifest.json";

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ------------------------------------------------------------------ paths

    /// <summary>A safe relative path inside a package: no absolute paths, drive letters, "..", backslashes.</summary>
    public static bool IsSafePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240) return false;
        if (path.StartsWith('/') || path.Contains('\\') || path.Contains(':') || path.Contains('\0')) return false;
        return path.Split('/').All(part => part.Length > 0 && part != "." && part != "..");
    }

    private static string ObjectPath(Def d, bool pinned) =>
        $"objects/{d.Category}/{ContentIO.SafeFileName(d.Id)}{(pinned ? "@" + d.Version : "")}.json";

    private static string TotalHash(IEnumerable<(string Path, string Sha)> files) =>
        ContentLibrary.Sha256(string.Join("\n", files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => f.Path + ":" + f.Sha)));

    private static string Sha(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    // ------------------------------------------------------------------ export

    /// <summary>The objects a package of the roots contains: roots + every transitive dependency (pinned versions included).</summary>
    public static List<Def> Collect(ContentDatabase db, IEnumerable<Def> roots) => ContentDependencies.Closure(db, roots);

    public static byte[] Export(ContentDatabase db, IEnumerable<Def> roots, PackageExportOptions options) => Export(db, roots, options, out _);

    public static byte[] Export(ContentDatabase db, IEnumerable<Def> roots, PackageExportOptions options, out PackageManifest manifest)
    {
        var rootList = roots.ToList();
        var objects = Collect(db, rootList);
        var created = options.Created ?? DateTime.Now;
        manifest = new PackageManifest
        {
            Id = string.IsNullOrEmpty(options.Id) ? ContentIO.SafeFileName(rootList.FirstOrDefault()?.Id ?? "package") : options.Id,
            Name = string.IsNullOrEmpty(options.Name) ? rootList.FirstOrDefault()?.DisplayName ?? "Пакет" : options.Name,
            Description = options.Description,
            Author = options.Author,
            Created = created.ToString("yyyy-MM-ddTHH:mm:ss"),
        };
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var d in objects)
        {
            bool pinned = db.Table(d.Category).Get(d.Id) != d;
            var path = ObjectPath(d, pinned);
            var bytes = Encoding.UTF8.GetBytes(ContentIO.ExportDef(d));
            files[path] = bytes;
            var info = new PackageObjectInfo
            {
                Category = d.Category.ToString(), Id = d.Id, Name = d.DisplayName, Version = d.Version, Pinned = pinned,
                Path = path, Sha256 = Sha(bytes), Incomplete = d.Incomplete,
                Dependencies = ContentDependencies.Uses(d).Select(u => $"{u.Category}:{u.Reference}").Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(),
            };
            manifest.Objects.Add(info);
            foreach (var m in ContentDependencies.Missing(db, d))
                manifest.MissingDependencies.Add($"{d.Category}:{d.Id} → {m.Category}:{m.Reference}");
        }
        foreach (var rel in options.AssetPaths.Distinct())
        {
            if (!IsSafePath(rel)) throw new ArgumentException($"Недопустимый путь ресурса '{rel}': только относительные пути внутри пакета");
            var bytes = options.ReadAsset?.Invoke(rel);
            if (bytes == null)
            {
                manifest.MissingDependencies.Add("ресурс " + rel);
                continue;
            }
            var path = "assets/" + rel;
            files[path] = bytes;
            manifest.Assets.Add(new PackageAssetInfo { Path = path, Sha256 = Sha(bytes), Size = bytes.Length });
        }
        manifest.Incomplete = manifest.MissingDependencies.Count > 0 || objects.Any(o => o.Incomplete);
        manifest.Hash = TotalHash(manifest.Objects.Select(o => (o.Path, o.Sha256)).Concat(manifest.Assets.Select(a => (a.Path, a.Sha256))));

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            // Fixed entry times: the same content gives the same bytes.
            var stamp = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            void Add(string name, byte[] data)
            {
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                e.LastWriteTime = stamp;
                using var s = e.Open();
                s.Write(data);
            }
            Add(ManifestEntry, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, ManifestJson)));
            foreach (var kv in files) Add(kv.Key, kv.Value);
        }
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ import: read (format, integrity, version)

    public static PackageContents Read(byte[] data)
    {
        try
        {
            return ReadUnchecked(data);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException or ArgumentException)
        {
            var bad = new PackageContents();
            bad.Issues.Add(new ContentIssue(IssueSeverity.Error, "пакет", "", "Пакет повреждён и не читается: " + e.Message));
            return bad;
        }
    }

    private static PackageContents ReadUnchecked(byte[] data)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            var bad = new PackageContents();
            bad.Issues.Add(new ContentIssue(IssueSeverity.Error, "пакет", "", "Файл не является пакетом .hexpack (повреждён или другой формат)"));
            return bad;
        }
        using (zip)
        {
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            var unsafePaths = new List<string>();
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/')) continue;
                if (!IsSafePath(e.FullName)) unsafePaths.Add(e.FullName);
                else entries[e.FullName] = e;
            }
            PackageManifest? manifest = null;
            var issues = new List<ContentIssue>();
            foreach (var p in unsafePaths)
                issues.Add(new ContentIssue(IssueSeverity.Error, "пакет", p, "Недопустимый путь в пакете (абсолютный или с «..») — пакет отвергнут"));
            if (!entries.TryGetValue(ManifestEntry, out var me))
            {
                issues.Add(new ContentIssue(IssueSeverity.Error, "пакет", ManifestEntry, "Нет manifest.json"));
            }
            else
            {
                try
                {
                    manifest = JsonSerializer.Deserialize<PackageManifest>(ReadAll(me), ManifestJson);
                }
                catch (JsonException ex)
                {
                    issues.Add(new ContentIssue(IssueSeverity.Error, "пакет", ManifestEntry, "manifest.json не читается: " + ex.Message));
                }
            }
            var result = new PackageContents { Manifest = manifest ?? new PackageManifest() };
            result.Issues.AddRange(issues);
            if (manifest == null || !result.Ok) return result;

            if (manifest.PackageFormat > PackageManifest.CurrentPackageFormat)
            {
                result.Issues.Add(new ContentIssue(IssueSeverity.Error, "пакет", "packageFormat",
                    $"Пакет формата {manifest.PackageFormat} создан более новой версией игры (поддерживается до {PackageManifest.CurrentPackageFormat})"));
                return result;
            }
            if (manifest.FormatVersion > ContentJson.CurrentFormatVersion)
            {
                result.Issues.Add(new ContentIssue(IssueSeverity.Error, "пакет", "formatVersion",
                    $"Контент формата v{manifest.FormatVersion} новее поддерживаемого v{ContentJson.CurrentFormatVersion} — обновите игру"));
                return result;
            }

            // Integrity: every listed file is present with its hash, the total hash matches, nothing unlisted.
            var listed = new HashSet<string>(StringComparer.Ordinal) { ManifestEntry };
            var hashes = new List<(string, string)>();
            var texts = new Dictionary<PackageObjectInfo, string>();
            foreach (var o in manifest.Objects)
            {
                listed.Add(o.Path);
                if (!IsSafePath(o.Path) || !entries.TryGetValue(o.Path, out var e))
                {
                    result.Issues.Add(new ContentIssue(IssueSeverity.Error, "пакет", o.Path, $"Нет файла объекта {o.Category}:{o.Id}"));
                    continue;
                }
                var bytes = ReadBytes(e);
                var sha = Sha(bytes);
                hashes.Add((o.Path, sha));
                if (sha != o.Sha256) result.Issues.Add(new ContentIssue(IssueSeverity.Error, "пакет", o.Path, $"Файл объекта {o.Category}:{o.Id} повреждён (контрольная сумма не совпадает)"));
                else texts[o] = Encoding.UTF8.GetString(bytes);
            }
            foreach (var a in manifest.Assets)
            {
                listed.Add(a.Path);
                if (!IsSafePath(a.Path) || !a.Path.StartsWith("assets/", StringComparison.Ordinal) || !entries.TryGetValue(a.Path, out var e))
                {
                    result.Issues.Add(new ContentIssue(IssueSeverity.Error, "пакет", a.Path, "Нет файла ресурса"));
                    continue;
                }
                var bytes = ReadBytes(e);
                var sha = Sha(bytes);
                hashes.Add((a.Path, sha));
                if (sha != a.Sha256) result.Issues.Add(new ContentIssue(IssueSeverity.Error, "пакет", a.Path, "Файл ресурса повреждён (контрольная сумма не совпадает)"));
                else result.Assets[a.Path["assets/".Length..]] = bytes;
            }
            if (result.Ok && TotalHash(hashes) != manifest.Hash)
                result.Issues.Add(new ContentIssue(IssueSeverity.Error, "пакет", "hash", "Общая контрольная сумма пакета не совпадает — пакет изменён или повреждён"));
            foreach (var extra in entries.Keys.Where(k => !listed.Contains(k)))
                result.Issues.Add(new ContentIssue(IssueSeverity.Warning, "пакет", extra, "Файл не указан в манифесте — игнорируется"));
            if (!result.Ok) return result;

            // Version compatibility: older object files are migrated by the importer.
            foreach (var o in manifest.Objects)
            {
                var text = texts[o];
                int fv = FormatOf(text);
                if (fv < ContentJson.CurrentFormatVersion) result.MigratedFrom = Math.Min(result.MigratedFrom ?? fv, fv);
                var def = ContentIO.ImportDef(text, result.Issues);
                if (def == null) continue;
                if (def.Category.ToString() != o.Category || def.Id != o.Id)
                {
                    result.Issues.Add(new ContentIssue(IssueSeverity.Error, "пакет", o.Path, $"Файл содержит {def.Category}:{def.Id}, а манифест — {o.Category}:{o.Id}"));
                    continue;
                }
                if (o.Pinned)
                {
                    def.Version = o.Version;
                    result.PinnedObjects.Add(def);
                }
                else result.Objects.Add(def);
            }
            if (result.MigratedFrom != null)
                result.Issues.Add(new ContentIssue(IssueSeverity.Info, "пакет", "formatVersion", $"Объекты формата v{result.MigratedFrom} обновлены до v{ContentJson.CurrentFormatVersion} (миграция)"));
            return result;
        }
    }

    /// <summary>A single exported object (.json) goes through the same import steps.</summary>
    public static PackageContents ReadSingle(string json)
    {
        var result = new PackageContents { IsSingleObject = true };
        int fv = FormatOf(json);
        if (fv > ContentJson.CurrentFormatVersion)
        {
            result.Issues.Add(new ContentIssue(IssueSeverity.Error, "импорт", "formatVersion", $"Файл формата v{fv} новее поддерживаемого v{ContentJson.CurrentFormatVersion} — обновите игру"));
            return result;
        }
        if (fv >= 0 && fv < ContentJson.CurrentFormatVersion) result.MigratedFrom = fv;
        var def = ContentIO.ImportDef(json, result.Issues);
        if (def == null) return result;
        result.Objects.Add(def);
        result.Manifest.Id = def.Id;
        result.Manifest.Name = def.DisplayName;
        result.Manifest.Objects.Add(new PackageObjectInfo { Category = def.Category.ToString(), Id = def.Id, Name = def.DisplayName, Version = def.Version, Incomplete = def.Incomplete });
        return result;
    }

    private static int FormatOf(string text)
    {
        try
        {
            return (JsonNode.Parse(text) as JsonObject)?["formatVersion"]?.GetValue<int>() ?? 0;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static byte[] ReadBytes(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string ReadAll(ZipArchiveEntry e) => Encoding.UTF8.GetString(ReadBytes(e));

    // ------------------------------------------------------------------ import: plan (dependencies, preview, conflicts)

    private static string Canonical(Def d)
    {
        var copy = ContentJson.Clone(d);
        copy.Version = 0;
        copy.Incomplete = false;
        return JsonSerializer.Serialize(copy, copy.GetType(), ContentJson.Options);
    }

    public static ImportPlan Plan(ContentDatabase target, PackageContents package)
    {
        var plan = new ImportPlan { Package = package };
        var inPackage = new HashSet<(DefCategory, string)>(package.Objects.Select(o => (o.Category, o.Id)));
        var pinnedInPackage = new HashSet<(DefCategory, string, int)>(package.PinnedObjects.Select(o => (o.Category, o.Id, o.Version)));
        foreach (var d in package.Objects)
        {
            var existing = target.Table(d.Category).Get(d.Id);
            ImportItem item;
            if (existing == null) item = new ImportItem { Def = d, Status = ImportStatus.New };
            else if (existing.GetType() == d.GetType() && Canonical(existing) == Canonical(d)) item = new ImportItem { Def = d, Status = ImportStatus.Identical, Existing = existing };
            else item = new ImportItem { Def = d, Status = ImportStatus.Conflict, Existing = existing, Diff = ContentDiff.Compare(existing, d) };
            plan.Items.Add(item);
        }
        foreach (var d in package.Objects.Concat(package.PinnedObjects))
        foreach (var dep in ContentDependencies.Uses(d))
        {
            var (id, v) = ContentRef.Parse(dep.Reference);
            bool packaged = v == null ? inPackage.Contains((dep.Category, id)) : pinnedInPackage.Contains((dep.Category, id, v.Value))
                                                                               || package.Objects.Any(o => o.Category == dep.Category && o.Id == id && o.Version == v);
            if (packaged) continue;
            if (target.Exists(dep.Category, dep.Reference))
            {
                plan.FromLibrary.Add((d, dep));
                continue;
            }
            plan.Missing.Add((d, dep));
            plan.Items.FirstOrDefault(i => i.Def == d)?.MissingDependencies.Add(dep);
        }
        return plan;
    }

    // ------------------------------------------------------------------ import: apply, validate, report

    /// <summary>
    /// Imports into the target (the editor draft). A plan with missing dependencies imports only with
    /// allowIncomplete — the objects that lack something are marked «неполный».
    /// </summary>
    public static ImportReport Apply(ContentDatabase target, ImportPlan plan, bool allowIncomplete = false)
    {
        if (!plan.Package.Ok) throw new InvalidOperationException("Пакет не прошёл проверку — импорт невозможен");
        if (plan.NeedsIncomplete && !allowIncomplete)
            throw new InvalidOperationException("Не хватает зависимостей: импорт возможен только как черновик с отметкой «неполный»");
        var report = new ImportReport { Assets = plan.Package.Assets.Count };

        // New IDs for copies (unique in the target and among each other).
        var idMap = new Dictionary<(DefCategory, string), string>();
        var taken = new HashSet<(DefCategory, string)>(plan.Items.Where(i => i.Status == ImportStatus.New).Select(i => (i.Def.Category, i.Def.Id)));
        foreach (var item in plan.Items.Where(i => i.Status == ImportStatus.Conflict && i.Resolution == ConflictResolution.Copy))
        {
            string baseId = item.Def.Id;
            string id = ContentIO.UniqueId(target, item.Def.Category, baseId);
            for (int n = 2; taken.Contains((item.Def.Category, id)) || target.Exists(item.Def.Category, id); n++) id = $"{baseId}_copy{n}";
            taken.Add((item.Def.Category, id));
            idMap[(item.Def.Category, item.Def.Id)] = id;
        }

        Def Prepare(Def source)
        {
            var d = ContentJson.Clone(source);
            if (idMap.TryGetValue((d.Category, d.Id), out var nid)) d.Id = nid;
            // References inside the package follow the copies (pins keep their version).
            ReferenceWalker.WalkDef(d, (owner, path, cat, value, set, _) =>
            {
                if (string.IsNullOrEmpty(value) || (owner == d && path == d.Id + ".Id")) return;
                var (rid, v) = ContentRef.Parse(value);
                if (idMap.TryGetValue((cat, rid), out var mapped)) set(v == null ? mapped : ContentRef.Pin(mapped, v.Value));
            });
            return d;
        }

        var imported = new List<Def>();
        foreach (var item in plan.Items)
        {
            switch (item.Status)
            {
                case ImportStatus.Identical:
                    report.Identical++;
                    continue;
                case ImportStatus.Conflict when item.Resolution == ConflictResolution.Skip:
                    report.Skipped.Add(item);
                    continue;
            }
            var d = Prepare(item.Def);
            if (item.Status == ImportStatus.New)
            {
                target.Add(d);
                report.Added.Add(d);
            }
            else if (item.Resolution == ConflictResolution.Copy)
            {
                target.Add(d);
                report.Copied.Add((d, item.Def.Id));
            }
            else
            {
                // The publication raises the version of the replaced object (its old version goes to the history).
                d.Version = item.Existing!.Version;
                target.Table(d.Category).Replace(d);
                report.Replaced.Add(d);
            }
            if (item.MissingDependencies.Count > 0) d.Incomplete = true;
            if (d.Incomplete) report.MarkedIncomplete.Add(d);
            imported.Add(d);
        }

        foreach (var p in plan.Package.PinnedObjects)
        {
            var d = Prepare(p);
            var key = (d.Category, d.Id, d.Version);
            var cur = target.Table(d.Category).Get(d.Id);
            if ((cur != null && cur.Version == d.Version) || target.Pinned.ContainsKey(key)) continue;
            target.Pinned[key] = d;
            report.PinnedAdded++;
        }

        var sources = new HashSet<string>(imported.Select(d => $"{d.Category}:{d.Id}"));
        report.Issues.AddRange(ContentValidator.Validate(target).Where(i => sources.Contains(i.Source)));
        return report;
    }
}
