using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexTactics.Core.Content;

/// <summary>What a resource file contains and how it fits the limits (docs/DECISIONS_3.md §47).</summary>
public sealed class AssetAnalysis
{
    public string FileName { get; set; } = "";
    public string Format { get; set; } = "";
    public AssetKind? Kind { get; set; }
    public long Size { get; set; }
    // Images.
    public int Width { get; set; }
    public int Height { get; set; }
    public bool HasAlpha { get; set; }
    /// <summary>Only the size is wrong: the import window offers to scale the larger side down to this.</summary>
    public int ResizeTo { get; set; }
    public int BitDepth { get; set; }
    // Models.
    public int Meshes { get; set; }
    public int Triangles { get; set; }
    public int Materials { get; set; }
    public List<string> UnusedMaterials { get; } = new();
    public int Bones { get; set; }
    public int UnusedSkins { get; set; }
    public List<(string Name, double Seconds)> Animations { get; } = new();
    public List<string> MissingFiles { get; } = new();
    /// <summary>Files the model refers to (relative to it): copied into the pack with it.</summary>
    public List<string> ExternalFiles { get; } = new();
    public double[]? BoundsMin { get; set; }
    public double[]? BoundsMax { get; set; }
    /// <summary>The engine reads the geometry itself (FBX): triangles/bones are counted at the preview.</summary>
    public bool GeometryByEngine { get; set; }
    // Sounds.
    public double Duration { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }

    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> Notes { get; } = new();

    public bool Ok => Errors.Count == 0;

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Lines of the import window: name, type, format, size, dimensions, geometry, animations, sound.</summary>
    public List<string> Lines()
    {
        var l = new List<string> { $"Файл: {FileName}", $"Тип: {(Kind is { } k ? Names(k) : "не распознан")} · формат {Format}", $"Размер файла: {Mb(Size)}" };
        if (Width > 0) l.Add($"Изображение: {Width}×{Height} пикс.{(BitDepth > 0 ? $", {BitDepth} бит" : "")}, альфа-канал: {(HasAlpha ? "есть" : "нет")}");
        if (Kind == AssetKind.Model)
        {
            if (GeometryByEngine) l.Add("Геометрия: считается движком при предпросмотре (FBX)");
            else l.Add($"Геометрия: мешей {Meshes}, треугольников {Triangles.ToString("N0", Ru)}, материалов {Materials}, костей {Bones}");
            if (BoundsMin != null && BoundsMax != null)
                l.Add($"Габариты: {(BoundsMax[0] - BoundsMin[0]).ToString("0.##", Ru)} × {(BoundsMax[1] - BoundsMin[1]).ToString("0.##", Ru)} × {(BoundsMax[2] - BoundsMin[2]).ToString("0.##", Ru)} (X×Y×Z), низ на Y = {BoundsMin[1].ToString("0.##", Ru)}");
            l.Add(Animations.Count == 0 ? "Анимаций нет" : "Анимации: " + string.Join(", ", Animations.Select(a => $"{a.Name} ({a.Seconds.ToString("0.##", Ru)} с)")));
        }
        if (Kind == AssetKind.Sound && Duration > 0) l.Add($"Звук: {Duration.ToString("0.##", Ru)} с, {SampleRate} Гц, каналов {Channels}");
        return l;
    }

    public string Summary() => string.Join("; ", Lines().Skip(1));

    public static string Mb(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.##} МБ" : $"{bytes / 1024.0:0.#} КБ";

    private static string Names(AssetKind k) => k switch
    {
        AssetKind.Model => "модель",
        AssetKind.Texture => "текстура",
        AssetKind.Sprite => "спрайт",
        AssetKind.Icon => "иконка",
        AssetKind.Sound => "звук",
        _ => "материал",
    };
}

/// <summary>
/// Reads what the editor must show before a file enters the library (TZ §10.2, §11–13, §15): image dimensions and
/// alpha (PNG, JPEG, WebP, TGA, HDR, EXR), model geometry from glTF/GLB/OBJ (meshes, triangles, materials and
/// unused ones, bones, unbound skins, animations with durations, bounds — orientation and pivot, missing external
/// files), sound durations (WAV, OGG, MP3), and checks them against the configured limits. Pure parsing — no
/// engine, nothing is executed; a damaged file gives an error, never an exception.
/// </summary>
public static class AssetAnalyzer
{
    public static AssetAnalysis Analyze(byte[] bytes, string fileName, AssetLimitsDef limits, TexturePurpose purpose = TexturePurpose.Other, Func<string, bool>? exists = null)
    {
        var a = new AssetAnalysis { FileName = fileName, Size = bytes.LongLength };
        var info = AssetValidator.Check(bytes, fileName);
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        a.Format = ext.TrimStart('.').ToUpperInvariant();
        if (!info.Ok)
        {
            a.Errors.Add(info.Error!);
            return a;
        }
        a.Kind = info.Kind;
        try
        {
            switch (ext)
            {
                case ".png": Png(bytes, a); break;
                case ".jpg" or ".jpeg": Jpeg(bytes, a); break;
                case ".webp": Webp(bytes, a); break;
                case ".tga": Tga(bytes, a); break;
                case ".hdr": Hdr(bytes, a); break;
                case ".exr": Exr(bytes, a); break;
                case ".glb": Glb(bytes, a, exists); break;
                case ".gltf": Gltf(Encoding.UTF8.GetString(bytes), a, exists); break;
                case ".obj": Obj(Encoding.UTF8.GetString(bytes), a, exists); break;
                case ".fbx": Fbx(bytes, a); break;
                case ".wav": Wav(bytes, a); break;
                case ".ogg": Ogg(bytes, a); break;
                case ".mp3": Mp3(bytes, a); break;
            }
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or JsonException or FormatException or InvalidOperationException or OverflowException)
        {
            a.Errors.Add("Повреждён: заголовок не читается (" + e.GetType().Name + ")");
            return a;
        }
        Limits(a, limits, purpose, ext);
        return a;
    }

    // ------------------------------------------------------------------ limits

    private static void Limits(AssetAnalysis a, AssetLimitsDef lim, TexturePurpose purpose, string ext)
    {
        double mb = a.Size / 1048576.0;
        switch (a.Kind)
        {
            case AssetKind.Texture or AssetKind.Sprite or AssetKind.Icon:
            {
                if (mb > lim.MaxImageMb) a.Errors.Add($"Файл изображения {mb:0.#} МБ больше предела {lim.MaxImageMb:0} МБ");
                var t = lim.For(purpose);
                int side = Math.Max(a.Width, a.Height);
                if (a.Width <= 0 || a.Height <= 0) a.Errors.Add("Размеры изображения не определены");
                else if (side > t.Max)
                {
                    a.Errors.Add($"{a.Width}×{a.Height} больше максимума {t.Max}×{t.Max} для назначения «{AssetRequirements.PurposeName(purpose)}» — уменьшите при импорте или выберите другое назначение");
                    a.ResizeTo = t.Max;
                }
                else if (side > t.RecommendedMax * 2) a.Warnings.Add($"Изображение {a.Width}×{a.Height} чрезмерно большое для «{AssetRequirements.PurposeName(purpose)}» (рекомендуется {t.RecommendedMin}–{t.RecommendedMax})");
                else if (side < t.RecommendedMin / 2) a.Notes.Add($"Изображение меньше рекомендуемого ({t.RecommendedMin}–{t.RecommendedMax}) — может выглядеть размыто");
                if (a.Width > 0 && (!IsPow2(a.Width) || !IsPow2(a.Height))) a.Notes.Add("Размеры не степени двойки — mipmaps и повторение могут работать хуже");
                if (t.NeedsAlpha && ext is ".jpg" or ".jpeg") a.Warnings.Add("JPEG не хранит прозрачность — для иконок и спрайтов используйте PNG");
                else if (t.NeedsAlpha && !a.HasAlpha) a.Warnings.Add("У изображения нет альфа-канала, а назначению нужна прозрачность");
                if (ext is ".hdr" or ".exr") a.Notes.Add("HDR/EXR — только как текстура высокого динамического диапазона; если в сборке нет загрузчика, будет явная ошибка при загрузке");
                break;
            }
            case AssetKind.Model:
                if (mb > lim.MaxModelMb) a.Errors.Add($"Модель {mb:0.#} МБ больше предела {lim.MaxModelMb:0} МБ");
                else if (mb > lim.WarnModelMb) a.Warnings.Add($"Модель {mb:0.#} МБ — больше {lim.WarnModelMb:0} МБ, загрузка будет долгой");
                if (a.Triangles > lim.WarnTriangles) a.Warnings.Add($"Треугольников {a.Triangles:N0} — больше {lim.WarnTriangles:N0}: чрезмерная сложность для персонажа");
                if (a.Bones > lim.WarnBones) a.Warnings.Add($"Костей {a.Bones} — больше {lim.WarnBones}");
                foreach (var (name, sec) in a.Animations.Where(x => x.Seconds > lim.WarnAnimationSeconds))
                    a.Warnings.Add($"Анимация «{name}» длится {sec:0.#} с — больше {lim.WarnAnimationSeconds:0} с");
                if (a.UnusedMaterials.Count > 0) a.Warnings.Add("Неиспользуемые материалы: " + string.Join(", ", a.UnusedMaterials));
                if (a.UnusedSkins > 0) a.Warnings.Add($"Скелетов без привязки к узлу: {a.UnusedSkins} — их кости не используются");
                if (a.MissingFiles.Count > 0) a.Errors.Add("Отсутствуют файлы, на которые ссылается модель: " + string.Join(", ", a.MissingFiles));
                if (ext == ".obj") a.Notes.Add("OBJ — статичная геометрия: скелетов и анимаций нет");
                if (a.BoundsMin != null && a.BoundsMax != null)
                {
                    double w = a.BoundsMax[0] - a.BoundsMin[0], h = a.BoundsMax[1] - a.BoundsMin[1], d = a.BoundsMax[2] - a.BoundsMin[2];
                    double cx = (a.BoundsMax[0] + a.BoundsMin[0]) / 2, cz = (a.BoundsMax[2] + a.BoundsMin[2]) / 2;
                    double size = Math.Max(w, Math.Max(h, d));
                    if (size <= 0) break;
                    if (h < 0.35 * Math.Max(w, d)) a.Warnings.Add("Модель, возможно, лежит: высота по Y заметно меньше ширины (в glTF верх — +Y)");
                    if (Math.Abs(a.BoundsMin[1]) > 0.1 * size) a.Warnings.Add($"Точка опоры не у основания: низ модели на Y = {a.BoundsMin[1]:0.##} (поправьте «смещение по высоте»)");
                    if (Math.Abs(cx) > 0.25 * size || Math.Abs(cz) > 0.25 * size) a.Warnings.Add("Центр модели смещён от точки опоры по горизонтали");
                    if (size > 50 || size < 0.01) a.Notes.Add($"Габарит {size:0.###} — проверьте единицы (масштаб в ресурсе)");
                }
                break;
            case AssetKind.Sound:
                if (mb > lim.MaxSoundMb) a.Errors.Add($"Звук {mb:0.#} МБ больше предела {lim.MaxSoundMb:0} МБ");
                if (a.Duration > lim.WarnSoundSeconds) a.Warnings.Add($"Звук длится {a.Duration:0.#} с — для эффектов желательно до {lim.WarnSoundSeconds:0} с");
                break;
        }
    }

    private static bool IsPow2(int v) => v > 0 && (v & (v - 1)) == 0;

    // ------------------------------------------------------------------ images

    private static int Be16(byte[] b, int i) => (b[i] << 8) | b[i + 1];
    private static int Be32(byte[] b, int i) => (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
    private static int Le16(byte[] b, int i) => b[i] | (b[i + 1] << 8);
    private static int Le24(byte[] b, int i) => b[i] | (b[i + 1] << 8) | (b[i + 2] << 16);
    private static int Le32(byte[] b, int i) => BitConverter.ToInt32(b, i);

    private static void Png(byte[] b, AssetAnalysis a)
    {
        a.Width = Be32(b, 16);
        a.Height = Be32(b, 20);
        a.BitDepth = b[24];
        int colorType = b[25];
        a.HasAlpha = colorType is 4 or 6;
        // tRNS: transparency of palette / grey / truecolour images.
        int i = 8;
        while (i + 8 <= b.Length)
        {
            int len = Be32(b, i);
            string type = Encoding.ASCII.GetString(b, i + 4, 4);
            if (type == "tRNS") a.HasAlpha = true;
            if (type == "IEND" || len < 0) break;
            i += 12 + len;
        }
    }

    private static void Jpeg(byte[] b, AssetAnalysis a)
    {
        int i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF)
            {
                i++;
                continue;
            }
            int m = b[i + 1];
            if (m is 0xD8 or 0x01 || m is >= 0xD0 and <= 0xD7)
            {
                i += 2;
                continue;
            }
            int len = Be16(b, i + 2);
            if (m is >= 0xC0 and <= 0xCF && m is not (0xC4 or 0xC8 or 0xCC))
            {
                a.BitDepth = b[i + 4];
                a.Height = Be16(b, i + 5);
                a.Width = Be16(b, i + 7);
                return;
            }
            i += 2 + len;
        }
        throw new FormatException("нет кадра JPEG");
    }

    private static void Webp(byte[] b, AssetAnalysis a)
    {
        string chunk = Encoding.ASCII.GetString(b, 12, 4);
        switch (chunk)
        {
            case "VP8X":
                a.HasAlpha = (b[20] & 0x10) != 0;
                a.Width = Le24(b, 24) + 1;
                a.Height = Le24(b, 27) + 1;
                break;
            case "VP8L":
            {
                uint bits = BitConverter.ToUInt32(b, 21);
                a.Width = (int)(bits & 0x3FFF) + 1;
                a.Height = (int)((bits >> 14) & 0x3FFF) + 1;
                a.HasAlpha = ((bits >> 28) & 1) != 0;
                break;
            }
            default:
                a.Width = Le16(b, 26) & 0x3FFF;
                a.Height = Le16(b, 28) & 0x3FFF;
                break;
        }
    }

    private static void Tga(byte[] b, AssetAnalysis a)
    {
        a.Width = Le16(b, 12);
        a.Height = Le16(b, 14);
        a.BitDepth = b[16];
        a.HasAlpha = b[16] == 32 || (b[17] & 0x0F) > 0;
    }

    private static void Hdr(byte[] b, AssetAnalysis a)
    {
        var text = Encoding.ASCII.GetString(b, 0, Math.Min(b.Length, 4096));
        foreach (var line in text.Split('\n'))
        {
            var p = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 4 && (p[0] is "-Y" or "+Y") && (p[2] is "+X" or "-X"))
            {
                a.Height = int.Parse(p[1], CultureInfo.InvariantCulture);
                a.Width = int.Parse(p[3], CultureInfo.InvariantCulture);
                a.BitDepth = 32;
                return;
            }
        }
        throw new FormatException("нет строки разрешения HDR");
    }

    private static void Exr(byte[] b, AssetAnalysis a)
    {
        int i = 8;
        while (i < b.Length && b[i] != 0)
        {
            string name = ReadZ(b, ref i);
            string type = ReadZ(b, ref i);
            int size = Le32(b, i);
            i += 4;
            if (name == "dataWindow" && type == "box2i")
            {
                int x0 = Le32(b, i), y0 = Le32(b, i + 4), x1 = Le32(b, i + 8), y1 = Le32(b, i + 12);
                a.Width = x1 - x0 + 1;
                a.Height = y1 - y0 + 1;
            }
            if (name == "channels" && type == "chlist")
            {
                int j = i;
                while (j < i + size && b[j] != 0)
                {
                    string ch = ReadZ(b, ref j);
                    if (ch == "A") a.HasAlpha = true;
                    j += 16;
                }
            }
            i += size;
        }
        a.BitDepth = 16;
        if (a.Width <= 0) throw new FormatException("нет dataWindow");
    }

    private static string ReadZ(byte[] b, ref int i)
    {
        int s = i;
        while (b[i] != 0) i++;
        var r = Encoding.ASCII.GetString(b, s, i - s);
        i++;
        return r;
    }

    // ------------------------------------------------------------------ models

    private static void Glb(byte[] b, AssetAnalysis a, Func<string, bool>? exists)
    {
        int chunkLength = Le32(b, 12);
        Gltf(Encoding.UTF8.GetString(b, 20, chunkLength), a, exists);
    }

    private static void Gltf(string json, AssetAnalysis a, Func<string, bool>? exists)
    {
        var root = JsonNode.Parse(json.TrimEnd('\0', ' ')) as JsonObject ?? throw new FormatException("пустой glTF");
        var accessors = root["accessors"] as JsonArray ?? new JsonArray();
        int Count(int? idx) => idx is { } i && i >= 0 && i < accessors.Count ? (int?)accessors[i]?["count"] ?? 0 : 0;
        var used = new HashSet<int>();
        double[]? min = null, max = null;
        if (root["meshes"] is JsonArray meshes)
        {
            a.Meshes = meshes.Count;
            foreach (var m in meshes)
            foreach (var p in m?["primitives"] as JsonArray ?? new JsonArray())
            {
                if (p == null) continue;
                int mode = (int?)p["mode"] ?? 4;
                int n = p["indices"] != null ? Count((int?)p["indices"]) : Count((int?)p["attributes"]?["POSITION"]);
                a.Triangles += mode switch { 4 => n / 3, 5 or 6 => Math.Max(0, n - 2), _ => 0 };
                if ((int?)p["material"] is { } mat) used.Add(mat);
                if ((int?)p["attributes"]?["POSITION"] is { } pos && pos < accessors.Count && accessors[pos] is JsonObject acc
                    && acc["min"] is JsonArray mn && acc["max"] is JsonArray mx && mn.Count >= 3 && mx.Count >= 3)
                {
                    min ??= new[] { double.MaxValue, double.MaxValue, double.MaxValue };
                    max ??= new[] { double.MinValue, double.MinValue, double.MinValue };
                    for (int k = 0; k < 3; k++)
                    {
                        min[k] = Math.Min(min[k], (double?)mn[k] ?? 0);
                        max[k] = Math.Max(max[k], (double?)mx[k] ?? 0);
                    }
                }
            }
        }
        a.BoundsMin = min;
        a.BoundsMax = max;
        if (root["materials"] is JsonArray materials)
        {
            a.Materials = materials.Count;
            for (int i = 0; i < materials.Count; i++)
                if (!used.Contains(i))
                    a.UnusedMaterials.Add((string?)materials[i]?["name"] is { Length: > 0 } n ? n : $"material_{i}");
        }
        if (root["skins"] is JsonArray skins)
        {
            var joints = new HashSet<int>();
            var boundSkins = new HashSet<int>();
            foreach (var node in root["nodes"] as JsonArray ?? new JsonArray())
                if ((int?)node?["skin"] is { } s)
                    boundSkins.Add(s);
            for (int i = 0; i < skins.Count; i++)
            {
                foreach (var j in skins[i]?["joints"] as JsonArray ?? new JsonArray())
                    if ((int?)j is { } ji)
                        joints.Add(ji);
                if (!boundSkins.Contains(i)) a.UnusedSkins++;
            }
            a.Bones = joints.Count;
        }
        if (root["animations"] is JsonArray anims)
            for (int i = 0; i < anims.Count; i++)
            {
                double len = 0;
                foreach (var s in anims[i]?["samplers"] as JsonArray ?? new JsonArray())
                    if ((int?)s?["input"] is { } inp && inp < accessors.Count && accessors[inp]?["max"] is JsonArray mx && mx.Count > 0)
                        len = Math.Max(len, (double?)mx[0] ?? 0);
                a.Animations.Add(((string?)anims[i]?["name"] is { Length: > 0 } n ? n : $"animation_{i}", len));
            }
        foreach (var list in new[] { "images", "buffers" })
            foreach (var item in root[list] as JsonArray ?? new JsonArray())
            {
                var uri = (string?)item?["uri"];
                if (uri == null || uri.StartsWith("data:", StringComparison.Ordinal)) continue;
                var rel = Uri.UnescapeDataString(uri);
                a.ExternalFiles.Add(rel);
                if (exists != null && !exists(rel)) a.MissingFiles.Add(rel);
            }
    }

    private static void Obj(string text, AssetAnalysis a, Func<string, bool>? exists)
    {
        var mats = new HashSet<string>();
        double[] min = { double.MaxValue, double.MaxValue, double.MaxValue }, max = { double.MinValue, double.MinValue, double.MinValue };
        int verts = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("v ", StringComparison.Ordinal))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 4) continue;
                verts++;
                for (int k = 0; k < 3; k++)
                {
                    double v = double.Parse(p[k + 1], CultureInfo.InvariantCulture);
                    min[k] = Math.Min(min[k], v);
                    max[k] = Math.Max(max[k], v);
                }
            }
            else if (line.StartsWith("f ", StringComparison.Ordinal))
            {
                int n = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length - 1;
                a.Triangles += Math.Max(0, n - 2);
            }
            else if (line.StartsWith("usemtl ", StringComparison.Ordinal)) mats.Add(line[7..].Trim());
            else if (line.StartsWith("mtllib ", StringComparison.Ordinal))
            {
                var file = line[7..].Trim();
                a.ExternalFiles.Add(file);
                if (exists != null && !exists(file)) a.MissingFiles.Add(file);
            }
            else if (line.StartsWith("o ", StringComparison.Ordinal) || line.StartsWith("g ", StringComparison.Ordinal)) a.Meshes++;
        }
        if (verts == 0) throw new FormatException("нет вершин");
        a.Meshes = Math.Max(1, a.Meshes);
        a.Materials = mats.Count;
        a.BoundsMin = min;
        a.BoundsMax = max;
    }

    private static void Fbx(byte[] b, AssetAnalysis a)
    {
        a.GeometryByEngine = true;
        if (Encoding.ASCII.GetString(b, 0, Math.Min(b.Length, 18)) == "Kaydara FBX Binary")
            a.Notes.Add($"FBX (двоичный), версия {Le32(b, 23)}; треугольники, кости и анимации — после загрузки движком (предпросмотр)");
        else
            a.Notes.Add("FBX (текстовый); треугольники, кости и анимации — после загрузки движком (предпросмотр)");
    }

    // ------------------------------------------------------------------ sounds

    private static void Wav(byte[] b, AssetAnalysis a)
    {
        int i = 12, byteRate = 0;
        while (i + 8 <= b.Length)
        {
            string id = Encoding.ASCII.GetString(b, i, 4);
            int size = Le32(b, i + 4);
            if (id == "fmt ")
            {
                a.Channels = Le16(b, i + 10);
                a.SampleRate = Le32(b, i + 12);
                byteRate = Le32(b, i + 16);
            }
            else if (id == "data" && byteRate > 0)
            {
                a.Duration = Math.Min(size, b.Length - i - 8) / (double)byteRate;
                return;
            }
            if (size < 0) break;
            i += 8 + size + (size & 1);
        }
    }

    private static void Ogg(byte[] b, AssetAnalysis a)
    {
        // Identification header: "\x01vorbis", version, channels, rate.
        int v = IndexOf(b, Encoding.ASCII.GetBytes("vorbis"), 0);
        if (v > 0)
        {
            a.Channels = b[v + 10];
            a.SampleRate = Le32(b, v + 11);
        }
        // The last page's granule position = total samples.
        int last = -1;
        for (int i = b.Length - 4; i >= 0; i--)
            if (b[i] == 'O' && b[i + 1] == 'g' && b[i + 2] == 'g' && b[i + 3] == 'S')
            {
                last = i;
                break;
            }
        if (last >= 0 && last + 14 <= b.Length && a.SampleRate > 0)
            a.Duration = BitConverter.ToInt64(b, last + 6) / (double)a.SampleRate;
    }

    private static readonly int[] Mp3Bitrates = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
    private static readonly int[] Mp3Rates = { 44100, 48000, 32000 };

    private static void Mp3(byte[] b, AssetAnalysis a)
    {
        int i = 0;
        if (b.Length > 10 && b[0] == 'I' && b[1] == 'D' && b[2] == '3')
            i = 10 + ((b[6] & 0x7F) << 21 | (b[7] & 0x7F) << 14 | (b[8] & 0x7F) << 7 | (b[9] & 0x7F));
        for (; i + 4 < b.Length; i++)
        {
            if (b[i] != 0xFF || (b[i + 1] & 0xE0) != 0xE0) continue;
            int br = (b[i + 2] >> 4) & 0x0F, sr = (b[i + 2] >> 2) & 0x03;
            if (br == 0 || br >= 15 || sr >= 3) continue;
            a.SampleRate = Mp3Rates[sr];
            a.Channels = ((b[i + 3] >> 6) & 3) == 3 ? 1 : 2;
            a.Duration = (b.Length - i) * 8.0 / (Mp3Bitrates[br] * 1000.0);
            a.Notes.Add("Длительность MP3 оценена по битрейту первого кадра");
            return;
        }
    }

    private static int IndexOf(byte[] b, byte[] what, int from)
    {
        for (int i = from; i + what.Length <= b.Length; i++)
        {
            int k = 0;
            while (k < what.Length && b[i + k] == what[k]) k++;
            if (k == what.Length) return i;
        }
        return -1;
    }
}
