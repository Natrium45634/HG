using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using HexTactics.Core.Content;
using HexTactics.Game.App;
using HexTactics.Game.Render;
using HexTactics.Game.UI;

namespace HexTactics.Game.Editor;

/// <summary>A small 3D stage for previews (own world, light, slowly orbiting camera).</summary>
public partial class PreviewStage : SubViewportContainer
{
    public Node3D Root { get; } = new() { Name = "PreviewRoot" };
    private readonly Camera3D _camera = new() { Fov = 40 };
    private float _yaw;

    public PreviewStage()
    {
        Stretch = true;
        CustomMinimumSize = new Vector2(360, 300);
        var vp = new SubViewport { OwnWorld3D = true, TransparentBg = false, Msaa3D = Viewport.Msaa.Msaa4X, Size = new Vector2I(360, 300) };
        AddChild(vp);
        var env = new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.1f, 0.11f, 0.13f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.55f, 0.58f, 0.65f), AmbientLightEnergy = 0.7f,
            },
        };
        vp.AddChild(env);
        vp.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50, 35, 0), LightEnergy = 1.2f, ShadowEnabled = true });
        var floor = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 1.0f, BottomRadius = 1.0f, Height = 0.06f, RadialSegments = 6 },
            MaterialOverride = Mats.Solid(new Color(0.22f, 0.24f, 0.28f), 0.7f), Position = new Vector3(0, -0.03f, 0), RotationDegrees = new Vector3(0, 30, 0),
        };
        vp.AddChild(floor);
        vp.AddChild(Root);
        vp.AddChild(_camera);
    }

    public void Clear() => Ui.ClearChildren(Root);

    public override void _Process(double delta)
    {
        _yaw += (float)delta * 0.4f;
        _camera.Position = new Vector3(Mathf.Sin(_yaw) * 2.6f, 1.6f, Mathf.Cos(_yaw) * 2.6f);
        _camera.LookAt(new Vector3(0, 0.45f, 0), Vector3.Up);
    }
}

/// <summary>
/// «Ресурсы» (docs/DECISIONS_2.md §34): import of models, textures, sprites, icons and sounds with
/// the file check (format white list, signature, size, safe glTF references — no code), materials,
/// a preview with the animation roles of a model, and the list of problems (missing/damaged files).
/// </summary>
public partial class AssetPanel : DefListPanel
{
    private PreviewStage? _stage;
    private Label? _problem;
    private Label? _stats;

    public AssetPanel(EditorContext ctx) : base(ctx, DefCategory.Asset, "Ресурсы (модели, текстуры, звуки, материалы)", null,
        () => new AssetDef { Name = "Новый материал", Kind = AssetKind.Material })
    {
    }

    public const string Formats = "*.glb, *.gltf, *.fbx, *.obj, *.png, *.jpg, *.jpeg, *.webp, *.tga, *.hdr, *.exr, *.ogg, *.wav, *.mp3 ; Ресурсы";

    public override void _Ready()
    {
        base._Ready();
        var importButton = Ui.Button("Импорт файла…", ImportFile, "glTF/GLB, FBX, OBJ, PNG/JPEG/WebP/TGA/HDR/EXR, OGG/WAV/MP3 — окно импорта: анализ, требования, назначение");
        importButton.Name = "ImportAssetFile";
        var checkButton = Ui.Button("Проверить файл…", () => Dialogs.OpenFile(this, AssetImportDialog.CheckTitle, path => AssetImportDialog.Open(this, Ctx, path, null, checkOnly: true), Formats),
            "Анализ файла по требованиям без создания ресурса");
        checkButton.Name = "CheckAssetFile";
        // Above the library list (the first column of the panel).
        if (GetChild(0) is VBoxContainer left)
        {
            left.AddChild(importButton);
            left.AddChild(checkButton);
        }
    }

    private void ImportFile()
    {
        Dialogs.OpenFile(this, AssetImportDialog.Title, path => AssetImportDialog.Open(this, Ctx, path, def =>
        {
            RefreshList();
            Select(def);
            // After the import window has closed (one exclusive window at a time).
            Callable.From(() =>
            {
                string clips = def.Clips.Count > 0 ? $"\nАнимации в файле: {string.Join(", ", def.Clips)}\nРоли: {string.Join(", ", def.Animations.Select(a => $"{Names.Of(a.Role)} → {a.Clip}"))}" : "";
                Dialogs.Info(this, "Ресурс импортирован", $"{def.DisplayName} [{def.Id}] — {Names.Of(def.Kind)}, {def.Size / 1024.0:0.#} КБ\n{def.Path}{clips}");
            }).CallDeferred();
        }), Formats, "*.glb, *.gltf, *.fbx, *.obj ; Модели", "*.png, *.jpg, *.jpeg, *.webp, *.tga, *.hdr, *.exr ; Изображения", "*.ogg, *.wav, *.mp3 ; Звуки");
    }

    protected override Control BuildDetail(Def d)
    {
        var a = (AssetDef)d;
        var root = Ui.HBox(10).Expand(true, true);
        var grid = new PropertyGrid();
        grid.SetTarget(a, Ctx.Db);
        grid.Changed += () => OnEdited(a);
        root.AddChild(grid.Expand(true));

        var side = Ui.VBox(6).MinSize(370);
        _problem = Ui.Label("", 13, Ui.Bad, wrap: true);
        side.AddChild(_problem);
        var problem = a.HasFile ? AssetValidator.FileProblem(a, Assets.Read(a.Path)) : null;
        _problem.Text = problem != null ? "⚠ " + problem + " — в игре будет использовано базовое представление" : Assets.Problems.GetValueOrDefault(a.Id, "");
        _problem.Name = "AssetProblem";
        if (!string.IsNullOrEmpty(a.Analysis)) side.AddChild(Ui.Label("Анализ: " + a.Analysis, 12, Ui.TextDim, wrap: true));
        // What the renderer does not draw in the current mode — said plainly (TZ §11.3).
        var unsupported = Assets.Unsupported(a);
        if (unsupported.Count > 0)
        {
            var u = Ui.Label(string.Join("\n", unsupported.Select(x => "ℹ " + x)), 12, Ui.Warn, wrap: true);
            u.Name = "AssetUnsupported";
            side.AddChild(u);
        }
        _stats = Ui.Label("", 12, Ui.TextDim, wrap: true);
        side.AddChild(_stats);
        _stage = new PreviewStage();
        side.AddChild(_stage);
        var buttons = new HFlowContainer();
        side.AddChild(buttons);
        side.AddChild(Ui.Label("Предпросмотр. Сцены Godot и скрипты не импортируются; файлы проверяются по сигнатуре и контрольной сумме.", 12, Ui.TextDim, wrap: true));
        root.AddChild(side);
        AfterLayout(_stage, () => Preview(a, buttons));
        return root;
    }

    private void Preview(AssetDef a, HFlowContainer buttons)
    {
        if (_stage == null) return;
        _stage.Clear();
        Ui.ClearChildren(buttons);
        switch (a.Kind)
        {
            case AssetKind.Model:
            {
                var model = Assets.Model(a);
                if (model == null)
                {
                    if (_problem != null) _problem.Text = "⚠ " + Assets.Problems.GetValueOrDefault(a.Id, "модель не загружена") + " — используется базовая фишка";
                    _stage.Root.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.25f, BottomRadius = 0.3f, Height = 0.6f }, Position = new Vector3(0, 0.3f, 0) });
                    return;
                }
                // What the engine read (for FBX the only place where geometry is counted).
                var (tris, bones, engineClips) = Assets.EngineStats(model);
                if (_stats != null)
                    _stats.Text = $"Движок: треугольников {tris:N0}, костей {bones}, анимаций {engineClips.Count}" +
                                  (tris > Ctx.Db.Rules.Assets.WarnTriangles ? $" — больше {Ctx.Db.Rules.Assets.WarnTriangles:N0}, задайте упрощённую модель" : "");
                var holder = new Node3D { Position = new Vector3(0, (float)a.YOffset, 0), RotationDegrees = new Vector3(0, (float)a.RotationY, 0), Scale = Vector3.One * (float)a.Scale };
                holder.AddChild(model);
                _stage.Root.AddChild(holder);
                var player = Assets.PlayerOf(model);
                foreach (var role in Enum.GetValues<AnimRole>())
                {
                    var map = a.Animations.FirstOrDefault(m => m.Role == role);
                    string? clip = map?.Clip ?? (player == null ? null : AssetValidator.GuessClip(role, player.GetAnimationList()));
                    var b = Ui.Button(Names.Of(role) + (clip == null ? " (процедурно)" : ""), () =>
                    {
                        if (player != null && clip != null && player.HasAnimation(clip)) player.Play(clip, -1, (float)(map?.Speed ?? 1));
                    }, clip == null ? "Клипа нет — в бою роль анимируется процедурно" : "Клип: " + clip);
                    b.Disabled = clip == null || player == null;
                    buttons.AddChild(b);
                }
                break;
            }
            case AssetKind.Texture or AssetKind.Sprite or AssetKind.Icon:
            {
                var tex = Assets.Texture(a);
                if (tex == null) return;
                var plane = new MeshInstance3D
                {
                    Mesh = new QuadMesh { Size = new Vector2(1.2f, 1.2f) },
                    MaterialOverride = new StandardMaterial3D { AlbedoTexture = tex, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                        BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled },
                    Position = new Vector3(0, 0.7f, 0),
                };
                _stage.Root.AddChild(plane);
                break;
            }
            case AssetKind.Material:
            {
                var mat = Assets.Material(a, Ctx.Db);
                _stage.Root.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.45f, Height = 0.9f }, MaterialOverride = mat, Position = new Vector3(0, 0.5f, 0) });
                break;
            }
            case AssetKind.Sound:
                buttons.AddChild(Ui.Button("▶ Прослушать", () =>
                {
                    var stream = Assets.Sound(a);
                    if (stream == null)
                    {
                        if (_problem != null) _problem.Text = "⚠ " + Assets.Problems.GetValueOrDefault(a.Id, "звук не загружен");
                        return;
                    }
                    var p = new AudioStreamPlayer { Stream = stream, VolumeDb = (float)a.VolumeDb };
                    AddChild(p);
                    p.Finished += p.QueueFree;
                    p.Play();
                }));
                break;
        }
    }
}

/// <summary>«Визуальные эффекты»: presets of particles, light and sound with a preview.</summary>
public partial class VfxPanel : DefListPanel
{
    private PreviewStage? _stage;

    public VfxPanel(EditorContext ctx) : base(ctx, DefCategory.Vfx, "Визуальные эффекты", null, () => new VfxDef { Name = "Новый эффект" })
    {
    }

    protected override Control BuildDetail(Def d)
    {
        var v = (VfxDef)d;
        var root = Ui.HBox(10).Expand(true, true);
        var grid = new PropertyGrid();
        grid.SetTarget(v, Ctx.Db);
        grid.Changed += () => OnEdited(v);
        root.AddChild(grid.Expand(true));
        var side = Ui.VBox(6).MinSize(370);
        _stage = new PreviewStage();
        side.AddChild(_stage);
        side.AddChild(Ui.Button("▶ Показать", () => Play(v)));
        side.AddChild(Ui.Label("Эффект проигрывается в бою при событиях (навыки, состояния, эффекты клеток, ловушки, разрушение, смена местности, падение) и операцией «Визуальный эффект».", 12, Ui.TextDim, wrap: true));
        root.AddChild(side);
        AfterLayout(_stage, () => Play(v));
        return root;
    }

    private void Play(VfxDef v)
    {
        if (_stage == null || !_stage.IsInsideTree()) return;
        Render.Vfx.Play(_stage.Root, v, new Vector3(0, 0.1f, 0), Ctx.Db);
    }
}

/// <summary>
/// «Требования к ресурсам» (TZ §16): the page built from the limits of the rules — formats, sizes, models, textures,
/// materials, skeletons, markers, attachment points, performance, naming, examples, common mistakes — with export to
/// TXT and HTML (PDF — by printing the HTML), the file check and the summary of the resources of the content.
/// </summary>
public partial class RequirementsPanel : VBoxContainer
{
    private readonly EditorContext _ctx;

    public RequirementsPanel(EditorContext ctx)
    {
        _ctx = ctx;
    }

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 8);
        var limits = _ctx.Db.Rules.Assets;
        var bar = new HFlowContainer();
        AddChild(bar);
        bar.AddChild(Ui.Label("Требования к ресурсам", 18, Ui.Accent));
        bar.AddChild(Ui.Button("Экспорт TXT…", () => Save("requirements.txt", AssetRequirements.ToText(limits), "*.txt ; Текст")).Also(b => b.Name = "ExportRequirementsTxt"));
        bar.AddChild(Ui.Button("Экспорт HTML…", () => Save("requirements.html", AssetRequirements.ToHtml(limits), "*.html ; HTML")).Also(b => b.Name = "ExportRequirementsHtml"));
        bar.AddChild(Ui.Button("Проверить файл…", () => Dialogs.OpenFile(this, AssetImportDialog.CheckTitle,
            path => AssetImportDialog.Open(this, _ctx, path, null, checkOnly: true), AssetPanel.Formats)).Also(b => b.Name = "RequirementsCheckFile"));
        AddChild(Ui.Label("Лимиты — в «Правила боя» → «Ресурсы: лимиты»; страница и проверка при импорте берут их оттуда.", 12, Ui.TextDim, wrap: true));

        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }.Expand(true, true);
        AddChild(scroll);
        var body = Ui.VBox(4).Expand(true);
        scroll.AddChild(body);

        // The summary of the content's resources (the debug view of the visual layer).
        var report = AssetReport.Build(_ctx.Db, Assets.Read);
        body.AddChild(Ui.Label("Ресурсы контента", 15, Ui.Accent));
        var summary = Ui.Label(string.Join("\n", report.Lines()), 13, Ui.Text, wrap: true);
        summary.Name = "AssetSummary";
        body.AddChild(summary);
        if (Assets.Problems.Count > 0)
            body.AddChild(Ui.Label("Ошибки загрузки в игре: " + string.Join("; ", Assets.Problems.Select(kv => $"{kv.Key}: {kv.Value}")), 12, Ui.Warn, wrap: true));
        if (Assets.PendingCount > 0) body.AddChild(Ui.Label($"Моделей в очереди фоновой загрузки: {Assets.PendingCount}", 12, Ui.TextDim));
        body.AddChild(Ui.Sep());

        foreach (var sec in AssetRequirements.Build(limits))
        {
            body.AddChild(Ui.Label(sec.Title, 15, Ui.Accent));
            foreach (var line in sec.Lines) body.AddChild(Ui.Label("• " + line, 13, Ui.Text, wrap: true));
            if (sec.Table is { Count: > 0 } table)
            {
                var grid = new GridContainer { Columns = table[0].Length, Name = "RequirementsTable" };
                grid.AddThemeConstantOverride("h_separation", 18);
                for (int i = 0; i < table.Count; i++)
                    foreach (var c in table[i])
                        grid.AddChild(Ui.Label(c, 13, i == 0 ? Ui.TextDim : Ui.Text));
                body.AddChild(grid);
            }
        }
    }

    private void Save(string name, string text, string filter)
    {
        Dialogs.SaveFile(this, "Экспорт требований", name, path =>
        {
            try
            {
                File.WriteAllText(path, text, new System.Text.UTF8Encoding(true));
                Dialogs.Info(this, "Экспорт требований", "Сохранено: " + path);
            }
            catch (Exception e)
            {
                Dialogs.Info(this, "Экспорт требований", e.Message);
            }
        }, filter);
    }
}
