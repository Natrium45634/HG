using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexTactics.Core.Content;

/// <summary>Result of checking a resource file.</summary>
public sealed class AssetFileInfo
{
    public bool Ok => Error == null;
    public string? Error { get; set; }
    /// <summary>Kind recognised from the content (not only the extension).</summary>
    public AssetKind? Kind { get; set; }
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    /// <summary>Animation clips of a glTF/GLB model.</summary>
    public List<string> Clips { get; } = new();
}

/// <summary>
/// Checks imported resource files (docs/DECISIONS_2.md §34, TZ §3.5): a white list of formats,
/// the file signature must match the extension, a size limit, glTF references only to safe relative
/// paths or embedded data. Scenes, scripts and binaries are rejected — resources are never executed.
/// </summary>
public static class AssetValidator
{
    /// <summary>Hard cap of any file; the configured limits per kind are in the rules (<see cref="AssetLimitsDef"/>).</summary>
    public const long MaxSize = 128L * 1024 * 1024;

    private static readonly Dictionary<string, AssetKind> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".glb"] = AssetKind.Model, [".gltf"] = AssetKind.Model, [".fbx"] = AssetKind.Model, [".obj"] = AssetKind.Model,
        [".png"] = AssetKind.Texture, [".jpg"] = AssetKind.Texture, [".jpeg"] = AssetKind.Texture, [".webp"] = AssetKind.Texture,
        [".tga"] = AssetKind.Texture, [".hdr"] = AssetKind.Texture, [".exr"] = AssetKind.Texture,
        [".ogg"] = AssetKind.Sound, [".wav"] = AssetKind.Sound, [".mp3"] = AssetKind.Sound,
    };

    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tscn", ".scn", ".res", ".tres", ".gd", ".cs", ".dll", ".exe", ".so", ".dylib", ".bat", ".cmd", ".sh", ".ps1", ".js", ".py",
        ".pck", ".zip", ".jar", ".msi", ".com", ".vbs", ".lua", ".gdextension",
    };

    public static IReadOnlyCollection<string> AllowedExtensions => Extensions.Keys;

    public static AssetKind? KindByExtension(string fileName) =>
        Extensions.TryGetValue(System.IO.Path.GetExtension(fileName), out var k) ? k : null;

    /// <summary>The kinds a file kind can serve as (a texture can also be a sprite or an icon).</summary>
    public static bool Compatible(AssetKind fileKind, AssetKind declared) =>
        fileKind == declared || (fileKind == AssetKind.Texture && declared is AssetKind.Sprite or AssetKind.Icon);

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static AssetFileInfo Check(byte[]? bytes, string fileName)
    {
        var info = new AssetFileInfo();
        string ext = System.IO.Path.GetExtension(fileName);
        if (Forbidden.Contains(ext))
        {
            info.Error = $"Файлы «{ext}» не импортируются: сцены, скрипты и программы могут содержать исполняемый код";
            return info;
        }
        if (!Extensions.TryGetValue(ext, out var kind))
        {
            info.Error = $"Формат «{ext}» не поддерживается (допустимо: {string.Join(", ", Extensions.Keys)})";
            return info;
        }
        if (bytes == null)
        {
            info.Error = "Файл отсутствует";
            return info;
        }
        info.Size = bytes.LongLength;
        if (bytes.LongLength == 0)
        {
            info.Error = "Файл пуст";
            return info;
        }
        if (bytes.LongLength > MaxSize)
        {
            info.Error = $"Файл больше {MaxSize / (1024 * 1024)} МБ";
            return info;
        }
        info.Sha256 = Sha256(bytes);
        info.Error = ext.ToLowerInvariant() switch
        {
            ".png" => Starts(bytes, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A) ? null : "Повреждён: нет сигнатуры PNG",
            ".jpg" or ".jpeg" => Starts(bytes, 0xFF, 0xD8, 0xFF) ? null : "Повреждён: нет сигнатуры JPEG",
            ".webp" => Ascii(bytes, 0, "RIFF") && Ascii(bytes, 8, "WEBP") ? null : "Повреждён: нет сигнатуры WebP",
            ".ogg" => Ascii(bytes, 0, "OggS") ? null : "Повреждён: нет сигнатуры OGG",
            ".wav" => Ascii(bytes, 0, "RIFF") && Ascii(bytes, 8, "WAVE") ? null : "Повреждён: нет сигнатуры WAV",
            ".tga" => TgaOk(bytes) ? null : "Повреждён: заголовок TGA некорректен",
            ".hdr" => Ascii(bytes, 0, "#?RADIANCE") || Ascii(bytes, 0, "#?RGBE") ? null : "Повреждён: нет сигнатуры Radiance HDR",
            ".exr" => Starts(bytes, 0x76, 0x2F, 0x31, 0x01) ? null : "Повреждён: нет сигнатуры OpenEXR",
            ".mp3" => Ascii(bytes, 0, "ID3") || bytes.Length > 1 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0 ? null : "Повреждён: нет сигнатуры MP3",
            ".fbx" => Ascii(bytes, 0, "Kaydara FBX Binary") || FbxAscii(bytes) ? null : "Повреждён: нет сигнатуры FBX",
            ".obj" => ObjOk(bytes),
            ".glb" => CheckGlb(bytes, info),
            ".gltf" => CheckGltfJson(Encoding.UTF8.GetString(bytes), info),
            _ => "Неизвестный формат",
        };
        if (info.Ok) info.Kind = kind;
        return info;
    }

    private static bool TgaOk(byte[] b) =>
        b.Length > 18 && b[1] <= 1 && b[2] is 1 or 2 or 3 or 9 or 10 or 11 && BitConverter.ToUInt16(b, 12) > 0 && BitConverter.ToUInt16(b, 14) > 0 && b[16] is 8 or 15 or 16 or 24 or 32;

    private static bool FbxAscii(byte[] b)
    {
        var head = Encoding.ASCII.GetString(b, 0, Math.Min(b.Length, 4096));
        return head.StartsWith("; FBX", StringComparison.Ordinal) || head.Contains("FBXHeaderExtension", StringComparison.Ordinal);
    }

    /// <summary>OBJ: text, vertex lines; nothing that looks binary.</summary>
    private static string? ObjOk(byte[] b)
    {
        int n = Math.Min(b.Length, 65536);
        for (int i = 0; i < n; i++)
            if (b[i] == 0) return "Повреждён: OBJ должен быть текстовым файлом";
        var text = Encoding.UTF8.GetString(b);
        return text.Split('\n').Any(l => l.TrimStart().StartsWith("v ", StringComparison.Ordinal)) ? null : "В OBJ нет вершин";
    }

    private static bool Starts(byte[] b, params byte[] sig) => b.Length >= sig.Length && sig.Select((x, i) => b[i] == x).All(x => x);

    private static bool Ascii(byte[] b, int at, string s) => b.Length >= at + s.Length && Encoding.ASCII.GetString(b, at, s.Length) == s;

    /// <summary>GLB: header "glTF", version 2, declared length, a JSON chunk first.</summary>
    private static string? CheckGlb(byte[] b, AssetFileInfo info)
    {
        if (b.Length < 20 || !Ascii(b, 0, "glTF")) return "Повреждён: нет сигнатуры GLB";
        uint version = BitConverter.ToUInt32(b, 4);
        uint length = BitConverter.ToUInt32(b, 8);
        if (version != 2) return $"Версия glTF {version} не поддерживается (нужна 2)";
        if (length != b.Length) return "Повреждён: длина файла не совпадает с заголовком GLB";
        uint chunkLength = BitConverter.ToUInt32(b, 12);
        if (!Ascii(b, 16, "JSON") || 20 + (long)chunkLength > b.Length) return "Повреждён: нет JSON-блока GLB";
        return CheckGltfJson(Encoding.UTF8.GetString(b, 20, (int)chunkLength), info, embedded: true);
    }

    /// <summary>glTF JSON: asset.version 2; buffers/images only as data: URIs or safe relative paths; animation names.</summary>
    private static string? CheckGltfJson(string json, AssetFileInfo info, bool embedded = false)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json.TrimEnd('\0', ' ')) as JsonObject;
        }
        catch (JsonException)
        {
            return "Повреждён: описание glTF не читается";
        }
        if (root == null) return "Повреждён: описание glTF пустое";
        if ((string?)root["asset"]?["version"] is not { } v || !v.StartsWith("2", StringComparison.Ordinal)) return "Нужна версия glTF 2.0";
        foreach (var listName in new[] { "buffers", "images" })
        {
            if (root[listName] is not JsonArray arr) continue;
            foreach (var item in arr)
            {
                var uri = (string?)item?["uri"];
                if (uri == null) continue; // GLB binary chunk / bufferView image
                if (uri.StartsWith("data:", StringComparison.Ordinal)) continue;
                if (!ContentPackage.IsSafePath(Uri.UnescapeDataString(uri)) || uri.Contains("://", StringComparison.Ordinal))
                    return $"Недопустимая ссылка «{uri}» в модели: только встроенные данные или относительные пути внутри пакета";
            }
        }
        if (root["animations"] is JsonArray anims)
            for (int i = 0; i < anims.Count; i++)
                info.Clips.Add((string?)anims[i]?["name"] is { Length: > 0 } n ? n : $"animation_{i}");
        return null;
    }

    // ------------------------------------------------------------------ import

    private static string KindDir(AssetKind k) => k switch
    {
        AssetKind.Model => "models",
        AssetKind.Sound => "sounds",
        AssetKind.Icon => "icons",
        AssetKind.Sprite => "sprites",
        _ => "textures",
    };

    /// <summary>
    /// Checks a file and describes it as a new resource: the path inside the pack (assets/&lt;kind&gt;/…;
    /// a different file with the same name gets a checksum prefix), checksum, size, model clips and
    /// a default role mapping by conventional clip names. Returns null and an error when the file is rejected.
    /// </summary>
    public static AssetDef? Import(ContentDatabase db, string fileName, byte[] bytes, Func<string, byte[]?> existing, out string? error, AssetKind? asKind = null,
        TexturePurpose? purpose = null)
    {
        var info = Check(bytes, fileName);
        error = info.Error;
        if (!info.Ok) return null;
        var kind = asKind != null && Compatible(info.Kind!.Value, asKind.Value) ? asKind.Value : info.Kind!.Value;
        string baseName = ContentIO.SafeFileName(System.IO.Path.GetFileNameWithoutExtension(fileName)).ToLowerInvariant();
        if (baseName.Length == 0) baseName = "asset";
        string ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
        string path = $"assets/{KindDir(kind)}/{baseName}{ext}";
        var old = existing(path);
        if (old != null && Sha256(old) != info.Sha256) path = $"assets/{KindDir(kind)}/{info.Sha256[..8]}_{baseName}{ext}";
        var def = new AssetDef
        {
            Id = ContentIO.UniqueId(db, DefCategory.Asset, baseName), Name = System.IO.Path.GetFileNameWithoutExtension(fileName), Kind = kind,
            Path = path, Sha256 = info.Sha256, Size = info.Size, Group = KindDir(kind),
            SourceFile = System.IO.Path.GetFileName(fileName), ImportedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture),
            Purpose = purpose ?? (kind == AssetKind.Icon ? TexturePurpose.SkillIcon : TexturePurpose.Other),
        };
        // What the file contains (docs/DECISIONS_3.md §47).
        var an = AssetAnalyzer.Analyze(bytes, fileName, db.Rules.Assets, def.Purpose);
        def.Width = an.Width;
        def.Height = an.Height;
        def.HasAlpha = an.HasAlpha;
        def.Triangles = an.Triangles;
        def.Bones = an.Bones;
        def.Duration = an.Duration;
        def.Analysis = an.Summary();
        if (an.Animations.Count > 0)
        {
            info.Clips.Clear();
            info.Clips.AddRange(an.Animations.Select(x => x.Name));
            def.ClipLengths.AddRange(an.Animations.Select(x => Math.Round(x.Seconds, 3)));
        }
        def.Clips.AddRange(info.Clips);
        foreach (var role in Enum.GetValues<AnimRole>())
        {
            var clip = GuessClip(role, info.Clips);
            if (clip != null) def.Animations.Add(new AnimationMapDef { Role = role, Clip = clip, Loop = role is AnimRole.Idle or AnimRole.Move or AnimRole.Control or AnimRole.Block });
        }
        return def;
    }

    private static readonly Dictionary<AnimRole, string[]> ConventionalNames = new()
    {
        [AnimRole.Idle] = new[] { "idle", "stand", "ожидание" },
        [AnimRole.Move] = new[] { "move", "walk", "run", "движение" },
        [AnimRole.Attack] = new[] { "attack", "strike", "атака" },
        [AnimRole.Skill] = new[] { "skill", "cast", "навык" },
        [AnimRole.Hit] = new[] { "hit", "hurt", "damage", "урон" },
        [AnimRole.Block] = new[] { "block", "defend", "guard", "блок" },
        [AnimRole.Control] = new[] { "stun", "control", "dizzy", "контроль" },
        [AnimRole.Death] = new[] { "death", "die", "смерть" },
        [AnimRole.Spawn] = new[] { "spawn", "appear", "появление" },
        [AnimRole.Turn] = new[] { "turn", "rotate", "поворот" },
        [AnimRole.Dodge] = new[] { "dodge", "evade", "roll", "уклонение" },
        [AnimRole.Fly] = new[] { "fly", "hover", "полёт" },
        [AnimRole.Burrow] = new[] { "burrow", "dig", "underground", "подкоп" },
        [AnimRole.Interact] = new[] { "interact", "use", "взаимодействие" },
        [AnimRole.Victory] = new[] { "victory", "emote", "cheer", "победа" },
    };

    /// <summary>A clip for a role by conventional names (idle, walk, attack, cast, hit, block, stun, death, spawn…).</summary>
    public static string? GuessClip(AnimRole role, IEnumerable<string> clips)
    {
        var list = clips.ToList();
        foreach (var n in ConventionalNames[role])
        {
            var exact = list.FirstOrDefault(c => string.Equals(c, n, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }
        foreach (var n in ConventionalNames[role])
        {
            var part = list.FirstOrDefault(c => c.Contains(n, StringComparison.OrdinalIgnoreCase));
            if (part != null) return part;
        }
        return null;
    }

    // ------------------------------------------------------------------ files of a content pack

    /// <summary>
    /// Checks the files of every resource (present, checksum, signature). Problems are warnings:
    /// the game falls back to the base geometry / silence and keeps running.
    /// </summary>
    public static List<ContentIssue> CheckFiles(ContentDatabase db, Func<string, byte[]?> read)
    {
        var issues = new List<ContentIssue>();
        foreach (var a in db.All<AssetDef>().Where(a => a.HasFile))
        {
            var problem = FileProblem(a, read(a.Path));
            if (problem != null)
                issues.Add(new ContentIssue(IssueSeverity.Warning, $"Asset:{a.Id}", "Path", problem + " — будет использовано базовое представление"));
        }
        return issues;
    }

    /// <summary>
    /// Replacing the file of a resource (ТЗ ч. 5, 7.5; docs/DECISIONS_5.md §89): the new file is checked as an import of the
    /// same kind; the resource keeps its ID, so every object that uses it gets the new file — those objects are listed before
    /// the change, with what changes (size, clips lost by the role mapping). Null and an error when the file is rejected.
    /// The old file stays in the pack (older versions of the resource may point to it).
    /// </summary>
    public static AssetReplacement? PrepareReplace(ContentDatabase db, AssetDef a, string fileName, byte[] bytes, Func<string, byte[]?> existing, out string? error)
    {
        var made = Import(db, fileName, bytes, existing, out error, a.Kind, a.Purpose);
        if (made == null) return null;
        if (made.Kind != a.Kind)
        {
            error = $"файл другого вида ({made.Kind}), а ресурс — {a.Kind}: замена не меняет вид ресурса";
            return null;
        }
        var r = new AssetReplacement { Target = a, Fresh = made, Dependents = ContentDependencies.UsedBy(db, a) };
        if (made.Sha256 == a.Sha256) r.Notes.Add("Файл тот же (контрольная сумма совпадает) — замена ничего не изменит.");
        if (a.Width > 0 && (made.Width != a.Width || made.Height != a.Height)) r.Notes.Add($"Размер изображения: {a.Width}×{a.Height} → {made.Width}×{made.Height}.");
        if (a.Triangles > 0 && made.Triangles != a.Triangles) r.Notes.Add($"Треугольников: {a.Triangles:N0} → {made.Triangles:N0}.");
        foreach (var m in a.Animations.Where(m => m.Clip != "" && !made.Clips.Contains(m.Clip)))
        {
            var guess = made.Animations.FirstOrDefault(x => x.Role == m.Role)?.Clip;
            r.Notes.Add($"Клипа «{m.Clip}» (роль {m.Role}) в новом файле нет" + (guess != null ? $" — будет «{guess}»." : " — роль будет анимироваться процедурно."));
        }
        if (r.Dependents.Count == 0) r.Notes.Add("Ресурс ещё ни к чему не назначен.");
        return r;
    }

    /// <summary>What is wrong with the file of a resource (null — fine).</summary>
    public static string? FileProblem(AssetDef a, byte[]? bytes)
    {
        if (!ContentPackage.IsSafePath(a.Path)) return $"недопустимый путь «{a.Path}»";
        if (bytes == null) return $"файл «{a.Path}» отсутствует";
        if (a.Sha256.Length > 0 && Sha256(bytes) != a.Sha256) return $"файл «{a.Path}» повреждён или заменён (контрольная сумма не совпадает)";
        var info = Check(bytes, a.Path);
        if (!info.Ok) return $"файл «{a.Path}»: {info.Error}";
        if (!Compatible(info.Kind!.Value, a.Kind)) return $"файл «{a.Path}» не подходит для вида «{a.Kind}»";
        return null;
    }
}

/// <summary>A prepared replacement of a resource file: what it changes and who uses the resource; <see cref="Apply"/> makes it.</summary>
public sealed class AssetReplacement
{
    public AssetDef Target { get; init; } = null!;
    /// <summary>The new file described as an import (its path in the pack, checksum, analysis).</summary>
    public AssetDef Fresh { get; init; } = null!;
    public List<Def> Dependents { get; init; } = new();
    public List<string> Notes { get; } = new();

    /// <summary>The file fields of the resource take those of the new file; the author's settings (scale, UV, roles that still exist, license) stay.</summary>
    public void Apply()
    {
        var a = Target;
        var f = Fresh;
        a.Path = f.Path;
        a.Sha256 = f.Sha256;
        a.Size = f.Size;
        a.SourceFile = f.SourceFile;
        a.ImportedAt = f.ImportedAt;
        a.Analysis = f.Analysis;
        a.Width = f.Width;
        a.Height = f.Height;
        a.HasAlpha = f.HasAlpha;
        a.Triangles = f.Triangles;
        a.Bones = f.Bones;
        a.Duration = f.Duration;
        a.Clips = new List<string>(f.Clips);
        a.ClipLengths = new List<double>(f.ClipLengths);
        // Roles: the author's mapping where the clip is still there, else the guess by the clip names.
        var kept = a.Animations.Where(m => m.Clip == "" || f.Clips.Contains(m.Clip)).ToList();
        kept.AddRange(f.Animations.Where(m => kept.All(k => k.Role != m.Role)));
        a.Animations = kept;
    }
}
