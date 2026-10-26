using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.Render;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>
/// The import window (TZ §16, docs/DECISIONS_3.md §47): what the file is (name, type, format, size, dimensions,
/// triangles, materials, bones, animations, sound), errors, warnings and advice against the configured limits, a
/// preview, the purpose of a texture, the name and path in the pack, the reduction of a too-large image, and where to
/// assign the new resource. «Проверить файл» shows the same window without creating anything.
/// </summary>
public static class AssetImportDialog
{
    public const string Title = "Импорт ресурса";
    public const string CheckTitle = "Проверка файла";

    /// <summary>Reads a file from disk and opens the window; checkOnly — only the analysis.</summary>
    public static void Open(Node parent, EditorContext ctx, string path, Action<AssetDef>? created, bool checkOnly = false)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e)
        {
            Dialogs.Info(parent, Title, e.Message);
            return;
        }
        Show(parent, ctx, Path.GetFileName(path), bytes, created, checkOnly, Path.GetDirectoryName(path));
    }

    /// <param name="sourceDir">Where the file came from: the files a model refers to are looked up (and copied) there.</param>
    public static void Show(Node parent, EditorContext ctx, string fileName, byte[] original, Action<AssetDef>? created, bool checkOnly = false, string? sourceDir = null)
    {
        bool Exists(string rel) => sourceDir != null && ContentPackage.IsSafePath(rel) && File.Exists(Path.Combine(sourceDir, rel));
        var limits = ctx.Db.Rules.Assets;
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        var kind = AssetValidator.KindByExtension(fileName);
        bool image = kind is AssetKind.Texture or AssetKind.Sprite or AssetKind.Icon;
        var purpose = kind == AssetKind.Icon ? TexturePurpose.SkillIcon : TexturePurpose.Other;

        var root = Ui.HBox(12);
        var left = Ui.VBox(6).MinSize(470);
        root.AddChild(left);
        var right = Ui.VBox(6).MinSize(330);
        root.AddChild(right);

        var info = Ui.Label("", 13, Ui.Text, wrap: true);
        info.Name = "ImportInfo";
        var issues = Ui.VBox(3);
        issues.Name = "ImportIssues";
        left.AddChild(info);
        left.AddChild(issues);
        left.AddChild(Ui.Sep());

        // Purpose (textures): decides the recommended and the maximum size.
        var purposeBox = new OptionButton { Name = "ImportPurpose" };
        foreach (var p in Enum.GetValues<TexturePurpose>()) purposeBox.AddItem(AssetRequirements.PurposeName(p), (int)p);
        purposeBox.Select((int)purpose);
        var resize = new CheckBox { Name = "ImportResize", Text = "Уменьшить до максимума при импорте (PNG)", Visible = false };
        if (image) left.AddChild(Ui.HBox(6, Ui.Label("Назначение", 13, Ui.TextDim).MinSize(130), purposeBox.Expand()));
        left.AddChild(resize);

        string baseName = ContentIO.SafeFileName(Path.GetFileNameWithoutExtension(fileName)).ToLowerInvariant();
        var nameEdit = new LineEdit { Name = "ImportName", Text = Path.GetFileNameWithoutExtension(fileName), PlaceholderText = "Отображаемое имя" };
        var fileEdit = new LineEdit { Name = "ImportFile", Text = baseName, PlaceholderText = "имя файла в пакете (латиница, цифры, _ и -)" };
        var pathLabel = Ui.Label("", 12, Ui.TextDim, wrap: true);
        // ТЗ ч. 5, 7.5: optional — where the file comes from and on what terms (§89).
        var licenseEdit = new LineEdit { Name = "ImportLicense", PlaceholderText = "необязательно: CC0, CC-BY 4.0 — автор…, собственная работа" };
        var assign = new OptionButton { Name = "ImportAssign" };
        // The same file already in the content (by checksum): offered instead of a duplicate.
        var sha = AssetValidator.Sha256(original);
        var same = ctx.Db.All<AssetDef>().FirstOrDefault(x => x.Sha256 == sha && x.HasFile);
        var reuse = new CheckBox { Name = "ImportReuse", Text = same == null ? "" : $"Такой файл уже есть — взять существующий ресурс «{same.DisplayName}» [{same.Id}]", ButtonPressed = same != null, Visible = same != null };
        var targets = kind is { } k ? AssetBinding.Targets(ctx.Db, k) : new List<AssetBinding.Target>();
        assign.AddItem("— не назначать —", 0);
        for (int i = 0; i < targets.Count; i++)
            assign.AddItem(targets[i].Label + (string.IsNullOrEmpty(targets[i].Get()) ? "" : " (заменит текущий)"), i + 1);
        if (!checkOnly)
        {
            left.AddChild(Ui.HBox(6, Ui.Label("Имя ресурса", 13, Ui.TextDim).MinSize(130), nameEdit.Expand()));
            left.AddChild(Ui.HBox(6, Ui.Label("Файл в пакете", 13, Ui.TextDim).MinSize(130), fileEdit.Expand()));
            left.AddChild(pathLabel);
            left.AddChild(Ui.HBox(6, Ui.Label("Лицензия", 13, Ui.TextDim).MinSize(130), licenseEdit.Expand()));
            left.AddChild(Ui.HBox(6, Ui.Label("Назначить объекту", 13, Ui.TextDim).MinSize(130), assign.Expand()));
            left.AddChild(reuse);
        }

        // Preview: the image, or the model in a small stage with what the engine counted (FBX geometry).
        var engine = Ui.Label("", 12, Ui.TextDim, wrap: true);
        engine.Name = "ImportEngineStats";
        void BuildPreview(byte[] bytes)
        {
            Ui.ClearChildren(right);
            right.AddChild(Ui.Label("Предпросмотр", 14, Ui.Accent));
            if (image)
            {
                var img = Assets.LoadImage(bytes, bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50 ? ".png" : ext, out var problem);
                if (img == null)
                {
                    right.AddChild(Ui.Label("⚠ " + problem, 12, Ui.Warn, wrap: true));
                    return;
                }
                var tr = new TextureRect
                {
                    Name = "ImportPreviewImage", Texture = ImageTexture.CreateFromImage(img), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = new Vector2(320, 280),
                };
                right.AddChild(tr);
                return;
            }
            if (kind == AssetKind.Model)
            {
                var stage = new PreviewStage { Name = "ImportPreviewStage" };
                right.AddChild(stage);
                var model = Assets.ModelFromBytes(bytes, ext, sourceDir ?? Path.GetTempPath(), out var problem);
                if (model == null)
                {
                    right.AddChild(Ui.Label("⚠ " + problem + " — в игре будет базовая фишка", 12, Ui.Warn, wrap: true));
                    return;
                }
                var (tris, bones, clips) = Assets.EngineStats(model);
                engine.Text = $"Движок прочитал: треугольников {tris:N0}, костей {bones}" +
                              (clips.Count > 0 ? ", анимации: " + string.Join(", ", clips.Select(c => $"{c.Name} ({c.Seconds:0.##} с)")) : ", анимаций нет");
                if (tris > limits.WarnTriangles) engine.Text += $"\n⚠ больше {limits.WarnTriangles:N0} треугольников";
                right.AddChild(engine);
                // Fit the model into the preview by its bounds.
                var holder = new Node3D();
                holder.AddChild(model);
                stage.Root.AddChild(holder);
                var aabb = Bounds(model);
                float size = Math.Max(aabb.Size.X, Math.Max(aabb.Size.Y, aabb.Size.Z));
                if (size > 0.0001f)
                {
                    holder.Scale = Vector3.One * (1.2f / size);
                    holder.Position = -new Vector3(aabb.GetCenter().X, aabb.Position.Y, aabb.GetCenter().Z) * (1.2f / size);
                }
                return;
            }
            if (kind == AssetKind.Sound)
                right.AddChild(Ui.Button("▶ Прослушать", () =>
                {
                    var stream = SoundFromBytes(bytes, ext);
                    if (stream == null) return;
                    var p = new AudioStreamPlayer { Stream = stream };
                    right.AddChild(p);
                    p.Finished += p.QueueFree;
                    p.Play();
                }));
        }

        byte[] current = original;
        string currentName = fileName;
        AssetAnalysis an = AssetAnalyzer.Analyze(original, fileName, limits, purpose, Exists);
        void Refresh()
        {
            purpose = (TexturePurpose)purposeBox.GetSelectedId();
            current = original;
            currentName = fileName;
            an = AssetAnalyzer.Analyze(original, fileName, limits, purpose, Exists);
            resize.Visible = image && an.ResizeTo > 0 && an.Errors.Count == 1;
            if (resize.Visible && resize.ButtonPressed && Reduced(original, ext, an.ResizeTo) is { } small)
            {
                current = small;
                currentName = Path.GetFileNameWithoutExtension(fileName) + ".png";
                an = AssetAnalyzer.Analyze(current, currentName, limits, purpose);
                an.Notes.Insert(0, $"Уменьшено до {an.Width}×{an.Height} и сохранено в PNG");
            }
            info.Text = string.Join("\n", an.Lines());
            Ui.ClearChildren(issues);
            foreach (var e in an.Errors) issues.AddChild(Ui.Label("✗ " + e, 12, Ui.Bad, wrap: true));
            foreach (var w in an.Warnings) issues.AddChild(Ui.Label("⚠ " + w, 12, Ui.Warn, wrap: true));
            foreach (var n in an.Notes) issues.AddChild(Ui.Label("ℹ " + n, 12, Ui.TextDim, wrap: true));
            if (an.Ok && an.Warnings.Count == 0) issues.AddChild(Ui.Label("✓ Файл соответствует требованиям", 12, Ui.Good));
            string safe = ContentIO.SafeFileName(fileEdit.Text.Trim()).ToLowerInvariant();
            pathLabel.Text = kind == null ? "" : $"Будет сохранён как assets/…/{(safe.Length == 0 ? "asset" : safe)}{Path.GetExtension(currentName).ToLowerInvariant()} (относительный путь; совпадающий файл не дублируется)";
            BuildPreview(current);
        }
        purposeBox.ItemSelected += _ => Refresh();
        resize.Toggled += _ => Refresh();
        fileEdit.TextChanged += _ => Refresh();
        assign.ItemSelected += i =>
        {
            // The purpose that suits the target (a hex top → «поверхность гекса», a skill icon → «иконка навыка»).
            if (i > 0 && targets[(int)i - 1].Purpose is { } tp && image)
            {
                purposeBox.Select((int)tp);
                Refresh();
            }
        };
        Refresh();

        var dlg = Dialogs.Custom(parent, checkOnly ? CheckTitle : Title, root, checkOnly ? "Закрыть" : "Создать ресурс", () =>
        {
            if (checkOnly) return true;
            if (!an.Ok)
            {
                issues.AddChild(Ui.Label("Ресурс не создан: исправьте ошибки выше (или выберите другое назначение).", 12, Ui.Bad, wrap: true));
                return false;
            }
            int t = assign.GetSelectedId();
            var target = t > 0 ? targets[t - 1] : null;
            // An image takes the kind of its target (a skill's icon), else of its purpose.
            var asKind = target?.Kind ?? (image && purpose is TexturePurpose.SkillIcon or TexturePurpose.StatusIcon ? AssetKind.Icon : kind);
            string safe = ContentIO.SafeFileName(fileEdit.Text.Trim()).ToLowerInvariant();
            string storeName = (safe.Length == 0 ? "asset" : safe) + Path.GetExtension(currentName).ToLowerInvariant();
            AssetDef def;
            if (same != null && reuse.ButtonPressed && current == original) def = same;
            else
            {
                var made = AssetValidator.Import(ctx.Db, storeName, current, Assets.Read, out var error, asKind, image ? purpose : null);
                if (made == null)
                {
                    issues.AddChild(Ui.Label("✗ " + error, 12, Ui.Bad, wrap: true));
                    return false;
                }
                def = made;
                def.SourceFile = fileName;
                if (nameEdit.Text.Trim().Length > 0) def.Name = nameEdit.Text.Trim();
                def.License = licenseEdit.Text.Trim();
                try
                {
                    var full = Path.Combine(App.Main.Instance.Content.UserRoot, def.Path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    if (!File.Exists(full)) File.WriteAllBytes(full, current);
                    // The model's own files (textures, buffers, .mtl) — next to it in the pack, by the same relative path.
                    foreach (var rel in an.ExternalFiles.Where(Exists).Distinct())
                    {
                        var dst = Path.Combine(Path.GetDirectoryName(full)!, rel.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        if (!File.Exists(dst)) File.Copy(Path.Combine(sourceDir!, rel), dst);
                    }
                }
                catch (Exception e)
                {
                    issues.AddChild(Ui.Label("✗ Файл не записан: " + e.Message, 12, Ui.Bad, wrap: true));
                    return false;
                }
                ctx.Db.Add(def);
            }
            target?.Apply(ctx.Db, def);
            ctx.MarkDirty();
            created?.Invoke(def);
            return true;
        }, new Vector2I(900, 0));
        dlg.Name = checkOnly ? "AssetCheckDialog" : "AssetImportDialog";
    }

    /// <summary>Scales the larger side down to max and stores the image as PNG.</summary>
    private static byte[]? Reduced(byte[] bytes, string ext, int max)
    {
        var img = Assets.LoadImage(bytes, ext, out _);
        if (img == null) return null;
        int w = img.GetWidth(), h = img.GetHeight();
        float k = max / (float)Math.Max(w, h);
        if (k >= 1) return null;
        img.Resize(Math.Max(1, (int)(w * k)), Math.Max(1, (int)(h * k)), Image.Interpolation.Lanczos);
        return img.SavePngToBuffer();
    }

    private static AudioStream? SoundFromBytes(byte[] bytes, string ext) => ext switch
    {
        ".ogg" => AudioStreamOggVorbis.LoadFromBuffer(bytes),
        ".mp3" => AudioStreamMP3.LoadFromBuffer(bytes),
        ".wav" => AudioStreamWav.LoadFromBuffer(bytes),
        _ => null,
    };

    private static Aabb Bounds(Node n)
    {
        Aabb? box = null;
        void Walk(Node x, Transform3D t)
        {
            var tt = x is Node3D n3 ? t * n3.Transform : t;
            if (x is VisualInstance3D vi)
            {
                var b = tt * vi.GetAabb();
                box = box is { } bb ? bb.Merge(b) : b;
            }
            foreach (var c in x.GetChildren()) Walk(c, tt);
        }
        Walk(n, Transform3D.Identity);
        return box ?? new Aabb(Vector3.Zero, Vector3.One);
    }
}
