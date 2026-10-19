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
    public const long MaxSize = 64L * 1024 * 1024;

    private static readonly Dictionary<string, AssetKind> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".glb"] = AssetKind.Model, [".gltf"] = AssetKind.Model,
        [".png"] = AssetKind.Texture, [".jpg"] = AssetKind.Texture, [".jpeg"] = AssetKind.Texture, [".webp"] = AssetKind.Texture,
        [".ogg"] = AssetKind.Sound, [".wav"] = AssetKind.Sound,
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
            ".glb" => CheckGlb(bytes, info),
            ".gltf" => CheckGltfJson(Encoding.UTF8.GetString(bytes), info),
            _ => "Неизвестный формат",
        };
        if (info.Ok) info.Kind = kind;
        return info;
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
    public static AssetDef? Import(ContentDatabase db, string fileName, byte[] bytes, Func<string, byte[]?> existing, out string? error, AssetKind? asKind = null)
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
        };
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
